using Microsoft.Extensions.Logging.Abstractions;
using PA.Connectors.Core;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Stores;
using Xunit;

namespace PA.Tests.Connectors;

public class ConnectorQueueTests
{
    private DateTime _now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private readonly InMemoryJobStore _jobs;
    private readonly InMemoryCursorStore _cursors = new();

    public ConnectorQueueTests()
    {
        _jobs = new InMemoryJobStore(() => _now);
    }

    private sealed class ScriptedConnector : IConnector
    {
        public ScriptedConnector(Func<ConnectorJob, Task>? handle = null, params RecurringJob[] recurring)
        {
            Handle = handle ?? (_ => Task.CompletedTask);
            RecurringJobs = recurring;
        }

        public Func<ConnectorJob, Task> Handle { get; set; }
        public List<ConnectorJob> Handled { get; } = [];
        public ConnectorSettings Settings { get; } = new() { Id = "shop1", Enabled = true };
        public IReadOnlyList<RecurringJob> RecurringJobs { get; }

        public async Task HandleAsync(ConnectorJob job, CancellationToken cancellationToken)
        {
            Handled.Add(job);
            await Handle(job);
        }
    }

    private sealed class FixedTime(Func<DateTime> now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now(), TimeSpan.Zero);
    }

    private ConnectorRunner Runner(IConnector connector, JobRetryPolicy? policy = null) =>
        new(connector, _jobs, _cursors, NullLogger.Instance, policy, new FixedTime(() => _now));

    // --- retry policy ------------------------------------------------------

    [Fact]
    public void Policy_Default_DoublesFrom30sAndCapsAtAnHour()
    {
        var policy = JobRetryPolicy.Default;
        Assert.Equal(TimeSpan.FromSeconds(30), policy.DelayFor(1));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.DelayFor(2));
        Assert.Equal(TimeSpan.FromMinutes(2), policy.DelayFor(3));
        Assert.Equal(TimeSpan.FromHours(1), policy.DelayFor(12));
        Assert.Equal(TimeSpan.FromHours(1), policy.DelayFor(1000));
    }

    [Fact]
    public void Policy_GivesUpAtMaxAttempts_OrWhenNotRetryable()
    {
        var policy = new JobRetryPolicy(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), MaxAttempts: 3);
        Assert.Equal(new JobFailureDecision(true, TimeSpan.FromSeconds(10)), policy.Decide(1));
        Assert.Equal(new JobFailureDecision(true, TimeSpan.FromSeconds(20)), policy.Decide(2));
        Assert.False(policy.Decide(3).Retry);
        Assert.False(policy.Decide(1, retryable: false).Retry);
    }

    // --- coalescing ---------------------------------------------------------

    [Fact]
    public async Task Enqueue_SamePendingKey_Coalesces_KeepingTheEarlierDueTime()
    {
        var first = await _jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.OrderImport, "58", DueAt: _now.AddMinutes(5)));
        var second = await _jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.OrderImport, "58", "{\"v\":2}", DueAt: _now));
        var otherOrder = await _jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.OrderImport, "59"));
        var otherStore = await _jobs.EnqueueAsync(new JobRequest("shop2", ConnectorJobKinds.OrderImport, "58"));

        Assert.False(first.Coalesced);
        Assert.True(second.Coalesced);
        Assert.Equal(first.JobId, second.JobId);
        Assert.False(otherOrder.Coalesced);
        Assert.False(otherStore.Coalesced);
        Assert.Equal(_now, _jobs.Get(first.JobId).NextAttemptAt);
        Assert.Equal("{\"v\":2}", _jobs.Get(first.JobId).Payload);
        Assert.Equal(3, _jobs.Jobs.Count);
    }

    [Fact]
    public async Task Enqueue_WhileTheSameKeyIsRunning_QueuesANewJob()
    {
        // A delivery that arrives mid-import must not be lost: the running job may
        // have fetched the order before the change.
        var first = await _jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.OrderImport, "58"));
        var claimed = await _jobs.ClaimNextAsync("shop1", TimeSpan.FromMinutes(1));
        var again = await _jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.OrderImport, "58"));

        Assert.Equal(first.JobId, claimed!.JobId);
        Assert.False(again.Coalesced);
        Assert.NotEqual(first.JobId, again.JobId);
    }

    // --- running ------------------------------------------------------------

    [Fact]
    public async Task Drain_Success_CompletesTheJob()
    {
        var connector = new ScriptedConnector();
        var id = (await _jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.OrderImport, "58"))).JobId;

        Assert.Equal(1, await Runner(connector).DrainAsync(10, CancellationToken.None));

        Assert.Equal(ConnectorJobStatuses.Done, _jobs.Get(id).Status);
        Assert.Equal(1, _jobs.Get(id).Attempts);
        Assert.Equal("58", Assert.Single(connector.Handled).ResourceKey);
    }

    [Fact]
    public async Task Drain_TransientFailure_ReschedulesWithBackoff_ThenSucceeds()
    {
        var calls = 0;
        var connector = new ScriptedConnector(_ => ++calls < 3 ? throw new HttpRequestException("store down") : Task.CompletedTask);
        var runner = Runner(connector);
        var id = (await _jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.CatalogPush))).JobId;

        await runner.DrainAsync(10, CancellationToken.None);
        var job = _jobs.Get(id);
        Assert.Equal(ConnectorJobStatuses.Pending, job.Status);
        Assert.Equal(_now.AddSeconds(30), job.NextAttemptAt);
        Assert.Contains("store down", job.LastError);

        // Not due yet: nothing runs.
        _now = _now.AddSeconds(29);
        Assert.Equal(0, await runner.DrainAsync(10, CancellationToken.None));

        _now = _now.AddSeconds(1);
        await runner.DrainAsync(10, CancellationToken.None);
        Assert.Equal(_now.AddSeconds(60), _jobs.Get(id).NextAttemptAt); // second failure: 60 s

        _now = _now.AddSeconds(60);
        await runner.DrainAsync(10, CancellationToken.None);
        Assert.Equal(ConnectorJobStatuses.Done, _jobs.Get(id).Status);
        Assert.Equal(3, _jobs.Get(id).Attempts);
        Assert.Empty(_jobs.Exceptions.Rows);
    }

    [Fact]
    public async Task Drain_OutOfAttempts_DeadLettersIntoTheExceptionsList()
    {
        var policy = new JobRetryPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), MaxAttempts: 2);
        var runner = Runner(new ScriptedConnector(_ => throw new TimeoutException("slow store")), policy);
        var id = (await _jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.OrderImport, "77"))).JobId;

        await runner.DrainAsync(10, CancellationToken.None);
        _now = _now.AddSeconds(1);
        await runner.DrainAsync(10, CancellationToken.None);

        Assert.Equal(ConnectorJobStatuses.Failed, _jobs.Get(id).Status);
        var exception = Assert.Single(_jobs.Exceptions.Rows);
        Assert.Equal(ConnectorExceptionKinds.JobFailed, exception.Kind);
        Assert.Equal("order.import:77", exception.ExternalRef);
        Assert.Equal(id, exception.JobId);
        Assert.Contains("slow store", exception.Reason);
    }

    [Fact]
    public async Task Drain_PermanentFailure_DeadLettersImmediately()
    {
        var runner = Runner(new ScriptedConnector(_ => throw new ConnectorJobException("keys revoked", retryable: false)));
        var id = (await _jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.CatalogPush))).JobId;

        await runner.DrainAsync(10, CancellationToken.None);

        Assert.Equal(ConnectorJobStatuses.Failed, _jobs.Get(id).Status);
        Assert.Equal(1, _jobs.Get(id).Attempts);
        Assert.Equal("keys revoked", Assert.Single(_jobs.Exceptions.Rows).Reason);
    }

    [Fact]
    public async Task Fail_WithANewerPendingDelivery_IsSuperseded_AndCarriesTheAttempts()
    {
        var connector = new ScriptedConnector();
        var runner = Runner(connector);
        connector.Handle = async _ =>
        {
            // A webhook for the same order lands while this import runs, then the import fails.
            await _jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.OrderImport, "58"));
            throw new InvalidOperationException("transient");
        };
        var first = (await _jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.OrderImport, "58"))).JobId;

        await runner.DrainAsync(1, CancellationToken.None);

        Assert.Equal(ConnectorJobStatuses.Done, _jobs.Get(first).Status);
        var newer = _jobs.Jobs.Single(j => j.JobId != first);
        Assert.Equal(ConnectorJobStatuses.Pending, newer.Status);
        Assert.Equal(1, newer.Attempts);
        Assert.Equal(_now.AddSeconds(30), newer.NextAttemptAt);
    }

    [Fact]
    public async Task Claim_OrderImportsAreSingleConsumer_OtherKindsAreNot()
    {
        await _jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.OrderImport, "58"));
        await _jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.OrderImport, "59"));
        await _jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.CatalogPush));

        var a = await _jobs.ClaimNextAsync("shop1", TimeSpan.FromMinutes(5));
        var b = await _jobs.ClaimNextAsync("shop1", TimeSpan.FromMinutes(5));
        var c = await _jobs.ClaimNextAsync("shop1", TimeSpan.FromMinutes(5));

        Assert.Equal(ConnectorJobKinds.OrderImport, a!.Kind);
        Assert.Equal(ConnectorJobKinds.CatalogPush, b!.Kind); // order 59 waits for 58
        Assert.Null(c);

        // Once 58's lease expires (process died), 58 is reclaimed before 59 runs.
        _now = _now.AddMinutes(6);
        var reclaimed = await _jobs.ClaimNextAsync("shop1", TimeSpan.FromMinutes(5));
        Assert.Equal(a.JobId, reclaimed!.JobId);
        Assert.Equal(2, reclaimed.Attempts);
    }

    [Fact]
    public async Task Schedule_EnqueuesRecurringJobsOncePerInterval()
    {
        var connector = new ScriptedConnector(null,
            new RecurringJob(ConnectorJobKinds.CatalogPush, TimeSpan.FromSeconds(60)),
            new RecurringJob(ConnectorJobKinds.OrderPoll, TimeSpan.FromMinutes(5)));
        var runner = Runner(connector);

        Assert.Equal(2, await runner.ScheduleDueAsync(CancellationToken.None));
        Assert.Equal(0, await runner.ScheduleDueAsync(CancellationToken.None));

        _now = _now.AddSeconds(60);
        Assert.Equal(1, await runner.ScheduleDueAsync(CancellationToken.None)); // push again, poll not yet
        // The second push coalesced into the still-pending first one.
        Assert.Single(_jobs.Jobs, j => j.Kind == ConnectorJobKinds.CatalogPush);

        _now = _now.AddMinutes(4);
        Assert.Equal(2, await runner.ScheduleDueAsync(CancellationToken.None));
        Assert.NotNull(_cursors.Values[("shop1", ConnectorCursorNames.LastScheduled(ConnectorJobKinds.OrderPoll))]);
    }

    [Fact]
    public async Task Drain_StopsAtMaxJobs()
    {
        var connector = new ScriptedConnector();
        for (var i = 0; i < 5; i++)
            await _jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.CatalogPush, $"k{i}"));

        Assert.Equal(3, await Runner(connector).DrainAsync(3, CancellationToken.None));
        Assert.Equal(2, await _jobs.CountOpenAsync("shop1"));
    }
}
