using System.Text.Json;
using System.Text.Json.Nodes;
using PA.Connectors.Core;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Orders;
using PA.Connectors.Core.Pos;
using PA.Connectors.Core.Stores;
using PA.Connectors.WooCommerce.Orders;
using Xunit;

namespace PA.Tests.Connectors.Orders;

/// <summary>
/// The review findings on the S2/S3 build: Woo stock holds for orders POSitive does
/// not have, pay-on-completion methods, re-push after a post-import cancel, edited-after-import totals,
/// order.import surviving an outage, recovery of a create whose outcome was lost, cent rounding of
/// tax-inclusive orders, trash. Same harness as <see cref="WooOrderImportTests"/>.
/// </summary>
public sealed class WooOrderReviewFixesTests : IAsyncLifetime
{
    private const string Shop = OrderImportHarness.Shop;
    private const int CapInvNo = 1001, HoodieRedInvNo = 2001, BeanieInvNo = 2002;
    private OrderImportHarness _h = null!;

    public async Task InitializeAsync() => _h = await OrderImportHarness.CreateAsync();

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<IReadOnlyDictionary<int, decimal>> HeldAsync() => _h.Stores.Orders.GetStockHoldTotalsAsync(Shop);

    private async Task<List<string>> PushesAsync() =>
        (await _h.DrainJobsAsync()).Where(j => j.Kind == ConnectorJobKinds.CatalogPush)
        .Select(j => j.ResourceKey + "=" + string.Join(",", JsonSerializer.Deserialize<CatalogPushPayload>(j.Payload!)!.InvNos))
        .ToList();

    private JsonObject OnHoldCapOrder(long id = 58) => _h.Woo.AddOrder("order-guest-paid.json", o =>
    {
        o["id"] = id;
        o["number"] = id.ToString();
        o["status"] = "on-hold";
        o["date_paid"] = null;
        o["date_paid_gmt"] = null;
    });

    // --- 1. stock holds -----------------------------------------------------------

    [Fact]
    public async Task OnHoldNotImported_HoldsItsStock_AndRepushes_ThenImportClearsIt()
    {
        var order = OnHoldCapOrder();
        await _h.LinkAsync(16, CapInvNo);

        await _h.ImportAsync(58);

        Assert.Empty(_h.Pos.Requests); // not imported: POSitive never called
        Assert.Equal(2m, (await HeldAsync())[CapInvNo]);
        Assert.Equal(["order:58=1001"], await PushesAsync());

        // Same state again (redelivery, poll): no change, no push.
        await _h.ImportAsync(58);
        Assert.Empty(await PushesAsync());

        // Paid → processing → imported: POSitive's availableLive counts it now, so the hold goes.
        order["status"] = "processing";
        order["date_paid_gmt"] = "2026-10-08T19:00:00";
        await _h.ImportAsync(58);

        Assert.Single(_h.Pos.Transactions);
        Assert.Empty(await HeldAsync());
        Assert.Equal(["order:58=1001"], await PushesAsync());
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("refunded")]
    [InlineData("trash")]
    [InlineData("pending")]
    [InlineData("failed")]
    public async Task HeldOrder_ThatIsVoidedOrBackToPending_ReleasesTheHold_AndRepushes(string status)
    {
        var order = OnHoldCapOrder();
        await _h.LinkAsync(16, CapInvNo);
        await _h.ImportAsync(58);
        await _h.DrainJobsAsync();

        order["status"] = status;
        await _h.ImportAsync(58);

        Assert.Empty(await HeldAsync());
        Assert.Equal(["order:58=1001"], await PushesAsync());
        Assert.Empty(_h.Pos.Requests);
    }

