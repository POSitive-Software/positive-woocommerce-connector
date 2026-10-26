using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PA.Connectors.Core;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Pos;
using PA.Connectors.Core.Secrets;
using PA.Connectors.Core.Stores;
using PA.Connectors.WooCommerce;
using PA.Connectors.WooCommerce.Orders;

namespace PA.Tests.Connectors.Orders;

/// <summary>
/// One connector's order side, wired as the host wires it but with fakes at
/// both ends: the real <see cref="WooOrderImport"/>, the real WooClient over
/// <see cref="FakeWooStore"/>, the real PosApiClient over <see cref="FakePosApi"/>,
/// and the real SQLite stores in a temp connectors.db. The clock is fixed and
/// the store's zone is UTC, so dates are exact.
/// </summary>
internal sealed class OrderImportHarness : IAsyncDisposable
{
    public const string Shop = "shop1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pa-orders-" + Guid.NewGuid().ToString("N"));
    private long _nextJobId = 1;

    public sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public DateTime Now { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    public sealed class MemorySecrets : IConnectorSecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string value) => Values[name] = value;
        public bool Remove(string name) => Values.Remove(name);
        public IReadOnlyList<string> List() => Values.Keys.ToList();
    }

    private OrderImportHarness(ConnectorSettings settings)
    {
        Settings = settings;
    }

    public ConnectorSettings Settings { get; private set; }
    public FakeWooStore Woo { get; } = new();
    public FakePosApi Pos { get; } = new();
    public MemorySecrets Secrets { get; } = new();
    public FixedClock Clock { get; } = new(new DateTime(2026, 10, 8, 20, 0, 0, DateTimeKind.Utc));
    public ConnectorDatabase Db { get; private set; } = null!;
    public ConnectorStores Stores { get; private set; } = null!;
    public WooOrderImport Import { get; private set; } = null!;

    public static async Task<OrderImportHarness> CreateAsync(Func<ConnectorSettings, ConnectorSettings>? configure = null)
    {
        var settings = new ConnectorSettings { Id = Shop, Enabled = true, StoreUrl = "https://shop.test" };
        var harness = new OrderImportHarness(configure?.Invoke(settings) ?? settings);
        harness.Db = new ConnectorDatabase(Path.Combine(harness._dir, "connectors.db"), harness.Clock);
        await harness.Db.MigrateAsync();
        harness.Stores = ConnectorStores.Sqlite(harness.Db);
        harness.Rebuild();
        return harness;
    }

    /// <summary>Where the import logs (set, then <see cref="Rebuild"/>, to read the log lines).</summary>
    public Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory { get; set; } = NullLoggerFactory.Instance;

    /// <summary>A fresh WooOrderImport over the same stores (what a host restart or config reload does).</summary>
    public void Rebuild(Func<ConnectorSettings, ConnectorSettings>? configure = null)
    {
        if (configure is not null)
            Settings = configure(Settings);
        var pos = new PosApiClient(new HttpClient(Pos), new PosApiClientOptions { BaseUrl = new Uri("http://pos.test"), ConnectorId = Shop },
            () => "connector-key");
        var services = new ConnectorServices(Settings, pos, Stores.Jobs, Stores.Cursors, Stores.Exceptions, Stores.Items,
            Stores.Orders, LoggerFactory);
        Import = new WooOrderImport(services, CreateClient, Secrets, Clock);
    }

    private WooClient CreateClient() =>
        new(new HttpClient(Woo), new WooClientOptions
        {
            StoreUrl = new Uri(Settings.StoreUrl!), ConsumerKey = "ck_test", ConsumerSecret = "cs_test", MaxRetries = 0,
        });

    public Task LinkAsync(long wooId, int invNo, long parentId = 0) =>
        Stores.Items.UpsertAsync(new ConnectorItem(Shop, invNo, wooId, parentId, parentId == 0 ? "simple" : "variation", null, null, null));

    public Task RunAsync(string kind, string resourceKey = "") =>
        Import.HandleAsync(new ConnectorJob(_nextJobId++, Shop, kind, resourceKey, null, ConnectorJobStatuses.Running, 1,
            Clock.Now, Clock.Now.AddMinutes(10), null, Clock.Now), CancellationToken.None);

    public Task ImportAsync(long orderId) => RunAsync(ConnectorJobKinds.OrderImport, orderId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public Task<IReadOnlyList<ConnectorExceptionRow>> OpenExceptionsAsync() => Stores.Exceptions.ListAsync(Shop);

    public Task<ConnectorOrder?> OrderRowAsync(long orderId) => Stores.Orders.GetAsync(Shop, orderId);

    /// <summary>Every job currently claimable (claims them, so call once per assertion).</summary>
    public async Task<List<ConnectorJob>> DrainJobsAsync()
    {
        var jobs = new List<ConnectorJob>();
        while (await Stores.Jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(5)) is { } job)
        {
            jobs.Add(job);
            await Stores.Jobs.CompleteAsync(job.JobId);
        }
        return jobs;
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Delete(_dir);
        return ValueTask.CompletedTask;
    }
}
