using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Stores;

namespace PA.Tests.Connectors;

/// <summary>
/// In-memory twin of SqliteConnectorJobStore's contract (coalesce on pending key,
/// claim due/expired-lease, single order.import consumer, fail via the policy,
/// dead-letter into an exception). Driven by an explicit clock so backoff is testable.
/// </summary>
internal sealed class InMemoryJobStore : IConnectorJobStore
{
    private readonly object _gate = new();
    private long _nextId = 1;

    public InMemoryJobStore(Func<DateTime> now, InMemoryExceptionStore? exceptions = null)
    {
        Now = now;
        Exceptions = exceptions ?? new InMemoryExceptionStore();
    }

    public Func<DateTime> Now { get; }
    public InMemoryExceptionStore Exceptions { get; }
    public List<ConnectorJob> Jobs { get; } = [];

    public Task<EnqueueResult> EnqueueAsync(JobRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var due = request.DueAt ?? Now();
            var index = Jobs.FindIndex(j => j.ConnectorId == request.ConnectorId && j.Kind == request.Kind
                                            && j.ResourceKey == request.ResourceKey && j.Status == ConnectorJobStatuses.Pending);
            if (index >= 0)
            {
                var existing = Jobs[index];
                Jobs[index] = existing with
                {
                    Payload = request.Payload ?? existing.Payload,
                    NextAttemptAt = due < existing.NextAttemptAt ? due : existing.NextAttemptAt,
                };
                return Task.FromResult(new EnqueueResult(existing.JobId, true));
            }

            var job = new ConnectorJob(_nextId++, request.ConnectorId, request.Kind, request.ResourceKey, request.Payload,
                ConnectorJobStatuses.Pending, 0, due, null, null, Now());
            Jobs.Add(job);
            return Task.FromResult(new EnqueueResult(job.JobId, false));
        }
    }

    public Task<ConnectorJob?> ClaimNextAsync(string connectorId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var now = Now();
            var orderBusy = Jobs.Any(j => j.ConnectorId == connectorId && j.Kind == ConnectorJobKinds.OrderImport
                                          && j.Status == ConnectorJobStatuses.Running && j.LeaseUntil >= now);
            var next = Jobs
                .Where(j => j.ConnectorId == connectorId)
                .Where(j => (j.Status == ConnectorJobStatuses.Pending && j.NextAttemptAt <= now)
                            || (j.Status == ConnectorJobStatuses.Running && j.LeaseUntil < now))
                .Where(j => !(ConnectorJobKinds.IsSingleConsumer(j.Kind) && orderBusy))
                .OrderBy(j => j.NextAttemptAt).ThenBy(j => j.JobId)
                .FirstOrDefault();
            if (next is null)
                return Task.FromResult<ConnectorJob?>(null);

            var claimed = next with { Status = ConnectorJobStatuses.Running, Attempts = next.Attempts + 1, LeaseUntil = now + lease };
            Replace(claimed);
            return Task.FromResult<ConnectorJob?>(claimed);
        }
    }

    public Task CompleteAsync(long jobId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var job = Jobs.Single(j => j.JobId == jobId);
            if (job.Status == ConnectorJobStatuses.Running)
                Replace(job with { Status = ConnectorJobStatuses.Done, LeaseUntil = null });
        }
        return Task.CompletedTask;
    }

    public async Task<JobFailureOutcome> FailAsync(
        ConnectorJob job, string error, bool retryable, JobRetryPolicy policy, CancellationToken cancellationToken = default)
    {
        var decision = policy.Decide(job.Attempts, retryable);
        lock (_gate)
        {
            var now = Now();
            var sibling = Jobs.FirstOrDefault(j => j.JobId != job.JobId && j.ConnectorId == job.ConnectorId && j.Kind == job.Kind
                                                   && j.ResourceKey == job.ResourceKey && j.Status == ConnectorJobStatuses.Pending);
            if (decision.Retry && sibling is not null)
            {
                Replace(sibling with
                {
                    Attempts = Math.Max(sibling.Attempts, job.Attempts),
                    NextAttemptAt = sibling.NextAttemptAt < now + decision.Delay ? now + decision.Delay : sibling.NextAttemptAt,
                });
                Replace(job with { Status = ConnectorJobStatuses.Done, LeaseUntil = null, LastError = error });
                return JobFailureOutcome.Superseded;
            }
            if (decision.Retry)
            {
                Replace(job with
                {
                    Status = ConnectorJobStatuses.Pending, LeaseUntil = null, LastError = error, NextAttemptAt = now + decision.Delay,
                });
                return JobFailureOutcome.Rescheduled;
            }
            Replace(job with { Status = ConnectorJobStatuses.Failed, LeaseUntil = null, LastError = error });
        }

        await Exceptions.RaiseAsync(new ConnectorExceptionInput(
            job.ConnectorId, ConnectorExceptionKinds.JobFailed, $"{job.Kind}:{job.ResourceKey}", error, JobId: job.JobId),
            cancellationToken);
        return JobFailureOutcome.DeadLettered;
    }

    public Task<int> CountOpenAsync(string connectorId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult(Jobs.Count(j => j.ConnectorId == connectorId
                                                   && j.Status is ConnectorJobStatuses.Pending or ConnectorJobStatuses.Running));
    }

    public Task<int> PurgeDoneAsync(DateTime olderThanUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);

    public Task<DateTime?> LastCompletedAtAsync(string connectorId, string kind, CancellationToken cancellationToken = default) =>
        Task.FromResult<DateTime?>(null);

    public Task<IReadOnlyList<string>> ListStrandedAsync(string connectorId, string kind, int max = 100,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<string>>(Jobs
                .Where(j => j.ConnectorId == connectorId && j.Kind == kind)
                .GroupBy(j => j.ResourceKey)
                .Select(g => g.MaxBy(j => j.JobId)!)
                .Where(j => j.Status == ConnectorJobStatuses.Failed)
                .Select(j => j.ResourceKey).Take(max).ToList());
    }

    public ConnectorJob Get(long jobId)
    {
        lock (_gate)
            return Jobs.Single(j => j.JobId == jobId);
    }

    private void Replace(ConnectorJob job) => Jobs[Jobs.FindIndex(j => j.JobId == job.JobId)] = job;
}