    [Fact]
    public async Task HeldOrder_DeletedInWoo_ReleasesTheHold()
    {
        OnHoldCapOrder();
        await _h.LinkAsync(16, CapInvNo);
        await _h.ImportAsync(58);
        await _h.DrainJobsAsync();

        _h.Woo.Orders.Remove(58);
        await _h.ImportAsync(58);

        Assert.Empty(await HeldAsync());
        Assert.Equal(["order:58=1001"], await PushesAsync());
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("failed")]
    [InlineData("checkout-draft")]
    [InlineData("cancelled")]
    public async Task StatusesWooDoesNotReduceStockFor_HoldNothing(string status)
    {
        _h.Woo.AddOrder("order-guest-paid.json", o => o["status"] = status);
        await _h.LinkAsync(16, CapInvNo);

        await _h.ImportAsync(58);

        Assert.Empty(await HeldAsync());
        Assert.Empty(await PushesAsync());
    }

    [Fact]
    public async Task OrderHeldAsAnException_KeepsItsHold_ResolvableLinesOnly_UntilARetryImportsIt()
    {
        // Totals mismatch on a paid (processing) order: an exception, nothing written — Woo still holds the stock.
        var order = _h.Woo.AddOrder("order-registered-coupon-shipping-fee.json", o => o["total"] = "99.99");
        await _h.LinkAsync(27, HoodieRedInvNo, parentId: 11);
        await _h.LinkAsync(14, BeanieInvNo);

        await _h.ImportAsync(59);

        Assert.Equal(OrderExceptionKinds.TotalsMismatch, Assert.Single(await _h.OpenExceptionsAsync()).Kind);
        var held = await HeldAsync();
        Assert.Equal((1m, 2m), (held[HoodieRedInvNo], held[BeanieInvNo]));

        order["total"] = "90.05";
        await _h.ImportAsync(59);

        Assert.Single(_h.Pos.Transactions);
        Assert.Empty(await HeldAsync());
    }

    [Fact]
    public async Task UnresolvableLine_CannotBeHeld_TheOthersAre()
    {
        _h.Rebuild(s => s with { ImportOnHold = false });
        _h.Woo.AddOrder("order-registered-coupon-shipping-fee.json", o => o["status"] = "on-hold");
        await _h.LinkAsync(14, BeanieInvNo); // the hoodie variation is not linked

        await _h.ImportAsync(59);

        var held = await HeldAsync();
        Assert.Equal(2m, Assert.Single(held).Value);
        Assert.Equal(BeanieInvNo, held.Single().Key);
    }

    [Fact]
    public async Task Poll_RecordsTheHoldOfAnOnHoldOrder_WithoutQueuingAnImport_AndRechecksAHeldOrderItNoLongerLists()
    {
        _h.Clock.Now = new DateTime(2026, 10, 8, 18, 30, 0, DateTimeKind.Utc);
        OnHoldCapOrder();
        await _h.LinkAsync(16, CapInvNo);

        await _h.RunAsync(ConnectorJobKinds.OrderPoll);

        Assert.Equal(2m, (await HeldAsync())[CapInvNo]);
        var jobs = await _h.DrainJobsAsync();
        Assert.DoesNotContain(jobs, j => j.Kind == ConnectorJobKinds.OrderImport);
        Assert.Contains(jobs, j => j.Kind == ConnectorJobKinds.CatalogPush && j.ResourceKey == "order:58");

        // Deleted in Woo (no webhook, never listed again): after an hour the poll re-reads it by id.
        _h.Woo.Orders.Remove(58);
        _h.Clock.Now = _h.Clock.Now.AddMinutes(10);
        await _h.RunAsync(ConnectorJobKinds.OrderPoll);
        Assert.NotEmpty(await HeldAsync()); // not stale yet

        _h.Clock.Now = _h.Clock.Now.AddHours(1);
        await _h.RunAsync(ConnectorJobKinds.OrderPoll);
        Assert.Empty(await HeldAsync());
    }

    // --- 2. pay on completion (cash on delivery) -------------------------------------------

