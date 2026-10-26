using Microsoft.Data.Sqlite;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Stores;
using Xunit;

namespace PA.Tests.Connectors;

/// <summary>
/// The SQLite stores (connectors.db) against a real temp-file database — no
/// server, no POSitive database, so they always run. The clock is injected, so
/// leases and backoff are exact.
/// </summary>
public sealed class SqliteConnectorStoreTests : IAsyncLifetime
{
    private const string Shop = "shop1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pa-connectors-db-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc));
    private ConnectorDatabase _db = null!;
    private ConnectorStores _stores = null!;

    private sealed class TestClock(DateTime start) : TimeProvider
    {
        public DateTime Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private static readonly JobRetryPolicy Immediate = new(TimeSpan.Zero, TimeSpan.Zero, MaxAttempts: 3);

    public async Task InitializeAsync()
    {
        _db = new ConnectorDatabase(Path.Combine(_dir, "connectors.db"), _clock);
        await _db.MigrateAsync();
        _stores = ConnectorStores.Sqlite(_db);
    }

    public Task DisposeAsync()
    {
        TestDirectory.Delete(_dir);
        return Task.CompletedTask;
    }

    // --- schema ---------------------------------------------------------------

    [Fact]
    public async Task Migrate_CreatesTheFile_InWal_AndIsIdempotent()
    {
        Assert.True(File.Exists(_db.Path));
        Assert.Equal(ConnectorDatabase.LatestVersion, await _db.GetVersionAsync());
        Assert.Equal((ConnectorDatabase.LatestVersion, ConnectorDatabase.LatestVersion), await _db.MigrateAsync());

        await using var connection = new SqliteConnection($"Data Source={_db.Path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string?)await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name LIKE 'Connector%';";
        // Job, Cursor, Item, Order, Exception, StockHold (schema 2), Ignore (schema 3, S5 match review).
        Assert.Equal(7L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Migrate_RefusesADatabaseFromANewerBuild()
    {
        await using (var connection = new SqliteConnection($"Data Source={_db.Path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {ConnectorDatabase.LatestVersion + 1};";
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.MigrateAsync());
        Assert.Contains("newer than this build", error.Message);
    }

    // --- queue ----------------------------------------------------------------

    [Fact]
    public async Task Enqueue_CoalescesPendingKey_ButNotARunningOne()
    {
        var jobs = _stores.Jobs;
        var a = await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.OrderImport, "58", DueAt: _clock.Now.AddMinutes(10)));
        var b = await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.OrderImport, "58", "{\"v\":2}"));
        var otherStore = await jobs.EnqueueAsync(new JobRequest("shop2", ConnectorJobKinds.OrderImport, "58"));
        Assert.False(a.Coalesced);
        Assert.True(b.Coalesced);
        Assert.Equal(a.JobId, b.JobId);
        Assert.False(otherStore.Coalesced);

        // b pulled the due time forward, so it can be claimed now; the newer payload won.
        var claimed = await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(5));
        Assert.Equal(a.JobId, claimed!.JobId);
        Assert.Equal("{\"v\":2}", claimed.Payload);
        Assert.Equal(1, claimed.Attempts);
        Assert.Equal(ConnectorJobStatuses.Running, claimed.Status);
        Assert.Equal(_clock.Now.AddMinutes(5), claimed.LeaseUntil);

