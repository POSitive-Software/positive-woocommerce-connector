using Microsoft.Extensions.Logging;
using PA.Connectors.Core;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Pos;
using PA.Connectors.Core.Stores;
using PA.Connectors.Host;
using Xunit;

namespace PA.Tests.Connectors;

public class ConnectorWorkerTests
{
    private sealed class FakeConfig(ConnectorsDocument? document, string path = "C:\\nowhere\\connectors.json") : IConnectorConfigSource
    {
        public ConnectorsDocument? Document { get; set; } = document;
        public bool Throw { get; set; }
        public string Path { get; } = path;
        public object? Version() => Document is null && !Throw ? null : (Document?.Connectors.Count ?? -1, Throw);

        public ConnectorsDocument Load()
        {
            if (Throw)
                throw new System.Text.Json.JsonException("bad json");
            return Document ?? new ConnectorsDocument();
        }
    }

    /// <summary>Any store touch fails the test: an idle worker must make none.</summary>
    private sealed class UntouchableJobStore : IConnectorJobStore
    {
        public Task<EnqueueResult> EnqueueAsync(JobRequest request, CancellationToken ct = default) => throw Touched();
        public Task<ConnectorJob?> ClaimNextAsync(string connectorId, TimeSpan lease, CancellationToken ct = default) => throw Touched();
        public Task CompleteAsync(long jobId, CancellationToken ct = default) => throw Touched();
        public Task<JobFailureOutcome> FailAsync(ConnectorJob job, string error, bool retryable, JobRetryPolicy policy, CancellationToken ct = default) => throw Touched();
        public Task<int> CountOpenAsync(string connectorId, CancellationToken ct = default) => throw Touched();
        public Task<int> PurgeDoneAsync(DateTime olderThanUtc, CancellationToken ct = default) => throw Touched();
        public Task<DateTime?> LastCompletedAtAsync(string connectorId, string kind, CancellationToken ct = default) => throw Touched();
        public Task<IReadOnlyList<string>> ListStrandedAsync(string connectorId, string kind, int max = 100, CancellationToken ct = default) => throw Touched();
        private static Exception Touched() => new InvalidOperationException("the job store must not be touched");
    }

    /// <summary>Records which connectors got a POSitive API client (one per connector, its own key).</summary>
    private sealed class RecordingPosFactory : IPosGatewayFactory
    {
        public List<(string ConnectorId, IPosGateway Gateway)> Created { get; } = [];

        public IPosGateway Create(ConnectorSettings settings)
        {
            var gateway = new PosApiClient(new HttpClient(), new PosApiClientOptions { BaseUrl = PosApiEndpoint.Default.BaseUrl },
                () => "unused");
            Created.Add((settings.Id, gateway));
            return gateway;
        }
    }

    private sealed class RecordingConnector(ConnectorSettings settings) : IConnector
    {
        public List<ConnectorJob> Handled { get; } = [];
        public ConnectorSettings Settings { get; } = settings;
        public IReadOnlyList<RecurringJob> RecurringJobs { get; } = [new RecurringJob(ConnectorJobKinds.CatalogPush, TimeSpan.FromMinutes(1))];

