using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PA.Connectors.Core;
using PA.Connectors.Core.Catalog;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Stores;
using PA.Connectors.WooCommerce;
using PA.Connectors.WooCommerce.Catalog;
using Xunit;

namespace PA.Tests.Connectors.Catalog;

/// <summary>
/// S2 catalog push end to end against an in-memory Woo store (HTTP fake under the real
/// WooClient), a fake POSitive gateway, and the real SQLite item store (its unique Woo-id
/// rule included). No server, no Docker, no database — always runs.
/// </summary>
public sealed class WooCatalogSyncTests : IAsyncLifetime
{
    private const string Shop = "shop1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pa-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc));
    private readonly FakeWooStore _woo = new();
    private readonly FakePos _pos = FakePos.Create();
    private readonly InMemoryExceptionStore _exceptions = new();
    private readonly InMemoryCursorStore _cursors = new();
    private readonly NoOrders _orders = new();
    private ConnectorDatabase _db = null!;
    private IConnectorItemStore _items = null!;
    private ConnectorSettings _settings = new() { Id = Shop, Enabled = true, StoreUrl = "https://shop.example.com" };

    private sealed class TestClock(DateTime start) : TimeProvider
    {
        public DateTime Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    public async Task InitializeAsync()
    {
        _db = new ConnectorDatabase(Path.Combine(_dir, "connectors.db"), _clock);
        await _db.MigrateAsync();
        _items = new SqliteConnectorItemStore(_db);
    }

    public Task DisposeAsync()
    {
        TestDirectory.Delete(_dir);
        return Task.CompletedTask;
    }

    private WooCatalogSync Sync() => new(
        new ConnectorServices(_settings, _pos.Gateway, new InMemoryJobStore(() => _clock.Now), _cursors, _exceptions, _items,
            _orders, NullLoggerFactory.Instance),
        () => new WooClient(new HttpClient(_woo, disposeHandler: false), new WooClientOptions
        {
            StoreUrl = new Uri("https://shop.example.com/"), ConsumerKey = "ck", ConsumerSecret = "cs", MaxRetries = 0, PageSize = 100,
        }, logger: null, delay: (_, _) => Task.CompletedTask),
        _clock);