        // A delivery that lands mid-import is a new job, not lost into the running one.
        var c = await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.OrderImport, "58"));
        Assert.False(c.Coalesced);
        Assert.Equal(2, await jobs.CountOpenAsync(Shop));
    }

    [Fact]
    public async Task ConcurrentEnqueues_OfOneKey_LeaveOnePendingJob()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            _stores.Jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.OrderImport, "dup"))));

        Assert.Single(results.Select(r => r.JobId).Distinct());
        Assert.Equal(11, results.Count(r => r.Coalesced));
    }

    [Fact]
    public async Task Claim_OrderImportsAreSingleConsumer_EvenWithConcurrentClaimers()
    {
        var jobs = _stores.Jobs;
        await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.OrderImport, "1"));
        await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.OrderImport, "2"));
        await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.CatalogPush));

        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(5))));
        var got = claims.Where(c => c is not null).Select(c => c!).ToList();

        Assert.Single(got, j => j.Kind == ConnectorJobKinds.OrderImport);
        Assert.Single(got, j => j.Kind == ConnectorJobKinds.CatalogPush);
        Assert.Equal(2, got.Count);

        // The first order finishes; the second becomes claimable.
        await jobs.CompleteAsync(got.Single(j => j.Kind == ConnectorJobKinds.OrderImport).JobId);
        var next = await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(5));
        Assert.Equal(ConnectorJobKinds.OrderImport, next!.Kind);
        Assert.Equal("2", next.ResourceKey);
    }

    [Fact]
    public async Task ExpiredLease_IsReclaimed_AndCountsAnAttempt_BeforeTheNextOrder()
    {
        var jobs = _stores.Jobs;
        var first = (await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.OrderImport, "58"))).JobId;
        await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.OrderImport, "59"));

        var claimed = await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(1));
        Assert.Null(await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(1))); // 59 waits behind a live lease

        _clock.Now = _clock.Now.AddMinutes(2); // the process "died" mid-import
        var again = await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(1));

        Assert.Equal(first, claimed!.JobId);
        Assert.Equal(first, again!.JobId);
        Assert.Equal(2, again.Attempts);
    }

    [Fact]
    public async Task Fail_Reschedules_ThenDeadLettersWithAnExceptionRow_InOneTransaction()
    {
        var jobs = _stores.Jobs;
        var id = (await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.OrderImport, "77", "{\"order\":77}"))).JobId;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var job = await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(5));
            Assert.Equal(id, job!.JobId);
            Assert.Equal(attempt, job.Attempts);
            var outcome = await jobs.FailAsync(job, $"boom {attempt}", retryable: true, Immediate);
            Assert.Equal(attempt < 3 ? JobFailureOutcome.Rescheduled : JobFailureOutcome.DeadLettered, outcome);
        }

        Assert.Null(await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(5)));
        Assert.Equal(0, await jobs.CountOpenAsync(Shop));
        var row = Assert.Single(await _stores.Exceptions.ListAsync(Shop));
        Assert.Equal(ConnectorExceptionKinds.JobFailed, row.Kind);
        Assert.Equal("order.import:77", row.ExternalRef);
        Assert.Equal(id, row.JobId);
        Assert.Contains("boom 3", row.Reason);
        Assert.Contains("\"payload\":\"{\\u0022order\\u0022:77}\"", row.Snapshot);
    }

    [Fact]
    public async Task Fail_NotRetryable_DeadLettersAtOnce()
    {
        var jobs = _stores.Jobs;
        await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.CatalogPush));
        var job = await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(5));

        Assert.Equal(JobFailureOutcome.DeadLettered, await jobs.FailAsync(job!, "keys revoked", retryable: false, JobRetryPolicy.Default));
        Assert.Equal(1, await _stores.Exceptions.CountOpenAsync(Shop));
    }

    [Fact]
    public async Task Fail_BacksOff30sThenDoubles_AndIsNotDueUntilThen()
    {
        var jobs = _stores.Jobs;
        await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.CatalogPush));

        var job = await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(5));
        await jobs.FailAsync(job!, "later", retryable: true, JobRetryPolicy.Default);
        _clock.Now = _clock.Now.AddSeconds(29);
        Assert.Null(await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(5)));

        _clock.Now = _clock.Now.AddSeconds(1);
        job = await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(5));
        Assert.Equal(2, job!.Attempts);
        Assert.Equal("later", job.LastError);
        await jobs.FailAsync(job, "later again", retryable: true, JobRetryPolicy.Default);
        _clock.Now = _clock.Now.AddSeconds(59);
        Assert.Null(await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(5)));
        _clock.Now = _clock.Now.AddSeconds(1);
        Assert.NotNull(await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Fail_EightAttemptsByDefault_ThenFailedWithAnException()
    {
        var jobs = _stores.Jobs;
        await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.OrderImport, "9"));

        var outcomes = new List<JobFailureOutcome>();
        for (var i = 0; i < 8; i++)
        {
            _clock.Now = _clock.Now.AddHours(1); // past any backoff
            var job = await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(5));
            outcomes.Add(await jobs.FailAsync(job!, "down", retryable: true, JobRetryPolicy.Default));
        }

        Assert.Equal(Enumerable.Repeat(JobFailureOutcome.Rescheduled, 7).Append(JobFailureOutcome.DeadLettered), outcomes);
        Assert.Equal(1, await _stores.Exceptions.CountOpenAsync(Shop));
    }

    [Fact]
    public async Task Fail_WhenANewerDeliveryIsPending_IsSuperseded_AndCarriesTheAttempts()
    {
        var jobs = _stores.Jobs;
        await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.OrderImport, "58"));
        var running = await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(5));
        var newer = await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.OrderImport, "58"));

        Assert.Equal(JobFailureOutcome.Superseded, await jobs.FailAsync(running!, "transient", true, Immediate));

        var next = await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(5));
        Assert.Equal(newer.JobId, next!.JobId);
        Assert.Equal(2, next.Attempts); // carried 1 over, +1 for this claim
        Assert.Equal(0, await _stores.Exceptions.CountOpenAsync(Shop));
    }

    [Fact]
    public async Task Purge_RemovesOldDoneJobs_AndLastCompletedFeedsHealth()
    {
        var jobs = _stores.Jobs;
        Assert.Null(await jobs.LastCompletedAtAsync(Shop, ConnectorJobKinds.CatalogPush));
        await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.CatalogPush));
        var job = await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(5));
        await jobs.CompleteAsync(job!.JobId);
        var completedAt = _clock.Now;

        Assert.Equal(completedAt, await jobs.LastCompletedAtAsync(Shop, ConnectorJobKinds.CatalogPush));
        Assert.Null(await jobs.LastCompletedAtAsync(Shop, ConnectorJobKinds.OrderImport));
        Assert.Equal(0, await jobs.PurgeDoneAsync(completedAt.AddSeconds(-1)));
        Assert.Equal(1, await jobs.PurgeDoneAsync(completedAt.AddSeconds(1)));
    }

    // --- exceptions -------------------------------------------------------------

    [Fact]
    public async Task Exceptions_OpenRowIsRefreshed_ClosedRowStartsANewOne()
    {
        var store = _stores.Exceptions;
        var input = new ConnectorExceptionInput(Shop, ConnectorExceptionKinds.UnmatchedProduct, "order:58",
            "SKU woo-cap matches no item", PosRef: null, Snapshot: "{}");

        var first = await store.RaiseAsync(input);
        var again = await store.RaiseAsync(input with { Reason = "still unmatched", Snapshot = null });
        Assert.Equal(first, again);
        var row = (await store.GetAsync(first))!;
        Assert.Equal(2, row.Occurrences);
        Assert.Equal("still unmatched", row.Reason);
        Assert.Equal("{}", row.Snapshot); // kept when not replaced
        Assert.Equal(1, await store.CountOpenAsync(Shop));

        Assert.True(await store.CloseAsync(first, ConnectorExceptionStatuses.Ignored, "john"));
        Assert.False(await store.CloseAsync(first, ConnectorExceptionStatuses.Resolved, "john"));
        var third = await store.RaiseAsync(input);
        Assert.NotEqual(first, third);
        var closed = (await store.GetAsync(first))!;
        Assert.Equal("john", closed.ResolvedBy);
        Assert.Equal(_clock.Now, closed.ResolvedAt);
        Assert.Equal(2, (await store.ListAsync(Shop, status: null)).Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = store.CloseAsync(third, "open", "john"); });
    }

    // --- cursors, items, orders ---------------------------------------------------

    [Fact]
    public async Task Cursors_Items_Orders_RoundTrip()
    {
        var cursors = _stores.Cursors;
        Assert.Null(await cursors.GetAsync(Shop, ConnectorCursorNames.PosModifiedSince));
        await cursors.SetAsync(Shop, ConnectorCursorNames.PosModifiedSince, "2026-10-08T12:00:00Z");
        await cursors.SetAsync(Shop, ConnectorCursorNames.PosModifiedSince, "2026-10-08T12:01:00Z");
        Assert.Equal("2026-10-08T12:01:00Z", await cursors.GetAsync(Shop, ConnectorCursorNames.PosModifiedSince));

        var items = _stores.Items;
        await items.UpsertAsync(new ConnectorItem(Shop, 3091, 27, 11, "variation", "woo-hoodie-red", null, null));
        await items.SetPushHashAsync(Shop, 3091, new string('a', 64));
        await items.UpsertAsync(new ConnectorItem(Shop, 3091, 27, 11, "variation", "WOO-HOODIE-RED", null, null));
        var item = (await items.GetByExternalIdAsync(Shop, 27))!;
        Assert.Equal(new string('a', 64), item.LastPushHash); // same Woo id: hash kept
        Assert.Equal(_clock.Now, item.LastPushedAt);
        Assert.Equal("WOO-HOODIE-RED", item.Sku);
        Assert.Equal(11, item.ExternalParentId);
        await items.UpsertAsync(new ConnectorItem(Shop, 3091, 99, 0, "simple", "x", null, null));
        Assert.Null((await items.GetByInvNoAsync(Shop, 3091))!.LastPushHash); // relinked: hash reset
        Assert.Single(await items.ListAsync(Shop));
        Assert.True(await items.RemoveAsync(Shop, 3091));
        Assert.False(await items.RemoveAsync(Shop, 3091));

        var orders = _stores.Orders;
        var modified = new DateTime(2026, 10, 8, 11, 59, 0, DateTimeKind.Utc);
        await orders.UpsertAsync(new ConnectorOrder(Shop, 58, "58", 50001556, "ABCDEFGHJKLMNPQR",
            ConnectorOrderStatuses.Imported, false, null, "processing", modified, null, _clock.Now));
        Assert.True(await orders.TryMarkPaymentPostedAsync(Shop, 58));
        Assert.False(await orders.TryMarkPaymentPostedAsync(Shop, 58)); // a retry never tenders twice
        await orders.UpsertAsync(new ConnectorOrder(Shop, 58, "58", 50001556, "ABCDEFGHJKLMNPQR",
            ConnectorOrderStatuses.Exception, false, null, "refunded", modified, "refunded after import", _clock.Now));
        var order = (await orders.GetAsync(Shop, 58))!;
        Assert.True(order.PaymentPosted); // upsert never clears it
        Assert.Equal(_clock.Now, order.PaymentPostedAt);
        Assert.Equal(ConnectorOrderStatuses.Exception, order.ImportStatus);
        Assert.Equal(50001556, order.InvoiceNo);
        Assert.Equal(modified, order.ExternalModifiedAt);
        Assert.Equal("refunded after import", order.LastError);
        Assert.Null(await orders.GetAsync(Shop, 59));
    }

    // --- schema 2: imported figures, permanent failures, stock holds ---------------------

    [Fact]
    public async Task Migrate_FromVersion1_KeepsTheRows_AndAddsTheNewColumnsAndTable()
    {
        var path = Path.Combine(_dir, "v1.db");
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = ConnectorDatabase.Migrations[0].Sql + """
                PRAGMA user_version = 1;
                INSERT INTO ConnectorOrder (ConnectorId, ExternalOrderId, ImportStatus, CreatedAt, UpdatedAt)
                VALUES ('shop1', 58, 'imported', '2026-10-01T00:00:00.000Z', '2026-10-01T00:00:00.000Z');
                """;
            await command.ExecuteNonQueryAsync();
        }
        var db = new ConnectorDatabase(path, _clock);

        Assert.Equal((1, ConnectorDatabase.LatestVersion), await db.MigrateAsync());

        var orders = new SqliteConnectorOrderStore(db);
        var row = (await orders.GetAsync(Shop, 58))!;
        Assert.Equal(ConnectorOrderStatuses.Imported, row.ImportStatus);
        Assert.Null(row.ImportedTotal); // imported before schema 2: never compared
        await orders.UpsertAsync(row with { ImportedTotal = 34.850000m, ImportedTax = 2.85m });
        Assert.Equal((34.85m, 2.85m), ((await orders.GetAsync(Shop, 58))!.ImportedTotal, (await orders.GetAsync(Shop, 58))!.ImportedTax));
        Assert.Empty(await orders.GetStockHoldTotalsAsync(Shop));
    }

    [Fact]
    public async Task StockHolds_ReplacePerOrder_SumPerItem_AndReportWhatChanged()
    {
        var orders = _stores.Orders;

        Assert.Equal([1001, 1002], await orders.SetStockHoldAsync(Shop, 58, "on-hold",
            [new(1001, 2m), new(1002, 1m), new(1001, 1m)], 0));
        Assert.Equal([1001], await orders.SetStockHoldAsync(Shop, 59, "processing", [new(1001, 0.5m)], 1));
        var totals = await orders.GetStockHoldTotalsAsync(Shop);
        Assert.Equal((3.5m, 1m), (totals[1001], totals[1002]));
        Assert.Empty(await orders.GetStockHoldTotalsAsync("other"));

        Assert.Empty(await orders.SetStockHoldAsync(Shop, 58, "on-hold", [new(1001, 3m), new(1002, 1m)], 0)); // same: no change
        Assert.Equal([1002, 1003], await orders.SetStockHoldAsync(Shop, 58, "on-hold", [new(1001, 3m), new(1003, 4m)], 0));
        Assert.Equal([1001, 1003], await orders.SetStockHoldAsync(Shop, 58, "cancelled", [], 0)); // cleared
        Assert.Equal(0.5m, Assert.Single(await orders.GetStockHoldTotalsAsync(Shop)).Value);

        // Re-check list: held orders whose last check is older than the cut-off.
        Assert.Empty(await orders.ListStockHoldOrdersAsync(Shop, _clock.Now, 10));
        _clock.Now = _clock.Now.AddHours(2);
        Assert.Equal([59L], await orders.ListStockHoldOrdersAsync(Shop, _clock.Now.AddHours(-1), 10));
        await orders.SetStockHoldAsync(Shop, 59, "processing", [new(1001, 0.5m)], 1); // seen again: checked now
        Assert.Empty(await orders.ListStockHoldOrdersAsync(Shop, _clock.Now.AddHours(-1), 10));
    }

    [Fact]
    public async Task ListStranded_IsTheKeysWhoseLatestJobRanOutOfRetries_NotPermanentFailures()
    {
        var jobs = _stores.Jobs;
        async Task DeadLetterAsync(string key, bool retryable)
        {
            await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.OrderImport, key));
            var job = await jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(1));
            Assert.Equal(key, job!.ResourceKey);
            await jobs.FailAsync(job, "down", retryable, new JobRetryPolicy(TimeSpan.Zero, TimeSpan.Zero, MaxAttempts: 1));
        }

        await DeadLetterAsync("58", retryable: true);
        await DeadLetterAsync("59", retryable: false);
        await DeadLetterAsync("60", retryable: true);
        await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.OrderImport, "60")); // already re-queued: not stranded

        Assert.Equal(["58"], await jobs.ListStrandedAsync(Shop, ConnectorJobKinds.OrderImport));
        Assert.Empty(await jobs.ListStrandedAsync(Shop, ConnectorJobKinds.CatalogPush));
    }
}