        public Task HandleAsync(ConnectorJob job, CancellationToken ct)
        {
            Handled.Add(job);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingFactory : IConnectorFactory
    {
        public List<RecordingConnector> Created { get; } = [];
        public List<ConnectorServices> Services { get; } = [];
        public string Platform => ConnectorPlatforms.WooCommerce;

        public IConnector Create(ConnectorServices services)
        {
            Services.Add(services);
            var connector = new RecordingConnector(services.Settings);
            Created.Add(connector);
            return connector;
        }
    }

    private sealed class ListLogger : ILogger, ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception)));
        }

        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
    }

    private static ConnectorSettings Enabled(string id = "shop1") => new()
    {
        Id = id, Enabled = true, StoreUrl = "https://shop.example.com",
    };

    private readonly ListLogger _log = new();
    private readonly RecordingPosFactory _pos = new();
    private DateTime _now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTime(Func<DateTime> now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now(), TimeSpan.Zero);
    }

    private ConnectorWorker Worker(IConnectorConfigSource config, IConnectorJobStore jobs, IConnectorFactory? factory = null)
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(_log).SetMinimumLevel(LogLevel.Trace));
        var engine = new ConnectorEngine(_pos,
            new ConnectorStores(jobs, new InMemoryCursorStore(), new InMemoryExceptionStore(), null!, null!));
        return new ConnectorWorker(config, factory is null ? [] : [factory], engine, loggerFactory,
            new ConnectorWorkerOptions { Tick = TimeSpan.FromMilliseconds(20) }, new FixedTime(() => _now));
    }

    [Fact]
    public async Task NoConfigFile_IdlesWithoutTouchingAnything_AndSaysSoOnce()
    {
        var worker = Worker(new FakeConfig(null), new UntouchableJobStore());

        for (var i = 0; i < 3; i++)
            await worker.TickAsync(CancellationToken.None);

        Assert.StartsWith("idle: no enabled connectors", worker.State);
        Assert.Empty(_pos.Created); // no POSitive API client, so no API call
        Assert.Single(_log.Entries, e => e.Message.Contains("connector worker idle"));
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task OnlyDisabledConnectors_AlsoIdle()
    {
        var worker = Worker(new FakeConfig(new ConnectorsDocument { Connectors = [Enabled() with { Enabled = false }] }),
            new UntouchableJobStore());

        await worker.TickAsync(CancellationToken.None);

        Assert.StartsWith("idle", worker.State);
        Assert.Empty(worker.RunningConnectorIds);
    }

    [Fact]
    public async Task HostedService_StartsAndStopsCleanly_WithNoConfig()
    {
        var worker = Worker(new FakeConfig(null), new UntouchableJobStore());

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(150); // several ticks
        await worker.StopAsync(CancellationToken.None);

        Assert.StartsWith("idle", worker.State);
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task EnabledConnector_SchedulesAndDrainsItsQueue_WithItsOwnApiClient()
    {
        var jobs = new InMemoryJobStore(() => _now);
        var factory = new RecordingFactory();
        var worker = Worker(new FakeConfig(new ConnectorsDocument { Connectors = [Enabled()] }), jobs, factory);
        await jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.OrderImport, "58"));

        await worker.TickAsync(CancellationToken.None);

        var connector = Assert.Single(factory.Created);
        Assert.Equal(["order.import:58", "catalog.push:"], connector.Handled.Select(j => $"{j.Kind}:{j.ResourceKey}"));
        Assert.All(jobs.Jobs, j => Assert.Equal(ConnectorJobStatuses.Done, j.Status));
        var (id, gateway) = Assert.Single(_pos.Created);
        Assert.Equal("shop1", id);
        Assert.Same(gateway, factory.Services.Single().Pos);
        Assert.Equal("running 1 connector(s)", worker.State);
        Assert.Equal(["shop1"], worker.RunningConnectorIds);

        // Same config next tick: the runner is reused, not rebuilt.
        await worker.TickAsync(CancellationToken.None);
        Assert.Single(factory.Created);
    }

    [Fact]
    public async Task ConfigChange_RebuildsRunners_AndDisablingStopsThem()
    {
        var jobs = new InMemoryJobStore(() => _now);
        var factory = new RecordingFactory();
        var config = new FakeConfig(new ConnectorsDocument { Connectors = [Enabled("shop1")] });
        var worker = Worker(config, jobs, factory);

        await worker.TickAsync(CancellationToken.None);
        config.Document = new ConnectorsDocument { Connectors = [Enabled("shop1"), Enabled("shop2")] };
        await worker.TickAsync(CancellationToken.None);
        Assert.Equal(["shop1", "shop1", "shop2"], factory.Created.Select(c => c.Settings.Id));

        config.Document = null;
        await worker.TickAsync(CancellationToken.None);
        Assert.StartsWith("idle", worker.State);
    }

    [Fact]
    public async Task MalformedConfig_LogsAnError_AndIdles()
    {
        var worker = Worker(new FakeConfig(null) { Throw = true }, new UntouchableJobStore());

        await worker.TickAsync(CancellationToken.None);

        Assert.StartsWith("idle", worker.State);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Could not read"));
    }

    [Fact]
    public async Task InvalidEnabledConnector_IsSkippedWithAnError()
    {
        var factory = new RecordingFactory();
        var worker = Worker(new FakeConfig(new ConnectorsDocument { Connectors = [Enabled() with { PriceLevel = 12 }] }),
            new UntouchableJobStore(), factory);

        await worker.TickAsync(CancellationToken.None);

        Assert.StartsWith("idle", worker.State);
        Assert.Empty(factory.Created);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("priceLevel"));
    }
}