    /// <summary>No orders; <see cref="Held"/> = the stock holds the push must subtract.</summary>
    private sealed class NoOrders : IConnectorOrderStore
    {
        public Dictionary<int, decimal> Held { get; } = [];
        public Task<ConnectorOrder?> GetAsync(string c, long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertAsync(ConnectorOrder order, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TryMarkPaymentPostedAsync(string c, long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<int>> SetStockHoldAsync(string c, long id, string? status, IReadOnlyList<ConnectorStockHoldLine> lines,
            int unresolved, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<int, decimal>> GetStockHoldTotalsAsync(string c, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, decimal>>(new Dictionary<int, decimal>(Held));
        public Task<IReadOnlyList<long>> ListStockHoldOrdersAsync(string c, DateTime before, int max, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static ConnectorJob Job(string kind, string resourceKey = "", string? payload = null) =>
        new(1, Shop, kind, resourceKey, payload, ConnectorJobStatuses.Running, 1, DateTime.UtcNow, null, null, DateTime.UtcNow);

    private List<ConnectorExceptionRow> Open(string kind) =>
        _exceptions.Rows.Where(r => r.Kind == kind && r.Status == ConnectorExceptionStatuses.Open).ToList();

    private async Task<ConnectorItem> Mapped(int invNo) =>
        await _items.GetByInvNoAsync(Shop, invNo) ?? throw new Xunit.Sdk.XunitException($"item {invNo} not mapped");

    private string? Cursor => _cursors.Values.GetValueOrDefault((Shop, ConnectorCursorNames.PosModifiedSince));

    // --- matching -------------------------------------------------------------

    [Fact]
    public async Task Match_LinksSkus_IgnoringCase_IncludingVariationsAndAlternateBarcodes()
    {
        _woo.Simple(16, "CAP-1");
        _woo.Variable(11, "hoodie");
        _woo.Variation(11, 27, "hood-red");
        _woo.Variation(11, 28, "hood-blue");
        _woo.Simple(30, "ALT-9");
        _pos.Add(1001, "cap-1", 18m, 5);
        _pos.Add(1002, "HOOD-RED", 40m, 3);
        _pos.Add(1003, "MAIN-9", 9m, 1).Barcodes.Add("alt-9");

        var summary = await Sync().RunMatchAsync(createMissing: false);

        var cap = await Mapped(1001);
        Assert.Equal((16L, 0L, CatalogItemTypes.Simple), (cap.ExternalId, cap.ExternalParentId, cap.ExternalType));
        var red = await Mapped(1002);
        Assert.Equal((27L, 11L, CatalogItemTypes.Variation), (red.ExternalId, red.ExternalParentId, red.ExternalType));
        Assert.Equal(30, (await Mapped(1003)).ExternalId);
        Assert.Equal(new Dictionary<int, string> { [1001] = "16", [1002] = "27", [1003] = "30" }, _pos.Links);
        Assert.Equal(3, summary.Matched);

        // Woo-only: reviewed, left unmanaged (no write to it); the variable parent itself is not a candidate.
        var unmatched = Assert.Single(Open(CatalogExceptionKinds.UnmatchedPlatform));
        Assert.Equal("28", unmatched.ExternalRef);
        Assert.DoesNotContain(_woo.Updates("variations/batch"), u => (long)u["id"]! == 28);

        // The match also pushes what differs (stock was never managed in Woo).
        Assert.Equal(5, (int)_woo.Find(16)!.StockQuantity!);
        Assert.Equal("40.00", _woo.Find(27)!.RegularPrice);
    }

    [Fact]
    public async Task Match_Ambiguous_LinksNothing_AndCreatesNothing()
    {
        _woo.Simple(40, "dup");
        _woo.Simple(41, "B1");
        _woo.Simple(42, "B2");
        _pos.Add(1004, "DUP", 5m, 1);
        _pos.Add(1005, "dup", 5m, 1);
        var both = _pos.Add(1006, "MAIN-6", 5m, 1);
        both.Barcodes.AddRange(["b1", "b2"]);

        var summary = await Sync().RunMatchAsync();

        Assert.Empty(await _items.ListAsync(Shop));
        Assert.Empty(_pos.Links);
        Assert.Empty(_woo.Creates());
        Assert.Equal(new[] { "40", "41", "42" }, Open(CatalogExceptionKinds.Ambiguous).Select(r => r.ExternalRef).Order());
        Assert.Equal(3, summary.Ambiguous);
    }

    [Fact]
    public async Task Match_NonWebItem_IsNotLinked_AndSaysWhy()
    {
        _woo.Simple(16, "cap-1");
        _pos.Add(1001, "cap-1", 18m, 5).Web = false;

        await Sync().RunMatchAsync();

        Assert.Empty(_pos.Links);
        Assert.Contains("not flagged as a web product", Assert.Single(Open(CatalogExceptionKinds.UnmatchedPlatform)).Reason);
    }

    [Fact]
    public async Task Match_TakesOverAnExistingApiLink_EvenWithoutSku()
    {
        _woo.Simple(16, null);
        _pos.Add(1001, "cap-1", 18m, 5);
        _pos.Links[1001] = "16";

        await Sync().RunMatchAsync();

        Assert.Equal(16, (await Mapped(1001)).ExternalId);
        Assert.Empty(_woo.Creates());
    }

    // --- create -----------------------------------------------------------------

    [Fact]
    public async Task UnmatchedWebItems_AreCreatedAsDrafts_WithTextPricesAndStock_ThenMappedAndLinked()
    {
        var item = _pos.Add(2001, "new-1", 12.5m, 7.9m);
        item.Description = "Short name";
        item.LongDescription = "Long text";
        item.ExtendedDescription = "<p>Web text</p>";
        item.SalePrices[0] = 9.99m;
        _pos.Add(2002, "matrix-1", 10m, 1).MatrixItemCount = 3;
        _pos.Add(2003, "cell-1", 10m, 1).ParentInvNo = 2002;
        _pos.Add(2004, "offline", 10m, 1).Web = false;

        var summary = await Sync().RunMatchAsync();

        var create = Assert.Single(_woo.Creates());
        Assert.Equal("draft", (string?)create["status"]);
        Assert.Equal("simple", (string?)create["type"]);
        Assert.Equal("new-1", (string?)create["sku"]);
        Assert.Equal("Short name", (string?)create["name"]);
        Assert.Equal("<p>Web text</p>", (string?)create["description"]);
        Assert.Equal("12.50", (string?)create["regular_price"]);
        Assert.Equal("9.99", (string?)create["sale_price"]);
        Assert.True((bool)create["manage_stock"]!);
        Assert.Equal(7, (int)create["stock_quantity"]!);

        var created = _woo.Products.Single(p => p.Sku == "new-1");
        var mapped = await Mapped(2001);
        Assert.Equal(created.Id, mapped.ExternalId);
        Assert.NotNull(mapped.LastPushHash);
        Assert.Equal(created.Id.ToString(), _pos.Links[2001]);
        Assert.Equal(1, summary.Created);
        Assert.Equal(new[] { "inv:2002", "inv:2003" }, Open(CatalogExceptionKinds.UnmatchedPos).Select(r => r.ExternalRef).Order());

        // The values sent on create are the push hash: the next push of an unchanged item sends nothing.
        await Sync().PushItemsAsync([2001]); // forced: sends
        _cursors.Values[(Shop, ConnectorCursorNames.PosModifiedSince)] = ConnectorRunnerFormat(_clock.Now);
        var before = _woo.Requests.Count;
        item.ModifiedAt = _clock.Now.AddMinutes(1);
        _clock.Now = _clock.Now.AddMinutes(2);
        await Sync().PushChangesAsync();
        Assert.Equal(0, _woo.Requests.Skip(before).Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task Create_SkulessWebItem_IsNotCreated_ButRaisedForReview()
    {
        var item = _pos.Add(2001, null, 12.5m, 3);
        item.Description = "No sku";
        _pos.Add(2002, "   ", 12.5m, 3);
        _pos.Add(2003, "has-sku", 12.5m, 3);

        var summary = await Sync().RunMatchAsync();

        Assert.Equal("has-sku", (string?)Assert.Single(_woo.Creates())["sku"]);
        Assert.Equal(1, summary.Created);
        Assert.Null(await _items.GetByInvNoAsync(Shop, 2001));
        var review = Open(CatalogExceptionKinds.UnmatchedPos);
        Assert.Equal(new[] { "inv:2001", "inv:2002" }, review.Select(r => r.ExternalRef).Order());
        Assert.All(review, r => Assert.Contains("has no SKU", r.Reason));

        // The same on the "create in Woo" path (S5): reviewed, never created.
        await Sync().CreateInWooAsync([2001]);
        Assert.Single(_woo.Creates());
    }

    [Fact]
    public async Task Create_PublishSetting_IsUsed()
    {
        _settings = _settings with { PublishNewAs = PublishStatuses.Publish };
        _pos.Add(2001, "new-1", 12.5m, 1);

        await Sync().RunMatchAsync();

        Assert.Equal("publish", (string?)Assert.Single(_woo.Creates())["status"]);
    }

    [Fact]
    public async Task Create_IsChunkedBy100_AndAPerItemErrorIsAnException_TheRestAreLinked()
    {
        for (var i = 1; i <= 150; i++)
            _pos.Add(3000 + i, $"bulk-{i}", 1m, 1);
        _woo.FailCreateSkus.Add("bulk-7");
        _woo.FailCreateSkus.Add("bulk-120");

        var summary = await Sync().RunMatchAsync();

        Assert.Equal(2, _woo.BatchRequestCount());
        Assert.Equal(148, summary.Created);
        Assert.Equal(2, summary.CreateFailed);
        Assert.Equal(148, (await _items.ListAsync(Shop)).Count);
        var failed = Open(CatalogExceptionKinds.CreateFailed);
        Assert.Equal(new[] { "inv:3007", "inv:3120" }, failed.Select(r => r.ExternalRef).Order());
        Assert.All(failed, f => Assert.Contains("product_invalid_sku", f.Reason));
        Assert.Null(await _items.GetByInvNoAsync(Shop, 3007));
    }

    [Fact]
    public async Task IncrementalCreate_LinksInsteadWhenWooAlreadyHasTheSku()
    {
        await Sync().PushChangesAsync(); // first push: empty match, cursor set
        _woo.Simple(77, "late-1");
        _pos.Add(4001, "LATE-1", 3m, 2, modifiedAt: _clock.Now.AddSeconds(30));
        _clock.Now = _clock.Now.AddMinutes(1);

        await Sync().PushChangesAsync();

        Assert.Equal(77, (await Mapped(4001)).ExternalId);
        Assert.Empty(_woo.Creates());
    }

    // --- push mapping -------------------------------------------------------------

    private async Task<FakeWooProduct> LinkedAsync(int invNo, string sku, decimal price, decimal live)
    {
        var woo = _woo.Simple(invNo + 10_000, sku, stock: 999);
        var item = _pos.Add(invNo, sku, price, live);
        await _items.UpsertAsync(new ConnectorItem(Shop, invNo, woo.Id, 0, CatalogItemTypes.Simple, sku, null, null));
        _pos.Links[invNo] = woo.Id.ToString();
        _ = item;
        return woo;
    }

    [Fact]
    public async Task Push_MapsPriceLevel_ActiveSale_Buffer_AvailableLive_AndNoTextByDefault()
    {
        _settings = _settings with { PriceLevel = 2, StockBuffer = 3 };
        var woo = await LinkedAsync(5001, "p-1", 1m, 10m);
        var item = _pos.Items.Single(i => i.InvNo == 5001);
        item.Prices[1] = 20.005m;
        item.SalePrices[1] = 15m;
        item.Available = 50; // must NOT be used
        item.Description = "POS name";
        var noSale = await LinkedAsync(5002, "p-2", 1m, 1m);
        _pos.Items.Single(i => i.InvNo == 5002).Prices[1] = 8m; // salePrice = price: no sale

        await Sync().PushItemsAsync([5001, 5002]);

        var update = _woo.Updates().Single(u => (long)u["id"]! == woo.Id);
        Assert.Equal("20.01", (string?)update["regular_price"]);
        Assert.Equal("15.00", (string?)update["sale_price"]);
        Assert.True((bool)update["manage_stock"]!);
        Assert.Equal(7, (int)update["stock_quantity"]!);
        Assert.Null(update["name"]);
        Assert.Null(update["description"]);
        var second = _woo.Updates().Single(u => (long)u["id"]! == noSale.Id);
        Assert.Equal("", (string?)second["sale_price"]);
        Assert.Equal(0, (int)second["stock_quantity"]!); // 1 − 3 → never below 0
    }

    [Fact]
    public async Task Push_OverwriteTitleDescription_SendsThem()
    {
        _settings = _settings with { OverwriteTitleDescription = true };
        var woo = await LinkedAsync(5001, "p-1", 4m, 1m);
        _pos.Items[0].Description = "POS name";
        _pos.Items[0].LongDescription = "POS long";

        await Sync().PushItemsAsync([5001]);

        var update = Assert.Single(_woo.Updates());
        Assert.Equal("POS name", (string?)update["name"]);
        Assert.Equal("POS long", (string?)update["description"]);
        Assert.Equal("POS name", woo.Name);
    }

    [Fact]
    public async Task Push_StoreDecimals_AreUsed()
    {
        _woo.Decimals = 0;
        await LinkedAsync(5001, "p-1", 18.5m, 1m);

        await Sync().PushItemsAsync([5001]);

        Assert.Equal("19", (string?)Assert.Single(_woo.Updates())["regular_price"]);
    }

    [Fact]
    public async Task Push_Variations_GoToTheParentsVariationBatch()
    {
        _woo.Variable(11, "hoodie");
        _woo.Variation(11, 27, "hood-red", stock: 1);
        _pos.Add(1002, "hood-red", 40m, 6);
        await _items.UpsertAsync(new ConnectorItem(Shop, 1002, 27, 11, CatalogItemTypes.Variation, "hood-red", null, null));

        await Sync().PushItemsAsync([1002]);

        Assert.Equal(1, _woo.BatchRequestCount("products/11/variations/batch"));
        Assert.Equal(0, _woo.BatchRequestCount("products/batch"));
        Assert.Equal(6, _woo.Find(27)!.StockQuantity);
    }

    // --- change feed, hash, cursor ---------------------------------------------------

    [Fact]
    public async Task ChangeFeed_PushesOnlyChangedHashes_ReadsWithOverlap_AndAdvancesTheCursor()
    {
        var a = await LinkedAsync(6001, "a", 5m, 5m);
        var b = await LinkedAsync(6002, "b", 5m, 5m);
        var t0 = _clock.Now;
        await Sync().PushChangesAsync(); // no cursor: full match + compare → both pushed (stock 999 in Woo)
        Assert.Equal(ConnectorRunnerFormat(t0), Cursor);
        Assert.Equal(2, _woo.Updates().Count);

        // b changes (price) and a is "touched" without a real change, inside the overlap window.
        _clock.Now = t0.AddMinutes(1);
        var itemA = _pos.Items.Single(i => i.InvNo == 6001);
        var itemB = _pos.Items.Single(i => i.InvNo == 6002);
        itemA.ModifiedAt = t0.AddSeconds(-30); // before the cursor, but the overlap re-reads it
        itemB.ModifiedAt = t0.AddSeconds(20);
        itemB.Prices[0] = 6m;
        var before = _woo.Updates().Count;

        await Sync().PushChangesAsync();

        var query = _pos.Queries.Last(q => q.ModifiedSince is not null);
        Assert.Equal(t0 - WooCatalogSync.ChangeFeedOverlap, query.ModifiedSince);
        Assert.True(query.WebstoreOnly);
        var pushed = _woo.Updates().Skip(before).ToList();
        Assert.Equal(b.Id, (long)Assert.Single(pushed)["id"]!);
        Assert.Equal("6.00", b.RegularPrice);
        Assert.Equal(ConnectorRunnerFormat(t0.AddMinutes(1)), Cursor);
        _ = a;
    }

    [Fact]
    public async Task ChangeFeed_StopsOnAShortPage_NotOnTotalItems()
    {
        await Sync().PushChangesAsync();
        for (var i = 1; i <= 250; i++)
            _pos.Add(7000 + i, $"f-{i}", 1m, 1, modifiedAt: _clock.Now.AddSeconds(1));
        _clock.Now = _clock.Now.AddMinutes(1);
        _pos.Queries.Clear();

        await Sync().PushChangesAsync();

        // totalItems claims 10,000+ more: paging still ends at the first short page (200 + 50).
        Assert.Equal(2, _pos.Queries.Count(q => q.ModifiedSince is not null));
    }

    [Fact]
    public async Task ChangeFeed_TransientWooFailure_LeavesTheCursor_SoNothingIsSkipped()
    {
        await LinkedAsync(6001, "a", 5m, 5m);
        await Sync().PushChangesAsync();
        var cursor = Cursor;
        _pos.Items[0].Prices[0] = 7m;
        _pos.Items[0].ModifiedAt = _clock.Now.AddSeconds(5);
        _clock.Now = _clock.Now.AddMinutes(1);
        _woo.BatchStatus = HttpStatusCode.ServiceUnavailable;

        var ex = await Assert.ThrowsAsync<WooApiException>(() => Sync().HandleAsync(Job(ConnectorJobKinds.CatalogPush), default));

        Assert.True(ex.Transient);
        Assert.Equal(cursor, Cursor);

        _woo.BatchStatus = null;
        await Sync().HandleAsync(Job(ConnectorJobKinds.CatalogPush), default);
        Assert.Equal("7.00", _woo.Products.Single().RegularPrice);
    }

    [Fact]
    public async Task FirstPush_WithAnEmptyItemStore_RunsTheMatch()
    {
        _woo.Simple(16, "cap-1");
        _pos.Add(1001, "CAP-1", 18m, 5);

        await Sync().HandleAsync(Job(ConnectorJobKinds.CatalogPush), default);

        Assert.Equal(16, (await Mapped(1001)).ExternalId);
        Assert.NotNull(Cursor);
        Assert.Equal("2026-10-08", _cursors.Values[(Shop, WooCatalogSync.NightlyCursor)]);
    }

    // --- Woo stock holds (orders Woo holds stock for that POSitive does not have) ----

    [Fact]
    public async Task Push_SubtractsWooHolds_TheSameWayForPayloadChangeFeedAndReconcile()
    {
        _settings = _settings with { StockBuffer = 1 };
        var a = await LinkedAsync(6001, "a", 5m, 10m);
        var b = await LinkedAsync(6002, "b", 5m, 10m);
        _orders.Held[6001] = 3m;   // an on-hold Woo order of 3 that is not imported
        _orders.Held[6002] = 2.5m; // fractional: the floor still applies once

        await Sync().PushItemsAsync([6001, 6002]); // payload push (after an order / a hold change)
        Assert.Equal(6, a.StockQuantity);          // ⌊10 − 1 − 3⌋
        Assert.Equal(6, b.StockQuantity);          // ⌊10 − 1 − 2.5⌋

        // Reconcile agrees with the payload push (nothing to fix) ...
        var before = _woo.Updates().Count;
        await Sync().ReconcileAsync();
        Assert.Equal(before, _woo.Updates().Count);

        // ... and puts the hold back when a push without it (or Woo by hand) moved the number.
        a.StockQuantity = 9;
        await Sync().ReconcileAsync();
        Assert.Equal(6, a.StockQuantity);

        // A hold that ends changes the value, so even the hash-skipping change feed pushes it.
        _cursors.Values[(Shop, ConnectorCursorNames.PosModifiedSince)] = ConnectorRunnerFormat(_clock.Now);
        _orders.Held.Remove(6001);
        _pos.Items.Single(i => i.InvNo == 6001).ModifiedAt = _clock.Now.AddMinutes(1);
        _clock.Now = _clock.Now.AddMinutes(2);
        await Sync().PushChangesAsync();
        Assert.Equal(9, a.StockQuantity);
    }

    [Fact]
    public void Mapper_StockIsAvailableLiveLessBufferLessHeld_NeverBelowZero()
    {
        var product = new PA.Connectors.Core.Pos.PosProduct(1, null, "x", "x", null, null, true, 99m, 0m, 0m,
            [new PA.Connectors.Core.Pos.PosPrice(1, 5m, null)], 0, AvailableLive: 10m);
        var settings = _settings with { StockBuffer = 2 };
        Assert.Equal(8, CatalogMapper.Map(product, settings, 2, false).Values!.StockQuantity);
        Assert.Equal(5, CatalogMapper.Map(product, settings, 2, false, pendingPlatformQuantity: 3).Values!.StockQuantity);
        Assert.Equal(0, CatalogMapper.Map(product, settings, 2, false, pendingPlatformQuantity: 30).Values!.StockQuantity);
        Assert.Equal(8, CatalogMapper.Map(product, settings, 2, false, pendingPlatformQuantity: -4).Values!.StockQuantity);
    }

    // --- payload push -------------------------------------------------------------

    [Fact]
    public async Task PayloadPush_PushesExactlyThoseItems_EvenWithAnUnchangedHash()
    {
        var a = await LinkedAsync(6001, "a", 5m, 5m);
        await LinkedAsync(6002, "b", 5m, 5m);
        await Sync().PushChangesAsync(); // pushes both, hashes stored
        a.StockQuantity = 3; // Woo moved on its own (an order)
        var before = _woo.Updates().Count;

        await Sync().HandleAsync(Job(ConnectorJobKinds.CatalogPush, "order:58", CatalogPayloads.Write([6001])), default);

        var pushed = _woo.Updates().Skip(before).ToList();
        Assert.Equal(a.Id, (long)Assert.Single(pushed)["id"]!);
        Assert.Equal(5, a.StockQuantity);
        Assert.Contains(_pos.Queries, q => q.InvNo == 6001);
    }

    [Fact]
    public void Payload_ReadsAnyCasing()
    {
        Assert.Equal(new[] { 3, 4 }, CatalogPayloads.ReadInvNos("""{"InvNos":[3,4,3,0]}"""));
        Assert.Equal(new[] { 5 }, CatalogPayloads.ReadInvNos("""{"invNos":[5]}"""));
        Assert.Null(CatalogPayloads.ReadInvNos(null));
        Assert.Throws<ConnectorJobException>(() => CatalogPayloads.ReadInvNos("not json"));
    }

    // --- batching + per-item errors --------------------------------------------------

    [Fact]
    public async Task Push_IsChunkedBy100_AndAPerItemErrorIsAnException_TheRestAreHashed()
    {
        var ids = new List<int>();
        for (var i = 1; i <= 230; i++)
        {
            await LinkedAsync(8000 + i, $"c-{i}", 2m, 1m);
            ids.Add(8000 + i);
        }
        _woo.FailUpdateIds.Add(18_005);

        var summary = await Sync().PushItemsAsync(ids);

        Assert.Equal(3, _woo.BatchRequestCount());
        Assert.Equal(229, summary.Pushed);
        var failed = Assert.Single(Open(ConnectorExceptionKinds.PushFailed));
        Assert.Equal("18005", failed.ExternalRef);
        Assert.Equal("8005", failed.PosRef);
        Assert.Null((await Mapped(8005)).LastPushHash);
        Assert.NotNull((await Mapped(8006)).LastPushHash);

        // Fixed on the next push: the open exception is closed.
        _woo.FailUpdateIds.Clear();
        await Sync().PushItemsAsync([8005]);
        Assert.Empty(Open(ConnectorExceptionKinds.PushFailed));
    }

    [Fact]
    public async Task Push_WithoutAvailableLive_IsRefused_NotPublishedFromAvailable()
    {
        await LinkedAsync(6001, "a", 5m, 5m);
        _pos.Items[0].AvailableLive = null;

        await Sync().PushItemsAsync([6001]);

        Assert.Empty(_woo.Updates());
        Assert.Contains("availableLive", Assert.Single(Open(ConnectorExceptionKinds.PushFailed)).Reason);
    }

    // --- reconcile -------------------------------------------------------------------

    [Fact]
    public async Task Reconcile_FixesDrift_AndSkipsWhatIsInSync()
    {
        var a = await LinkedAsync(6001, "a", 5m, 5m);
        var b = await LinkedAsync(6002, "b", 5m, 5m);
        await Sync().PushChangesAsync();
        b.StockQuantity = 1; // someone edited Woo by hand
        var before = _woo.Updates().Count;

        var summary = await Sync().ReconcileAsync();

        Assert.Equal(b.Id, (long)Assert.Single(_woo.Updates().Skip(before))["id"]!);
        Assert.Equal(5, b.StockQuantity);
        Assert.Equal(1, summary.Unchanged);
        _ = a;
    }

    [Fact]
    public async Task Reconcile_DeletedWooProduct_IsMissing_Unlinked_AndNeverRecreated()
    {
        var a = await LinkedAsync(6001, "a", 5m, 5m);
        await Sync().PushChangesAsync();
        _woo.Products.Remove(a);

        var summary = await Sync().ReconcileAsync();

        Assert.Equal(1, summary.Missing);
        var missing = Assert.Single(Open(CatalogExceptionKinds.PlatformMissing));
        Assert.Equal(a.Id.ToString(), missing.ExternalRef);
        Assert.Equal(CatalogItemTypes.Missing, (await Mapped(6001)).ExternalType);
        Assert.False(_pos.Links.ContainsKey(6001));
        Assert.Empty(_woo.Creates());

        // Changes keep coming: still not pushed, still not re-created.
        _pos.Items[0].ModifiedAt = _clock.Now.AddSeconds(1);
        _clock.Now = _clock.Now.AddMinutes(1);
        await Sync().PushChangesAsync();
        await Sync().ReconcileAsync();
        Assert.Empty(_woo.Creates());
        Assert.Empty(_woo.Products);
    }

    [Fact]
    public async Task Push_ToADeletedProduct_RelinksToTheReMadeOneWithTheSameSku()
    {
        var a = await LinkedAsync(6001, "a", 5m, 5m);
        _woo.Products.Remove(a);
        var remade = _woo.Simple(900, "A");

        var summary = await Sync().PushItemsAsync([6001]);

        Assert.Equal(1, summary.Relinked);
        Assert.Equal(900, (await Mapped(6001)).ExternalId);
        Assert.Equal("900", _pos.Links[6001]);
        Assert.Equal(5, remade.StockQuantity);
        Assert.Empty(Open(CatalogExceptionKinds.PlatformMissing));
    }

    [Fact]
    public async Task Reconcile_DeletedVariableParent_MarksItsVariationsMissing()
    {
        _woo.Variable(11, "hoodie");
        _woo.Variation(11, 27, "hood-red", stock: 1);
        _pos.Add(1002, "hood-red", 40m, 6);
        await _items.UpsertAsync(new ConnectorItem(Shop, 1002, 27, 11, CatalogItemTypes.Variation, "hood-red", null, null));
        _woo.Products.Clear();

        await Sync().PushItemsAsync([1002]);

        Assert.Equal(CatalogItemTypes.Missing, (await Mapped(1002)).ExternalType);
        Assert.Equal("27", Assert.Single(Open(CatalogExceptionKinds.PlatformMissing)).ExternalRef);
    }

    // --- recurring jobs --------------------------------------------------------------

    [Fact]
    public void RecurringJobs_PushEveryInterval_ReconcileCheckedHourly()
    {
        _settings = _settings with { PushIntervalSeconds = 90 };
        var jobs = Sync().RecurringJobs;

        Assert.Contains(jobs, j => j.Kind == ConnectorJobKinds.CatalogPush && j.Interval == TimeSpan.FromSeconds(90));
        Assert.Contains(jobs, j => j.Kind == ConnectorJobKinds.CatalogReconcile && j.Interval == TimeSpan.FromHours(1)
                                   && j.ResourceKey == WooCatalogSync.NightlyResourceKey);
    }

    [Fact]
    public async Task NightlyReconcile_RunsOncePerLocalDay_FromTwoAm_AndOnlyAfterTheFirstMatch()
    {
        _clock.Now = new DateTime(2026, 10, 8, 3, 0, 0, DateTimeKind.Utc);
        var nightly = Job(ConnectorJobKinds.CatalogReconcile, WooCatalogSync.NightlyResourceKey);

        await Sync().HandleAsync(nightly, default); // never matched yet: nothing
        Assert.Empty(_woo.Requests);

        _cursors.Values[(Shop, ConnectorCursorNames.PosModifiedSince)] = "2026-10-07T00:00:00.0000000Z";
        _clock.Now = new DateTime(2026, 10, 9, 1, 30, 0, DateTimeKind.Utc);
        await Sync().HandleAsync(nightly, default); // before 02:00
        Assert.Empty(_woo.Requests);

        _clock.Now = new DateTime(2026, 10, 9, 2, 5, 0, DateTimeKind.Utc);
        await Sync().HandleAsync(nightly, default);
        Assert.NotEmpty(_woo.Requests);
        Assert.Equal("2026-10-09", _cursors.Values[(Shop, WooCatalogSync.NightlyCursor)]);

        var count = _woo.Requests.Count;
        _clock.Now = new DateTime(2026, 10, 9, 3, 5, 0, DateTimeKind.Utc);
        await Sync().HandleAsync(nightly, default); // already ran today
        Assert.Equal(count, _woo.Requests.Count);

        await Sync().HandleAsync(Job(ConnectorJobKinds.CatalogReconcile, "manual"), default); // on demand: runs
        Assert.True(_woo.Requests.Count > count);
    }

    [Fact]
    public async Task WooRejectingTheKeys_IsAPermanentJobFailure()
    {
        var sync = new WooCatalogSync(
            new ConnectorServices(_settings, _pos.Gateway, new InMemoryJobStore(() => _clock.Now), _cursors, _exceptions, _items,
                new NoOrders(), NullLoggerFactory.Instance),
            () => new WooClient(new HttpClient(new Unauthorized()), new WooClientOptions
            {
                StoreUrl = new Uri("https://shop.example.com/"), ConsumerKey = "ck", ConsumerSecret = "cs", MaxRetries = 0,
            }),
            _clock);

        var ex = await Assert.ThrowsAsync<ConnectorJobException>(() => sync.HandleAsync(Job(ConnectorJobKinds.CatalogPush), default));
        Assert.False(ex.Retryable);
    }

    private sealed class Unauthorized : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(new JsonObject { ["code"] = "woocommerce_rest_cannot_view", ["message"] = "no" }.ToJsonString()),
            });
    }

    private static string ConnectorRunnerFormat(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