    [Fact]
    public async Task CashOnDelivery_ImportsAtProcessing_ButPostsThePaymentOnlyWhenCompleted()
    {
        var order = _h.Woo.AddOrder("order-guest-paid.json", o =>
        {
            o["payment_method"] = "COD";
            o["date_paid"] = null;
            o["date_paid_gmt"] = null;
        });
        await _h.LinkAsync(16, CapInvNo);

        await _h.ImportAsync(58);

        Assert.Single(_h.Pos.Transactions);
        Assert.Empty(_h.Pos.Payments);

        order["date_paid_gmt"] = "2026-10-09T09:00:00"; // some gateways stamp it early: still not paid
        await _h.ImportAsync(58);
        Assert.Empty(_h.Pos.Payments);

        order["status"] = "completed";
        await _h.ImportAsync(58);
        Assert.Equal(34.85m, Assert.Single(_h.Pos.Payments).Amount);
    }

    [Fact]
    public void PayOnCompletionMethods_DefaultsToCod_AndIsConfigurable()
    {
        Assert.Equal(["cod"], new ConnectorSettings().PayOnCompletionMethods);
        var parsed = ConnectorConfigFile.Parse("""{ "connectors": [ { "id": "s", "payOnCompletionMethods": ["cod", "cheque"] } ] }""");
        Assert.Equal(["cod", "cheque"], parsed.Connectors[0].PayOnCompletionMethods);

        var settings = new ConnectorSettings { PayOnCompletionMethods = ["cheque"] };
        var cod = new PA.Connectors.WooCommerce.Models.WooOrder { PaymentMethod = "cod", Status = "processing" };
        Assert.True(WooOrderImport.IsPaid(cod, settings)); // cod no longer listed: processing = paid
        Assert.False(WooOrderImport.IsPaid(cod, new ConnectorSettings()));
    }

    // --- 3 + 10. after-import cancel / refund / trash re-push --------------------------------

    [Theory]
    [InlineData("cancelled", "is now 'cancelled'")]
    [InlineData("refunded", "is now 'refunded'")]
    [InlineData("trash", "was moved to the trash")]
    public async Task VoidAfterImport_IsReportedOnce_AndRepushesTheOrdersItems(string status, string text)
    {
        var order = _h.Woo.AddOrder("order-guest-paid.json");
        await _h.LinkAsync(16, CapInvNo);
        await _h.ImportAsync(58);
        await _h.DrainJobsAsync();

        order["status"] = status;
        await _h.ImportAsync(58);
        await _h.ImportAsync(58);

        var exception = Assert.Single(await _h.OpenExceptionsAsync());
        Assert.Equal((OrderExceptionKinds.ChangedAfterImport, 1), (exception.Kind, exception.Occurrences));
        Assert.Contains(text, exception.Reason);
        Assert.Equal(["order:58=1001"], await PushesAsync()); // once, with the report
        Assert.Single(_h.Pos.Payments);
    }

    [Fact]
    public async Task TrashedBeforeThePaymentPosted_DoesNotTender()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        await _h.LinkAsync(16, CapInvNo);
        _h.Pos.FailNext(HttpMethod.Post, "/api/v2/transactions/payments");
        await Assert.ThrowsAsync<PosApiException>(() => _h.ImportAsync(58));

        _h.Woo.Order(58)["status"] = "trash";
        await _h.ImportAsync(58);

