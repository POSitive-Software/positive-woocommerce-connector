using System.Globalization;
using Microsoft.Extensions.Logging;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Stores;

namespace PA.Connectors.Core;

/// <summary>
/// The engine loop for ONE connector: enqueue recurring jobs that are due, then
/// drain the queue one job at a time (claim → handle → complete / fail). The
/// worker calls it on every tick; it is never run concurrently for the same
/// connector in one process, and the store's claim lock covers other processes.
/// </summary>
public sealed class ConnectorRunner
{
    /// <summary>How long a claimed job is leased before another claimer may take it over.</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(10);

    private readonly IConnector _connector;
    private readonly IConnectorJobStore _jobs;
    private readonly IConnectorCursorStore _cursors;
    private readonly JobRetryPolicy? _policy;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lease;
    private readonly ILogger _logger;

    public ConnectorRunner(
        IConnector connector, IConnectorJobStore jobs, IConnectorCursorStore cursors, ILogger logger,
        JobRetryPolicy? policy = null, TimeProvider? time = null, TimeSpan? lease = null)
    {
        _connector = connector;
        _jobs = jobs;
        _cursors = cursors;
        _logger = logger;
        _policy = policy; // null = per kind (JobRetryPolicy.For)
        _time = time ?? TimeProvider.System;
        _lease = lease ?? DefaultLease;
    }

    public string ConnectorId => _connector.Settings.Id;

    /// <summary>
    /// Enqueues each recurring job whose interval has elapsed since it was last
    /// scheduled. The last-scheduled time is a cursor, so a restart does not
    /// fire everything at once. Coalescing makes a double-schedule harmless.
    /// </summary>
    public async Task<int> ScheduleDueAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var enqueued = 0;
        foreach (var recurring in _connector.RecurringJobs)
        {
            var cursorName = ConnectorCursorNames.LastScheduled(recurring.Kind);
            var last = ParseUtc(await _cursors.GetAsync(ConnectorId, cursorName, cancellationToken));
            if (last is not null && now - last.Value < recurring.Interval)
                continue;

            await _jobs.EnqueueAsync(
                new JobRequest(ConnectorId, recurring.Kind, recurring.ResourceKey), cancellationToken);
            await _cursors.SetAsync(ConnectorId, cursorName, FormatUtc(now), cancellationToken);
            enqueued++;
        }
        return enqueued;
    }

    /// <summary>Runs up to <paramref name="maxJobs"/> due jobs, sequentially. Returns how many were claimed.</summary>
    public async Task<int> DrainAsync(int maxJobs, CancellationToken cancellationToken)
    {
        var processed = 0;
        while (processed < maxJobs && !cancellationToken.IsCancellationRequested)
        {
            var job = await _jobs.ClaimNextAsync(ConnectorId, _lease, cancellationToken);
            if (job is null)
                break;
            processed++;
            await RunOneAsync(job, cancellationToken);
        }
        return processed;
    }

    private async Task RunOneAsync(ConnectorJob job, CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["ConnectorId"] = job.ConnectorId,
            ["JobId"] = job.JobId,
            ["JobKind"] = job.Kind,
        });

        string error;
        bool retryable;
        try
        {
            await _connector.HandleAsync(job, cancellationToken);
            await _jobs.CompleteAsync(job.JobId, CancellationToken.None);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down mid-job: leave it leased. The lease expires and the
            // next claim retries it (the attempt counts, so a job that always
            // outlives shutdown still runs out eventually).
            throw;
        }
        catch (ConnectorJobException ex)
        {
            error = ex.Message;
            retryable = ex.Retryable;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            retryable = true;
        }

        var outcome = await _jobs.FailAsync(job, error, retryable, _policy ?? JobRetryPolicy.For(job.Kind), CancellationToken.None);
        if (outcome == JobFailureOutcome.DeadLettered)
            _logger.LogWarning("Connector job {JobKind} {ResourceKey} failed for good after {Attempts} attempt(s): {Error}",
                job.Kind, job.ResourceKey, job.Attempts, error);
        else
            _logger.LogInformation("Connector job {JobKind} {ResourceKey} failed (attempt {Attempts}), {Outcome}: {Error}",
                job.Kind, job.ResourceKey, job.Attempts, outcome, error);
    }

    internal static string FormatUtc(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    internal static DateTime? ParseUtc(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
}