internal sealed class InMemoryExceptionStore : IConnectorExceptionStore
{
    private long _nextId = 1;

    public List<ConnectorExceptionRow> Rows { get; } = [];

    public Task<long> RaiseAsync(ConnectorExceptionInput e, CancellationToken cancellationToken = default)
    {
        lock (Rows)
        {
            var index = Rows.FindIndex(r => r.ConnectorId == e.ConnectorId && r.Kind == e.Kind
                                            && r.ExternalRef == e.ExternalRef && r.Status == ConnectorExceptionStatuses.Open);
            if (index >= 0)
            {
                Rows[index] = Rows[index] with { Reason = e.Reason, Occurrences = Rows[index].Occurrences + 1 };
                return Task.FromResult(Rows[index].ExceptionId);
            }
            var row = new ConnectorExceptionRow(_nextId++, e.ConnectorId, e.Kind, e.ExternalRef, e.PosRef, e.Reason, e.Snapshot,
                ConnectorExceptionStatuses.Open, 1, e.JobId, DateTime.UtcNow, DateTime.UtcNow, null, null);
            Rows.Add(row);
            return Task.FromResult(row.ExceptionId);
        }
    }

    public Task<IReadOnlyList<ConnectorExceptionRow>> ListAsync(string connectorId, string? status = "open", int max = 200,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConnectorExceptionRow>>(
            Rows.Where(r => r.ConnectorId == connectorId && (status is null || r.Status == status)).Take(max).ToList());

    public Task<ConnectorExceptionRow?> GetAsync(long exceptionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Rows.FirstOrDefault(r => r.ExceptionId == exceptionId));

    public Task<bool> CloseAsync(long exceptionId, string status, string resolvedBy, CancellationToken cancellationToken = default)
    {
        var index = Rows.FindIndex(r => r.ExceptionId == exceptionId && r.Status == ConnectorExceptionStatuses.Open);
        if (index < 0)
            return Task.FromResult(false);
        Rows[index] = Rows[index] with { Status = status, ResolvedBy = resolvedBy, ResolvedAt = DateTime.UtcNow };
        return Task.FromResult(true);
    }

    public Task<int> CountOpenAsync(string connectorId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Rows.Count(r => r.ConnectorId == connectorId && r.Status == ConnectorExceptionStatuses.Open));
}

internal sealed class InMemoryCursorStore : IConnectorCursorStore
{
    public Dictionary<(string, string), string?> Values { get; } = [];

    public Task<string?> GetAsync(string connectorId, string cursorName, CancellationToken cancellationToken = default) =>
        Task.FromResult(Values.GetValueOrDefault((connectorId, cursorName)));

    public Task SetAsync(string connectorId, string cursorName, string? value, CancellationToken cancellationToken = default)
    {
        Values[(connectorId, cursorName)] = value;
        return Task.CompletedTask;
    }
}