        Assert.Empty(_h.Pos.Payments);
        Assert.Contains("trash", Assert.Single(await _h.OpenExceptionsAsync()).Reason);
    }

    [Fact]
    public async Task DeletedAfterImport_IsReported()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        await _h.LinkAsync(16, CapInvNo);
        await _h.ImportAsync(58);

        _h.Woo.Orders.Remove(58);
        await _h.ImportAsync(58);

        Assert.Contains("was deleted", Assert.Single(await _h.OpenExceptionsAsync()).Reason);
    }

    // --- 4. edited after import ---------------------------------------------------------------

    [Fact]
    public async Task TotalEditedAfterImport_IsReported_AndThePaymentIsNotPosted()
    {
        _h.Rebuild(s => s with { ImportOnHold = true });
        var order = OnHoldCapOrder();
        await _h.LinkAsync(16, CapInvNo);
        await _h.ImportAsync(58);

        var row = await _h.OrderRowAsync(58);
        Assert.Equal((34.85m, 2.85m), (row!.ImportedTotal, row.ImportedTax));

        // The owner adds a line in wp-admin, then marks it paid.
        order["total"] = "52.27";
        order["total_tax"] = "4.27";
        order["status"] = "processing";
        order["date_paid_gmt"] = "2026-10-09T09:00:00";
        await _h.ImportAsync(58);

        Assert.Empty(_h.Pos.Payments);
        var exception = Assert.Single(await _h.OpenExceptionsAsync());
        Assert.Equal(OrderExceptionKinds.ChangedAfterImport, exception.Kind);
        Assert.Contains("edited after import (total 34.85 incl. tax 2.85 is now 52.27 incl. tax 4.27)", exception.Reason);
        Assert.Contains("payment was NOT posted", exception.Reason);
        Assert.False((await _h.OrderRowAsync(58))!.PaymentPosted);

        await _h.ImportAsync(58); // same state: quiet
        Assert.Equal(1, Assert.Single(await _h.OpenExceptionsAsync()).Occurrences);
    }

    // --- 5. outages ----------------------------------------------------------------------------

    [Fact]
    public void OrderImport_RetriesForAboutADay_HourlyAtMost()
    {
        var policy = JobRetryPolicy.For(ConnectorJobKinds.OrderImport);
        Assert.Same(JobRetryPolicy.OrderImport, policy);
        Assert.InRange(policy.TotalRetryTime(), TimeSpan.FromHours(23.5), TimeSpan.FromHours(25));
        Assert.Equal(TimeSpan.FromHours(1), policy.DelayFor(30));
        Assert.True(policy.Decide(30).Retry);
        Assert.False(policy.Decide(31).Retry);
        Assert.Same(JobRetryPolicy.Default, JobRetryPolicy.For(ConnectorJobKinds.CatalogPush));
    }

    [Fact]
    public async Task Runner_UsesTheLongScheduleForOrderImport()
    {
        var jobs = new InMemoryJobStore(() => _h.Clock.Now);
        var runner = new ConnectorRunner(new AlwaysDown(_h.Settings), jobs, new InMemoryCursorStore(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, time: _h.Clock);
        await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.OrderImport, "58"));
        await jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.CatalogPush, "x"));

        for (var i = 0; i < 12; i++) // 12 failed attempts: past the default's 8
        {
            await runner.DrainAsync(10, CancellationToken.None);
            _h.Clock.Now = _h.Clock.Now.AddHours(2);
        }

        Assert.Equal(ConnectorJobStatuses.Pending, jobs.Jobs.Single(j => j.Kind == ConnectorJobKinds.OrderImport).Status);
        Assert.Equal(ConnectorJobStatuses.Failed, jobs.Jobs.Single(j => j.Kind == ConnectorJobKinds.CatalogPush).Status);
    }

    private sealed class AlwaysDown(ConnectorSettings settings) : IConnector
    {
        public ConnectorSettings Settings { get; } = settings;
        public IReadOnlyList<RecurringJob> RecurringJobs { get; } = [];
        public Task HandleAsync(ConnectorJob job, CancellationToken cancellationToken) =>
            throw new PosApiException("POSitive API answered 503", System.Net.HttpStatusCode.ServiceUnavailable, "unavailable");
    }

    [Fact]
    public async Task Poll_RequeuesOrderImportsThatRanOutOfRetries_ButNotPermanentFailuresOrFinishedOrders()
    {
        _h.Clock.Now = new DateTime(2026, 10, 8, 18, 30, 0, DateTimeKind.Utc);
        var giveUp = new JobRetryPolicy(TimeSpan.Zero, TimeSpan.Zero, MaxAttempts: 1);
        foreach (var (key, retryable) in new[] { ("58", true), ("61", false), ("62", true) })
        {
            await _h.Stores.Jobs.EnqueueAsync(new JobRequest(Shop, ConnectorJobKinds.OrderImport, key));
            var job = await _h.Stores.Jobs.ClaimNextAsync(Shop, TimeSpan.FromMinutes(5));
            Assert.Equal(JobFailureOutcome.DeadLettered, await _h.Stores.Jobs.FailAsync(job!, "down", retryable, giveUp));
        }
        // 62 was imported and paid since (by hand-triggered retry): nothing to do.
        await _h.Stores.Orders.UpsertAsync(new ConnectorOrder(Shop, 62, "62", 1, null, ConnectorOrderStatuses.Imported, false, null,
            "processing", null, null, null));
        await _h.Stores.Orders.TryMarkPaymentPostedAsync(Shop, 62);

        await _h.RunAsync(ConnectorJobKinds.OrderPoll);

        var imports = (await _h.DrainJobsAsync()).Where(j => j.Kind == ConnectorJobKinds.OrderImport).ToList();
        Assert.Equal("58", Assert.Single(imports).ResourceKey);
        Assert.Contains("poll-requeue", imports[0].Payload);

        // The re-queued job completed: not stranded any more.
        await _h.RunAsync(ConnectorJobKinds.OrderPoll);
        Assert.DoesNotContain(await _h.DrainJobsAsync(), j => j.Kind == ConnectorJobKinds.OrderImport);
    }

    // --- 6 + 11. a create whose outcome was lost ------------------------------------------------

    [Fact]
    public async Task LostCreate_ThenCancelled_IsFoundInPositive_RecordedWithItsInvoice_AndTheCancelReported()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        await _h.LinkAsync(16, CapInvNo);
        _h.Pos.LoseResponse(HttpMethod.Post, "/api/v2/transactions");
        await Assert.ThrowsAsync<PosApiException>(() => _h.ImportAsync(58));
        Assert.Equal(ConnectorOrderStatuses.Pending, (await _h.OrderRowAsync(58))!.ImportStatus);

        _h.Woo.Order(58)["status"] = "cancelled"; // not importable any more — but POSitive has it
        await _h.ImportAsync(58);

        var row = await _h.OrderRowAsync(58);
        Assert.Equal(ConnectorOrderStatuses.Imported, row!.ImportStatus);
        Assert.Equal(_h.Pos.Transactions["woo-58"].InvoiceNo, row.InvoiceNo);
        Assert.Equal(34.85m, row.ImportedTotal);
        Assert.Empty(_h.Pos.Payments);
        var exception = Assert.Single(await _h.OpenExceptionsAsync());
        Assert.Equal(OrderExceptionKinds.ChangedAfterImport, exception.Kind);
        Assert.Contains("is now 'cancelled'", exception.Reason);
        Assert.Equal(1, _h.Pos.CountPath(HttpMethod.Post, "/api/v2/transactions"));
    }

    [Fact]
    public async Task LostCreate_Retry_RecoversTheInvoiceNumber_WithoutCreatingAgain()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        await _h.LinkAsync(16, CapInvNo);
        _h.Pos.LoseResponse(HttpMethod.Post, "/api/v2/transactions");
        await Assert.ThrowsAsync<PosApiException>(() => _h.ImportAsync(58));

        await _h.ImportAsync(58);

        Assert.Equal(1, _h.Pos.CountPath(HttpMethod.Post, "/api/v2/transactions"));
        Assert.Equal(_h.Pos.Transactions["woo-58"].InvoiceNo, (await _h.OrderRowAsync(58))!.InvoiceNo);
        Assert.Single(_h.Pos.Payments);
    }

    [Fact]
    public async Task TransactionExists409_RecoversTheInvoiceNumber_ByExternalId_OrFromTheInvoiceTheLookupNames()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        _h.Woo.AddOrder("order-guest-paid.json", o => { o["id"] = 61; o["number"] = "61"; });
        await _h.LinkAsync(16, CapInvNo);
        await _h.ImportAsync(58);
        await _h.ImportAsync(61);
        _h.Pos.Transactions["woo-61"].Invoiced = true; // already posted to an invoice in POSitive

        // connectors.db lost: both replays answer 409 transaction_exists.
        await ClearOrderTableAsync();
        _h.Pos.Requests.Clear();
        await _h.ImportAsync(58);
        await _h.ImportAsync(61);

        Assert.Equal(_h.Pos.Transactions["woo-58"].InvoiceNo, (await _h.OrderRowAsync(58))!.InvoiceNo);
        Assert.Equal(_h.Pos.Transactions["woo-61"].InvoiceNo, (await _h.OrderRowAsync(61))!.InvoiceNo);
        Assert.Equal(2, _h.Pos.Transactions.Count);
        Assert.Equal(2, _h.Pos.Payments.Count);
        // G13: the exact lookup by our id — no customer or purchase-order scans.
        Assert.Equal(1, _h.Pos.Count(HttpMethod.Get, "/api/v2/transactions/pending?externalId=woo-58"));
        Assert.Equal(1, _h.Pos.Count(HttpMethod.Get, "/api/v2/transactions/pending?externalId=woo-61"));
        Assert.Equal(1, _h.Pos.Count(HttpMethod.Get, $"/api/v2/invoices/{_h.Pos.Transactions["woo-61"].InvoiceNo + 500000}"));
        Assert.DoesNotContain(_h.Pos.Requests, r => r.PathAndQuery.Contains("customerId=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TransactionExists409_LookupFindsNothing_KeepsTheInvoiceNumberThe409Names()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        await _h.LinkAsync(16, CapInvNo);
        await _h.ImportAsync(58);
        _h.Pos.Transactions["woo-58"].Voided = true; // voided in the store: still a duplicate, no longer pending

        await ClearOrderTableAsync();
        await _h.ImportAsync(58);

        Assert.Equal(_h.Pos.Transactions["woo-58"].InvoiceNo, (await _h.OrderRowAsync(58))!.InvoiceNo);
        Assert.Single(_h.Pos.Transactions);
    }

    [Fact]
    public async Task SkippedLine_IsReportedAgain_WhenTheCreateAnswerWasLost()
    {
        _h.Woo.AddOrder("order-registered-coupon-shipping-fee.json");
        await _h.LinkAsync(27, HoodieRedInvNo, parentId: 11);
        await _h.LinkAsync(14, BeanieInvNo);
        _h.Pos.Products.UnionWith([HoodieRedInvNo]); // the beanie no longer exists in POSitive
        _h.Pos.LoseResponse(HttpMethod.Post, "/api/v2/transactions");
        await Assert.ThrowsAsync<PosApiException>(() => _h.ImportAsync(59));
        Assert.Empty(await _h.OpenExceptionsAsync()); // the per-line answer was lost with the response

        await _h.ImportAsync(59);

        var exception = Assert.Single(await _h.OpenExceptionsAsync());
        Assert.Equal(OrderExceptionKinds.LinesSkipped, exception.Kind);
        Assert.Contains("woo-li4", exception.Reason);
        Assert.Equal(ConnectorOrderStatuses.Imported, (await _h.OrderRowAsync(59))!.ImportStatus);
    }

    [Fact]
    public async Task Poll_QueuesAPendingRow_WhateverTheOrdersStatusIsNow()
    {
        _h.Clock.Now = new DateTime(2026, 10, 8, 18, 30, 0, DateTimeKind.Utc);
        _h.Woo.AddOrder("order-guest-paid.json", o => o["status"] = "cancelled");
        await _h.Stores.Orders.UpsertAsync(new ConnectorOrder(Shop, 58, "58", null, null, ConnectorOrderStatuses.Pending, false, null,
            "processing", new DateTime(2026, 10, 8, 18, 0, 0, DateTimeKind.Utc), null, null));

        await _h.RunAsync(ConnectorJobKinds.OrderPoll);

        Assert.Contains(await _h.DrainJobsAsync(), j => j.Kind == ConnectorJobKinds.OrderImport && j.ResourceKey == "58");
    }

    private async Task ClearOrderTableAsync()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_h.Db.Path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ConnectorOrder;";
        await command.ExecuteNonQueryAsync();
    }

    // --- 7. rounding ---------------------------------------------------------------------------

    /// <summary>
    /// Four lines at 15% tax with prices that include tax (30.85, 13.68, 11.53, 36.52 = 92.58, tax
    /// 12.08). Rounded to the cent the ex-tax lines add up to 80.52, not 92.58 − 12.08 = 80.50.
    /// </summary>
    private JsonObject TaxInclusiveDriftOrder(bool sixPlaces) => _h.Woo.AddOrder("order-guest-paid.json", o =>
    {
        (string Total, string Tax)[] six =
        [
            ("26.826087", "4.023913"), ("11.895652", "1.784348"), ("10.026087", "1.503913"), ("31.756522", "4.763478"),
        ];
        (string Total, string Tax)[] two = [("26.83", "4.02"), ("11.90", "1.78"), ("10.03", "1.50"), ("31.76", "4.76")];
        var figures = sixPlaces ? six : two;
        var template = o["line_items"]![0]!;
        var lines = new JsonArray();
        for (var i = 0; i < figures.Length; i++)
        {
            var line = template.DeepClone().AsObject();
            line["id"] = 100 + i;
            line["product_id"] = 16 + i;
            line["quantity"] = 1;
            line["sku"] = "drift-" + i;
            line["subtotal"] = figures[i].Total;
            line["total"] = figures[i].Total;
            line["subtotal_tax"] = figures[i].Tax;
            line["total_tax"] = figures[i].Tax;
            line["taxes"] = new JsonArray();
            lines.Add(line);
        }
        o["line_items"] = lines;
        o["prices_include_tax"] = true;
        o["total"] = sixPlaces ? "92.580000" : "92.58";
        o["total_tax"] = sixPlaces ? "12.080000" : "12.08";
    });

    private async Task LinkDriftItemsAsync()
    {
        for (var i = 0; i < 4; i++)
            await _h.LinkAsync(16 + i, 3001 + i);
    }

    [Fact]
    public async Task OrdersAreReadWithSixDecimalPlaces()
    {
        _h.Clock.Now = new DateTime(2026, 10, 8, 18, 30, 0, DateTimeKind.Utc);
        _h.Woo.AddOrder("order-guest-paid.json");

        await _h.ImportAsync(58);
        await _h.RunAsync(ConnectorJobKinds.OrderPoll);

        var orderReads = _h.Woo.Requests.Where(r => r.PathAndQuery.Contains("/orders", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, orderReads.Count);
        Assert.All(orderReads, r => Assert.Contains("dp=6", r.PathAndQuery));
    }

    [Fact]
    public async Task TaxInclusiveOrder_AtSixPlaces_Reconciles_AndIsSentInCentsThatAddUp()
    {
        TaxInclusiveDriftOrder(sixPlaces: true);
        await LinkDriftItemsAsync();

        await _h.ImportAsync(58);

        Assert.Empty(await _h.OpenExceptionsAsync());
        var t = _h.Pos.Transactions["woo-58"].Body;
        var lines = t["lines"]!.AsArray();
        Assert.Equal(80.50m, (decimal)t["total"]!);
        Assert.Equal(12.08m, (decimal)t["taxAmount"]!);
        Assert.Equal(80.50m, lines.Sum(l => (decimal)l!["lineTotal"]!));
        Assert.Equal(12.08m, lines.Sum(l => (decimal)l!["taxAmount"]!));
        // Each line keeps its own gross: total + tax = the tax-inclusive price.
        Assert.Equal([30.85m, 13.68m, 11.53m, 36.52m], lines.Select(l => (decimal)l!["lineTotal"]! + (decimal)l["taxAmount"]!));
        Assert.All(lines, l => Assert.Equal(decimal.Round((decimal)l!["lineTotal"]!, 2), (decimal)l["lineTotal"]!));
        Assert.Equal(92.58m, Assert.Single(_h.Pos.Payments).Amount);
    }

    [Fact]
    public async Task TaxInclusiveOrder_AtTwoPlaces_DriftsTwoCents_WithinTheFourLineTolerance()
    {
        TaxInclusiveDriftOrder(sixPlaces: false); // an older store that ignores dp
        await LinkDriftItemsAsync();

        await _h.ImportAsync(58);

        Assert.Empty(await _h.OpenExceptionsAsync());
        var t = _h.Pos.Transactions["woo-58"].Body;
        Assert.Equal(80.50m, t["lines"]!.AsArray().Sum(l => (decimal)l!["lineTotal"]!));
        Assert.Equal(12.08m, t["lines"]!.AsArray().Sum(l => (decimal)l!["taxAmount"]!));
    }

    [Fact]
    public void Reconciliation_NetTolerance_ScalesWithTheLineCount()
    {
        static PosTransaction Lines(decimal drift, int count) => new()
        {
            LocalTransactionId = "t", Total = 100m, TaxAmount = 10m,
            Lines = Enumerable.Range(0, count)
                .Select(i => new PosTransactionLine { LineTotal = (i == 0 ? 100m + drift : 0m), TaxAmount = i == 0 ? 10m : 0m })
                .ToList(),
        };
        Assert.True(OrderReconciliation.Check(Lines(0.02m, 4), 110m, 10m).Balanced);   // 4 × 0.005
        Assert.False(OrderReconciliation.Check(Lines(0.03m, 4), 110m, 10m).Balanced);
        Assert.False(OrderReconciliation.Check(Lines(0.02m, 3), 110m, 10m).Balanced);  // 3 lines: 0.015
        Assert.True(OrderReconciliation.Check(Lines(0.01m, 1), 110m, 10m).Balanced);   // never below 0.01
        Assert.Equal(0.05m, OrderReconciliation.LineTolerance(10));
    }

    [Fact]
    public void ToCents_MovesTheRoundingCentsOntoTheLinesThatRoundedMost()
    {
        var unrounded = new PosTransaction
        {
            LocalTransactionId = "t", Total = 80.50m, TaxAmount = 12.08m,
            Lines =
            [
                new() { Quantity = 1, LineTotal = 26.826087m, TaxAmount = 4.023913m },
                new() { Quantity = 1, LineTotal = 11.895652m, TaxAmount = 1.784348m },
                new() { Quantity = 1, LineTotal = 10.026087m, TaxAmount = 1.503913m },
                new() { Quantity = 2, LineTotal = 31.756522m, TaxAmount = 4.763478m },
            ],
        };

        var sent = WooOrderMapper.ToCents(unrounded);

        Assert.Equal([26.82m, 11.89m, 10.03m, 31.76m], sent.Lines.Select(l => l.LineTotal));
        Assert.Equal([4.03m, 1.79m, 1.50m, 4.76m], sent.Lines.Select(l => l.TaxAmount));
        Assert.Equal(15.88m, sent.Lines[3].Price); // unit price follows the sent total
        Assert.Equal((80.50m, 12.08m), (sent.Total, sent.TaxAmount));
    }
}
