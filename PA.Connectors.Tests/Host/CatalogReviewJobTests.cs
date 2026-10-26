using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PA.Connectors.Core;
using PA.Connectors.Core.Catalog;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Stores;
using PA.Connectors.WooCommerce;
using PA.Tests.Connectors.Catalog;
using Xunit;

namespace PA.Tests.Connectors.Host;

/// <summary>
/// S5: the match review's jobs as the worker runs them through <see cref="WooConnector"/> —
/// catalog.match (summary kept for the page), catalog.link (a hand link: PUT link, item map, push;
/// refused when either side is linked elsewhere; a variation found even when the store will not
/// answer products/{id} for it; the order re-imported after a link from its exception) and
/// catalog.create. In-memory Woo store under the real WooClient, fake gateway, real SQLite item store.
/// </summary>
public sealed class CatalogReviewJobTests : IAsyncLifetime
{
    private const string Shop = "shop1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pa-review-" + Guid.NewGuid().ToString("N"));
    private readonly FakeWooStore _woo = new();
    private readonly FakePos _pos = FakePos.Create();
    private readonly InMemoryExceptionStore _exceptions = new();
    private readonly InMemoryCursorStore _cursors = new();
    private InMemoryJobStore _jobs = null!;
    private ConnectorDatabase _db = null!;
    private IConnectorItemStore _items = null!;
    private IConnectorOrderStore _orders = null!;
    private long _nextJob = 100;

    public async Task InitializeAsync()
    {
        _db = new ConnectorDatabase(Path.Combine(_dir, "connectors.db"));
        await _db.MigrateAsync();
        var stores = ConnectorStores.Sqlite(_db);
        _items = stores.Items;
        _orders = stores.Orders;
        _jobs = new InMemoryJobStore(() => DateTime.UtcNow, _exceptions);
    }

    public Task DisposeAsync()
    {
        TestDirectory.Delete(_dir);
        return Task.CompletedTask;
    }

    private WooConnector Connector() => new(
        new ConnectorServices(new ConnectorSettings { Id = Shop, Enabled = true, StoreUrl = "https://shop.example.com" },
            _pos.Gateway, _jobs, _cursors, _exceptions, _items, _orders, NullLoggerFactory.Instance),
        () => new WooClient(new HttpClient(_woo, disposeHandler: false), new WooClientOptions
        {
            StoreUrl = new Uri("https://shop.example.com/"), ConsumerKey = "ck", ConsumerSecret = "cs", MaxRetries = 0, PageSize = 100,
        }));

    private Task RunAsync(string kind, string key, object payload) => Connector().HandleAsync(new ConnectorJob(
        _nextJob++, Shop, kind, key, ReviewJobKinds.Serialize(payload), ConnectorJobStatuses.Running, 1, DateTime.UtcNow, null, null,
        DateTime.UtcNow), CancellationToken.None);

    [Fact]
    public async Task Link_LinksMapsAndPushes_ResolvesTheReviewException_AndReImportsTheOrder()
    {
        _woo.Simple(16, "WOO-CAP", "9.00", stock: 1);
        _pos.Add(2001, "POS-CAP", 12.5m, 7m);
        await _exceptions.RaiseAsync(new ConnectorExceptionInput(Shop, CatalogExceptionKinds.UnmatchedPlatform, "16", "no match"));

        await RunAsync(ReviewJobKinds.CatalogLink, "woo:16", new CatalogLinkPayload(2001, 16, RetryOrderId: 59));

        Assert.Equal("16", _pos.Links[2001]);
        var item = await _items.GetByInvNoAsync(Shop, 2001);
        Assert.Equal((16L, CatalogItemTypes.Simple), (item!.ExternalId, item.ExternalType));
        var update = Assert.Single(_woo.Updates());
        Assert.Equal("12.50", (string?)update["regular_price"]);
        Assert.Equal(7, (int?)update["stock_quantity"]);
        Assert.Equal(ConnectorExceptionStatuses.Resolved, Assert.Single(_exceptions.Rows).Status);
        var retry = Assert.Single(_jobs.Jobs);
        Assert.Equal((ConnectorJobKinds.OrderImport, "59"), (retry.Kind, retry.ResourceKey));
        Assert.Contains("\"kind\":\"catalog.link\"", (await _cursors.GetAsync(Shop, ReviewJobKinds.LastRunCursor))!);
    }

    [Fact]
    public async Task Link_FindsAVariation_WhenTheStoreWillNotServeItAsAProduct()
    {
        _woo.Variable(30, null);
        _woo.Variation(30, 34, "WOO-RED", stock: 2);
        _pos.Add(2002, "POS-RED", 5m, 3m);

        await RunAsync(ReviewJobKinds.CatalogLink, "woo:34", new CatalogLinkPayload(2002, 34));

        var item = await _items.GetByInvNoAsync(Shop, 2002);
        Assert.Equal((34L, 30L, CatalogItemTypes.Variation), (item!.ExternalId, item.ExternalParentId, item.ExternalType));
        Assert.Single(_woo.Updates("products/30/variations/batch"));
    }

    [Fact]
    public async Task Link_IsRefused_WhenEitherSideIsLinkedElsewhere_OrTheWooItemIsMissing()
    {
        _woo.Simple(16, "A");
        _woo.Simple(17, "B");
        _pos.Add(2001, "A", 1m, 1m);
        _pos.Add(2002, "B", 1m, 1m);
        await RunAsync(ReviewJobKinds.CatalogLink, "woo:16", new CatalogLinkPayload(2001, 16));

        await RunAsync(ReviewJobKinds.CatalogLink, "woo:16", new CatalogLinkPayload(2002, 16));   // Woo 16 is 2001's
        await RunAsync(ReviewJobKinds.CatalogLink, "woo:17", new CatalogLinkPayload(2001, 17));   // 2001 has 16
        await RunAsync(ReviewJobKinds.CatalogLink, "woo:99", new CatalogLinkPayload(2002, 99, RetryOrderId: 59));

        // F2: a refused HAND link is an exception (catalog_link_refused), not a match review item.
        var conflicts = _exceptions.Rows.Where(r => r.Kind == CatalogExceptionKinds.ManualLinkRefused).ToList();
        Assert.Equal(new[] { "16", "17", "99" }, conflicts.Select(c => c.ExternalRef).Order().ToArray());
        Assert.Contains("already linked to POSitive item 2001", conflicts.Single(c => c.ExternalRef == "16").Reason);
        Assert.False(CatalogExceptionKinds.IsReview(CatalogExceptionKinds.ManualLinkRefused));
        Assert.DoesNotContain(_exceptions.Rows, r => r.Kind == CatalogExceptionKinds.LinkConflict);
        Assert.Equal(new Dictionary<int, string> { [2001] = "16" }, _pos.Links);
        Assert.Empty(_jobs.Jobs); // no re-import when nothing was linked
    }

    // F4: "Unlink #58 and link" — the confirmed replacing link unlinks both old links first.
    [Fact]
    public async Task Link_WithReplaceExisting_UnlinksWhatEitherSideIsLinkedTo_ThenLinks_AndClosesTheRefusal()
    {
        _woo.Simple(58, "WID-011");
        _woo.Simple(96, "WID-011-B");
        _woo.Simple(97, "OTHER");
        _pos.Add(4198, "WID-011", 1m, 1m);
        _pos.Add(3199, "OTHER", 1m, 1m);
        await RunAsync(ReviewJobKinds.CatalogLink, "woo:58", new CatalogLinkPayload(4198, 58));
        await RunAsync(ReviewJobKinds.CatalogLink, "woo:96", new CatalogLinkPayload(3199, 96));
        await RunAsync(ReviewJobKinds.CatalogLink, "woo:96", new CatalogLinkPayload(4198, 96)); // refused: both sides taken
        Assert.Single(_exceptions.Rows, r => r.Kind == CatalogExceptionKinds.ManualLinkRefused && r.Status == ConnectorExceptionStatuses.Open);

        await RunAsync(ReviewJobKinds.CatalogLink, "woo:96", new CatalogLinkPayload(4198, 96, ReplaceExisting: true));

        Assert.Equal(new Dictionary<int, string> { [4198] = "96" }, _pos.Links);
        Assert.Equal(96L, (await _items.GetByInvNoAsync(Shop, 4198))!.ExternalId);
        Assert.Null(await _items.GetByInvNoAsync(Shop, 3199));   // 3199 lost Woo 96
        Assert.Null(await _items.GetByExternalIdAsync(Shop, 58)); // Woo 58 stays in Woo, unlinked
        Assert.NotNull(_woo.Find(58));
        Assert.All(_exceptions.Rows.Where(r => r.Kind == CatalogExceptionKinds.ManualLinkRefused),
            r => Assert.Equal(ConnectorExceptionStatuses.Resolved, r.Status));
    }

    [Fact]
    public async Task Match_RunsWithTheChosenCreateMode_AndKeepsItsSummaryForThePage()
    {
        _woo.Simple(16, "CAP-1");
        _pos.Add(2001, "cap-1", 10m, 4m);
        _pos.Add(2002, "new-1", 10m, 4m);

        await RunAsync(ReviewJobKinds.CatalogMatch, "ui", new CatalogMatchPayload(CreateMissing: false));

        Assert.Equal("16", _pos.Links[2001]);
        Assert.Empty(_woo.Creates());
        var lastRun = (await _cursors.GetAsync(Shop, ReviewJobKinds.LastRunCursor))!;
        Assert.Contains("\"kind\":\"catalog.match\"", lastRun);
        Assert.Contains("\"matched\":1", lastRun);

        await RunAsync(ReviewJobKinds.CatalogCreate, "inv:2002", new CatalogCreatePayload([2002]));
        Assert.Equal("new-1", (string?)Assert.Single(_woo.Creates())["sku"]);
    }

    [Fact]
    public async Task AReviewJobWithoutItsPayload_FailsPermanently()
    {
        var error = await Assert.ThrowsAsync<ConnectorJobException>(() => Connector().HandleAsync(new ConnectorJob(
            1, Shop, ReviewJobKinds.CatalogLink, "woo:1", null, ConnectorJobStatuses.Running, 1, DateTime.UtcNow, null, null,
            DateTime.UtcNow), CancellationToken.None));

        Assert.False(error.Retryable);
    }
}
