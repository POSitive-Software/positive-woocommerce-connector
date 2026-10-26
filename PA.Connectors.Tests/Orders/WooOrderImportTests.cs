using System.Text.Json;
using System.Text.Json.Nodes;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Orders;
using PA.Connectors.Core.Pos;
using PA.Connectors.Core.Stores;
using PA.Connectors.WooCommerce.Orders;
using Xunit;

namespace PA.Tests.Connectors.Orders;

/// <summary>
/// order.import end to end against the recorded Woo orders (spike S0bc): #58 a
/// guest paying 2× Cap; #59 registered customer 3 with a variation, a coupon,
/// Flat rate shipping and a taxable fee. Woo and POSitive are fakes at the HTTP
/// boundary; the import, both clients and the SQLite stores are real.
/// </summary>
public sealed class WooOrderImportTests : IAsyncLifetime
{
    private const int CapInvNo = 1001, HoodieRedInvNo = 2001, BeanieInvNo = 2002, FreightInvNo = 900806;
    private OrderImportHarness _h = null!;

    public async Task InitializeAsync() => _h = await OrderImportHarness.CreateAsync();

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private JsonObject Transaction(long orderId) => _h.Pos.Transactions[WooOrderMapper.LocalTransactionId(orderId)].Body;

    private static JsonArray Lines(JsonObject transaction) => transaction["lines"]!.AsArray();

    private async Task LinkOrder59Async()
    {
        await _h.LinkAsync(27, HoodieRedInvNo, parentId: 11);
        await _h.LinkAsync(14, BeanieInvNo);
    }

    // --- the happy paths ------------------------------------------------------

    [Fact]
    public async Task GuestPaidOrder_CreatesGuestCustomer_Transaction_Payment_AndQueuesTheStockPush()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        await _h.LinkAsync(16, CapInvNo);

        await _h.ImportAsync(58);

        // Guest key: lowercased, trimmed billing email (the fixture's is mixed case).
        var customer = Assert.Single(_h.Pos.Customers.Values);
        Assert.Equal("woo-ggina.guest@example.com", customer.ExternalId);
        Assert.Equal("Gina", (string?)customer.Fields["firstName"]);
        Assert.Equal("Guest", (string?)customer.Fields["lastName"]);
        Assert.Equal("Gina.Guest@Example.com", (string?)customer.Fields["email"]);
        Assert.Equal("509-555-0199", (string?)customer.Fields["homePhone"]);
        Assert.Equal("300 Guest Rd", (string?)customer.Billing!["address1"]);
        Assert.Null(customer.Billing["contact"]); // address fields only: never renames the customer

        var t = Transaction(58);
        Assert.Equal("woo-58", (string?)t["localTransactionId"]);
        Assert.Equal("woo-ggina.guest@example.com", (string?)t["localCustomerId"]);
        Assert.Equal("N", (string?)t["transactionType"]);
        Assert.Equal("58", (string?)t["purchaseOrderNumber"]);
        Assert.Equal("2026-10-08T18:12:01", (string?)t["createdAt"]); // store-local wall time, no offset
        Assert.Equal(32.00m, (decimal)t["total"]!);
        Assert.Equal(2.85m, (decimal)t["taxAmount"]!);
        Assert.Contains("Payment: Credit card (Stripe)", (string?)t["note"]);
        Assert.Contains("Customer note: Leave at the back door", (string?)t["note"]);
        Assert.Equal("300 Guest Rd", (string?)t["shippingAddress"]!["address1"]);
        Assert.Equal("Gina Guest", (string?)t["shippingAddress"]!["contact"]);
        Assert.Null(t["shippingAddress"]!["localAddressId"]); // identical ship-tos are reused by the API
        var line = Assert.Single(Lines(t))!;
        Assert.Equal(CapInvNo, (int)line["productId"]!);
        Assert.Equal(2m, (decimal)line["quantity"]!);
        Assert.Equal(16m, (decimal)line["price"]!);
        Assert.Equal(32.00m, (decimal)line["lineTotal"]!);
        Assert.Equal(2.85m, (decimal)line["taxAmount"]!);

        var payment = Assert.Single(_h.Pos.Payments);
        Assert.Equal(("woo-58", "woo-58-pay", "R", 34.85m),
            (payment.LocalTransactionId, payment.LocalPaymentId, payment.PaymentType, payment.Amount));

        var row = await _h.OrderRowAsync(58);
        Assert.Equal(ConnectorOrderStatuses.Imported, row!.ImportStatus);
        Assert.True(row.PaymentPosted);
        Assert.Equal(_h.Pos.Transactions["woo-58"].InvoiceNo, row.InvoiceNo);
        Assert.Equal("processing", row.ExternalStatus);

        var push = Assert.Single(await _h.DrainJobsAsync());
        Assert.Equal((ConnectorJobKinds.CatalogPush, "order:58"), (push.Kind, push.ResourceKey));
        Assert.Equal([CapInvNo], JsonSerializer.Deserialize<CatalogPushPayload>(push.Payload!)!.InvNos);
        Assert.Empty(await _h.OpenExceptionsAsync());
    }

    [Fact]
    public async Task RegisteredOrder_WithCouponShippingAndFee_MapsEveryLine_AndReconciles()
    {
        _h.Woo.AddOrder("order-registered-coupon-shipping-fee.json");
        await LinkOrder59Async();
        _h.Pos.AddSku("FREIGHT", FreightInvNo);

        await _h.ImportAsync(59);

        Assert.Equal("woo-c3", Assert.Single(_h.Pos.Customers.Keys));
        var t = Transaction(59);
        var lines = Lines(t);
        Assert.Equal(4, lines.Count);
        // Product lines: unit price = line total / qty (after the 10% coupon, ex tax), no description (POSitive's own).
        Assert.Equal((HoodieRedInvNo, 1m, 37.80m, 37.80m, 3.36m), LineFigures(lines[0]!));
        Assert.Equal((BeanieInvNo, 2m, 16.20m, 32.40m, 2.88m), LineFigures(lines[1]!));
        Assert.Null(lines[0]!["description"]);
        // Shipping: the store's FREIGHT item, the method as its description (design §7.1).
        Assert.Equal((FreightInvNo, 1m, 10.00m, 10.00m, 0.89m), LineFigures(lines[2]!));
        Assert.Equal("Flat rate", (string?)lines[2]!["description"]);
        // Fee: non-inventory.
        Assert.Null(lines[3]!["productId"]);
        Assert.Equal("Fee - Gift wrap", (string?)lines[3]!["description"]);
        Assert.Equal(2.50m, (decimal)lines[3]!["lineTotal"]!);
        Assert.Equal(0.22m, (decimal)lines[3]!["taxAmount"]!);

        Assert.Equal(82.70m, (decimal)t["total"]!); // 90.05 − 7.35
        Assert.Equal(7.35m, (decimal)t["taxAmount"]!);
        Assert.Contains("Coupons: spike10", (string?)t["note"]);
        Assert.Equal("200 Ship Ave", (string?)t["shippingAddress"]!["address1"]);
        Assert.Equal(90.05m, Assert.Single(_h.Pos.Payments).Amount);

        // The stock push names the ordered items, not the freight item.
        var push = Assert.Single(await _h.DrainJobsAsync());
        Assert.Equal([HoodieRedInvNo, BeanieInvNo], JsonSerializer.Deserialize<CatalogPushPayload>(push.Payload!)!.InvNos);
    }

    [Fact]
    public async Task NoFreightItem_ShippingBecomesANonInventoryLine()
    {
        _h.Woo.AddOrder("order-registered-coupon-shipping-fee.json");
        await LinkOrder59Async();

        await _h.ImportAsync(59);

        var shipping = Lines(Transaction(59))[2]!;
        Assert.Null(shipping["productId"]);
        Assert.Null(shipping["sku"]);
        Assert.Equal("Shipping - Flat rate", (string?)shipping["description"]);
        Assert.Equal(10.00m, (decimal)shipping["lineTotal"]!);
    }

    [Fact]
    public async Task NegativeFee_IsKept_AndFreeShippingAddsNoLine()
    {
        _h.Woo.AddOrder("order-registered-coupon-shipping-fee.json", o =>
        {
            // Shipping made free (−10.00 −0.89), fee turned into a −2.50 discount (−0.22 tax): lines 67.70 + tax 6.02 = 73.72.
            o["shipping_lines"]![0]!["total"] = "0.00";
            o["shipping_lines"]![0]!["total_tax"] = "0.00";
            o["fee_lines"]![0]!["total"] = "-2.50";
            o["fee_lines"]![0]!["total_tax"] = "-0.22";
            o["total"] = "73.72";
            o["total_tax"] = "6.02";
        });
        await LinkOrder59Async();

        await _h.ImportAsync(59);

        var lines = Lines(Transaction(59));
        Assert.Equal(3, lines.Count);
        Assert.Equal(-2.50m, (decimal)lines[2]!["lineTotal"]!);
        Assert.Equal(67.70m, (decimal)Transaction(59)["total"]!);
        Assert.Empty(await _h.OpenExceptionsAsync());
    }

    [Fact]
    public async Task PricesIncludeTax_TotalsAreAlreadyExTax_SoTheLinesAreTheSame()
    {
        // Woo's REST line/shipping/fee totals are ex tax under either setting.
        _h.Woo.AddOrder("order-registered-coupon-shipping-fee.json", o => o["prices_include_tax"] = true);
        await LinkOrder59Async();

        await _h.ImportAsync(59);

        var lines = Lines(Transaction(59));
        Assert.Equal((HoodieRedInvNo, 1m, 37.80m, 37.80m, 3.36m), LineFigures(lines[0]!));
        Assert.Equal(82.70m, (decimal)Transaction(59)["total"]!);
        Assert.Empty(await _h.OpenExceptionsAsync());
    }

    [Fact]
    public async Task UnlinkedLine_FallsBackToTheSku_ExactlyOneMatchOnly()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        _h.Pos.AddSku("woo-cap", CapInvNo);

        await _h.ImportAsync(58);

        Assert.Equal(CapInvNo, (int)Assert.Single(Lines(Transaction(58)))!["productId"]!);
    }

    // --- nothing written ------------------------------------------------------------

    [Fact]
    public async Task UnmatchedLine_RaisesOneException_WritesNothing_AndARetryAfterLinkingImportsAndResolvesIt()
    {
        _h.Woo.AddOrder("order-registered-coupon-shipping-fee.json");
        await _h.LinkAsync(27, HoodieRedInvNo, parentId: 11);
        _h.Pos.AddSku("woo-beanie", 7, 8); // ambiguous: not a match either

        await _h.ImportAsync(59);

        var exception = Assert.Single(await _h.OpenExceptionsAsync());
        Assert.Equal((OrderExceptionKinds.UnmatchedProduct, "59"), (exception.Kind, exception.ExternalRef));
        Assert.Contains("line 4 'Beanie'", exception.Reason);
        Assert.Contains("matches 2 POSitive items", exception.Reason);
        Assert.DoesNotContain("Hoodie", exception.Reason);
        Assert.Empty(_h.Pos.Customers);
        Assert.Empty(_h.Pos.Transactions);
        Assert.Empty(_h.Pos.Payments);
        Assert.Equal(ConnectorOrderStatuses.Exception, (await _h.OrderRowAsync(59))!.ImportStatus);
        // Woo holds the stock of the linked line for this order: that item is re-pushed with the hold.
        var hold = Assert.Single(await _h.DrainJobsAsync());
        Assert.Equal((ConnectorJobKinds.CatalogPush, "order:59"), (hold.Kind, hold.ResourceKey));
        Assert.Equal([HoodieRedInvNo], JsonSerializer.Deserialize<CatalogPushPayload>(hold.Payload!)!.InvNos);
        Assert.Equal(1m, (await _h.Stores.Orders.GetStockHoldTotalsAsync(OrderImportHarness.Shop))[HoodieRedInvNo]);

        await _h.LinkAsync(14, BeanieInvNo);
        await _h.ImportAsync(59);

        Assert.Single(_h.Pos.Transactions);
        Assert.Empty(await _h.OpenExceptionsAsync());
        var resolved = (await _h.Stores.Exceptions.GetAsync(exception.ExceptionId))!;
        Assert.Equal(ConnectorExceptionStatuses.Resolved, resolved.Status);
        // F6: the outcome is stored with it — the invoice and the link that fixed the line.
        var invoice = (await _h.OrderRowAsync(59))!.InvoiceNo;
        Assert.NotNull(invoice);
        Assert.Equal($"Imported as POSitive order {invoice} on retry; line 4 'Beanie' linked to POSitive item {BeanieInvNo}.", resolved.Resolution);
    }

    [Fact]
    public async Task AnExceptionClosedWithRetry_GetsTheOutcomeRecorded_WhenThatRetryImports()
    {
        _h.Woo.AddOrder("order-registered-coupon-shipping-fee.json");
        await _h.LinkAsync(27, HoodieRedInvNo, parentId: 11);
        _h.Pos.AddSku("woo-beanie", 7, 8);
        await _h.ImportAsync(59);
        var exception = Assert.Single(await _h.OpenExceptionsAsync());
        // The owner clicked Retry: closed as retried before the re-import runs.
        Assert.True(await _h.Stores.Exceptions.CloseAsync(exception.ExceptionId, ConnectorExceptionStatuses.Retried, "POS Manager (POS)"));

        await _h.LinkAsync(14, BeanieInvNo);
        await _h.ImportAsync(59);

        var row = (await _h.Stores.Exceptions.GetAsync(exception.ExceptionId))!;
        Assert.Equal(ConnectorExceptionStatuses.Retried, row.Status);
        Assert.StartsWith("Imported as POSitive order ", row.Resolution);
        Assert.Contains($"line 4 'Beanie' linked to POSitive item {BeanieInvNo}", row.Resolution);
    }

    [Fact]
    public async Task TotalsMismatch_RaisesException_AndWritesNothing()
    {
        _h.Woo.AddOrder("order-guest-paid.json", o => o["total"] = "40.00");
        await _h.LinkAsync(16, CapInvNo);

        await _h.ImportAsync(58);

        var exception = Assert.Single(await _h.OpenExceptionsAsync());
        Assert.Equal(OrderExceptionKinds.TotalsMismatch, exception.Kind);
        Assert.Contains("lines add up to 32.00 but the order total less tax is 37.15", exception.Reason);
        Assert.Empty(_h.Pos.Customers);
        Assert.Empty(_h.Pos.Transactions);
        Assert.Empty(_h.Pos.Payments);
    }

    [Fact]
    public async Task GuestWithoutEmail_IsRejected_AndWritesNothing()
    {
        _h.Woo.AddOrder("order-guest-paid.json", o => o["billing"]!["email"] = "  ");
        await _h.LinkAsync(16, CapInvNo);

        await _h.ImportAsync(58);

        Assert.Equal(OrderExceptionKinds.Rejected, Assert.Single(await _h.OpenExceptionsAsync()).Kind);
        Assert.Empty(_h.Pos.Requests);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("failed")]
    [InlineData("checkout-draft")]
    [InlineData("on-hold")]
    [InlineData("cancelled")]
    [InlineData("refunded")]
    public async Task StatusGate_OnlyProcessingAndCompletedImport(string status)
    {
        _h.Woo.AddOrder("order-guest-paid.json", o => o["status"] = status);
        await _h.LinkAsync(16, CapInvNo);

        await _h.ImportAsync(58);

        Assert.Empty(_h.Pos.Requests);
        Assert.Null(await _h.OrderRowAsync(58));
        Assert.Empty(await _h.OpenExceptionsAsync());
    }

    [Fact]
    public async Task Completed_Imports()
    {
        _h.Woo.AddOrder("order-guest-paid.json", o => o["status"] = "completed");
        await _h.LinkAsync(16, CapInvNo);

        await _h.ImportAsync(58);

        Assert.Single(_h.Pos.Transactions);
        Assert.Single(_h.Pos.Payments);
    }

    [Fact]
    public async Task OnHold_WithImportOnHold_ImportsUnpaid_ThenPostsThePaymentOnceItIsPaid()
    {
        _h.Rebuild(s => s with { ImportOnHold = true });
        var order = _h.Woo.AddOrder("order-guest-paid.json", o =>
        {
            o["status"] = "on-hold";
            o["date_paid"] = null;
            o["date_paid_gmt"] = null;
        });
        await _h.LinkAsync(16, CapInvNo);

        await _h.ImportAsync(58);

        Assert.Single(_h.Pos.Transactions);
        Assert.Empty(_h.Pos.Payments);
        Assert.False((await _h.OrderRowAsync(58))!.PaymentPosted);

        order["status"] = "processing";
        order["date_paid_gmt"] = "2026-10-09T09:00:00";
        await _h.ImportAsync(58);

        Assert.Single(_h.Pos.Transactions);
        Assert.Equal(34.85m, Assert.Single(_h.Pos.Payments).Amount);
        Assert.True((await _h.OrderRowAsync(58))!.PaymentPosted);
    }

    // --- customers --------------------------------------------------------------------

    [Fact]
    public async Task ExistingCustomer_IsPatchedWithChangedFieldsOnly_PhonesComparedByDigits()
    {
        _h.Woo.AddOrder("order-registered-coupon-shipping-fee.json");
        await LinkOrder59Async();
        var existing = new FakePosApi.Customer { Id = 77, ExternalId = "woo-c3", Billing = new JsonObject { ["address1"] = "old" } };
        existing.Fields["firstName"] = "Sam";
        existing.Fields["lastName"] = "Spike";
        existing.Fields["email"] = "old@example.com";
        existing.Fields["homePhoneAreaCode"] = "509"; // POSitive files the area code apart
        existing.Fields["homePhone"] = "555-0100";
        _h.Pos.Customers["woo-c3"] = existing;

        await _h.ImportAsync(59);

        var patch = JsonNode.Parse(Assert.Single(existing.Patches))!.AsObject();
        Assert.Equal(["email"], patch.Select(p => p.Key));
        Assert.Equal("spike.customer@example.com", (string?)patch["email"]);
        Assert.Equal(0, _h.Pos.CountPath(HttpMethod.Post, "/api/v2/customers"));
        Assert.Equal("old", (string?)existing.Billing["address1"]); // billing already on file: kept
        Assert.Single(_h.Pos.Transactions);
    }

    [Fact]
    public async Task RepeatGuest_ReusesTheSameCustomer()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        _h.Woo.AddOrder("order-guest-paid.json", o =>
        {
            o["id"] = 61;
            o["number"] = "61";
            o["billing"]!["email"] = " gina.guest@EXAMPLE.com ";
        });
        await _h.LinkAsync(16, CapInvNo);

        await _h.ImportAsync(58);
        await _h.ImportAsync(61);

        Assert.Single(_h.Pos.Customers);
        Assert.Equal(2, _h.Pos.Transactions.Count);
        Assert.All(_h.Pos.Transactions.Values, t => Assert.Equal("woo-ggina.guest@example.com", (string?)t.Body["localCustomerId"]));
    }

    // --- idempotency and resume ------------------------------------------------------

    [Fact]
    public async Task Replay_WritesNothingTwice_EvenWhenTheOrderStoreIsLost()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        await _h.LinkAsync(16, CapInvNo);

        await _h.ImportAsync(58);
        await _h.ImportAsync(58); // redelivery: the store says imported

        Assert.Equal(1, _h.Pos.CountPath(HttpMethod.Post, "/api/v2/transactions"));
        Assert.Equal(1, _h.Pos.CountPath(HttpMethod.Post, "/api/v2/transactions/payments"));

        // A rebuilt connectors.db: the API's own guards (409s) carry it.
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_h.Db.Path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ConnectorOrder;";
            await command.ExecuteNonQueryAsync();
        }
        await _h.ImportAsync(58);

        Assert.Single(_h.Pos.Transactions);
        Assert.Single(_h.Pos.Payments);
        Assert.Equal(2, _h.Pos.Count(HttpMethod.Post, "/api/v2/transactions/payments")); // the replay answered 409 payment_exists
        var row = await _h.OrderRowAsync(58);
        Assert.Equal(ConnectorOrderStatuses.Imported, row!.ImportStatus);
        Assert.True(row.PaymentPosted);
    }

    [Fact]
    public async Task LostTransactionResponse_TheRetryRecoversFrom409_AndPaysOnce()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        await _h.LinkAsync(16, CapInvNo);
        _h.Pos.LoseResponse(HttpMethod.Post, "/api/v2/transactions");

        await Assert.ThrowsAsync<PosApiException>(() => _h.ImportAsync(58));
        Assert.Single(_h.Pos.Transactions); // it did land
        Assert.Equal(ConnectorOrderStatuses.Pending, (await _h.OrderRowAsync(58))!.ImportStatus);
        Assert.Empty(_h.Pos.Payments);

        await _h.ImportAsync(58);

        Assert.Single(_h.Pos.Transactions);
        Assert.Single(_h.Pos.Payments);
        Assert.Equal(ConnectorOrderStatuses.Imported, (await _h.OrderRowAsync(58))!.ImportStatus);
    }

    [Fact]
    public async Task DatabaseUpgradeRequired_RaisesPosUpgradeRequired_KeepsRetrying_AndTheRetryAfterTheUpgradeResolvesIt()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        await _h.LinkAsync(16, CapInvNo);
        _h.Pos.FailNext(HttpMethod.Post, "/api/v2/transactions", PosErrorCodes.DatabaseUpgradeRequired);

        // Transient: the job is retried on the order.import schedule, not given up.
        var error = await Assert.ThrowsAsync<PosApiException>(() => _h.ImportAsync(58));
        Assert.Equal(PosErrorCodes.DatabaseUpgradeRequired, error.ErrorCode);
        var raised = Assert.Single(await _h.OpenExceptionsAsync());
        Assert.Equal(OrderExceptionKinds.PosUpgradeRequired, raised.Kind);
        Assert.Contains("Prepare database", raised.Reason);
        Assert.Empty(_h.Pos.Transactions);

        // Prepare database has run: the retry imports the order and closes the exception.
        await _h.ImportAsync(58);

        Assert.Single(_h.Pos.Transactions);
        Assert.Equal(ConnectorOrderStatuses.Imported, (await _h.OrderRowAsync(58))!.ImportStatus);
        Assert.Empty(await _h.OpenExceptionsAsync());
    }

    [Fact]
    public async Task PaymentFailure_TheRetryResumesAtThePayment()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        await _h.LinkAsync(16, CapInvNo);
        _h.Pos.FailNext(HttpMethod.Post, "/api/v2/transactions/payments");

        await Assert.ThrowsAsync<PosApiException>(() => _h.ImportAsync(58));
        var row = await _h.OrderRowAsync(58);
        Assert.Equal(ConnectorOrderStatuses.Imported, row!.ImportStatus);
        Assert.False(row.PaymentPosted);

        await _h.ImportAsync(58);

        Assert.Equal(1, _h.Pos.CountPath(HttpMethod.Post, "/api/v2/transactions")); // not created again
        Assert.Single(_h.Pos.Transactions);
        Assert.Single(_h.Pos.Payments);
        Assert.True((await _h.OrderRowAsync(58))!.PaymentPosted);
    }

    [Fact]
    public async Task LostPaymentResponse_TheRetryGets409PaymentExists_AndNeverTendersTwice()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        await _h.LinkAsync(16, CapInvNo);
        _h.Pos.LoseResponse(HttpMethod.Post, "/api/v2/transactions/payments");

        await Assert.ThrowsAsync<PosApiException>(() => _h.ImportAsync(58));
        await _h.ImportAsync(58);
        await _h.ImportAsync(58);

        Assert.Single(_h.Pos.Payments);
        Assert.Equal(2, _h.Pos.Count(HttpMethod.Post, "/api/v2/transactions/payments"));
        Assert.True((await _h.OrderRowAsync(58))!.PaymentPosted);
    }

    [Fact]
    public async Task LinePosSkipped_AfterCreate_IsReported()
    {
        _h.Woo.AddOrder("order-registered-coupon-shipping-fee.json");
        await LinkOrder59Async(); // ...but item 2002 no longer exists in POSitive
        _h.Pos.Products.UnionWith([HoodieRedInvNo]);

        await _h.ImportAsync(59);

        var exception = Assert.Single(await _h.OpenExceptionsAsync());
        Assert.Equal(OrderExceptionKinds.LinesSkipped, exception.Kind);
        Assert.Contains("woo-li4", exception.Reason);
        Assert.Equal(ConnectorOrderStatuses.Imported, (await _h.OrderRowAsync(59))!.ImportStatus);
    }

    // --- after import -------------------------------------------------------------------

    [Fact]
    public async Task RefundOrCancelAfterImport_RaisesOneExceptionPerChange()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        await _h.LinkAsync(16, CapInvNo);
        await _h.ImportAsync(58);

        // Partial refund #60: status stays processing, refunds[] gains an entry.
        var refunded = _h.Woo.AddOrder("order-refund.json");
        await _h.ImportAsync(58);
        await _h.ImportAsync(58); // same state again: quiet

        var exception = Assert.Single(await _h.OpenExceptionsAsync());
        Assert.Equal((OrderExceptionKinds.ChangedAfterImport, "58", 1), (exception.Kind, exception.ExternalRef, exception.Occurrences));
        Assert.Contains("17.42", exception.Reason);
        Assert.Contains("One cap returned", exception.Reason);
        Assert.Equal(_h.Pos.Transactions["woo-58"].InvoiceNo.ToString(), exception.PosRef);

        refunded["status"] = "cancelled";
        await _h.ImportAsync(58);

        exception = Assert.Single(await _h.OpenExceptionsAsync());
        Assert.Equal(2, exception.Occurrences);
        Assert.Contains("is now 'cancelled'", exception.Reason);
        Assert.Single(_h.Pos.Transactions);
        Assert.Single(_h.Pos.Payments);
        Assert.Equal("cancelled", (await _h.OrderRowAsync(58))!.ExternalStatus);
    }

    [Fact]
    public async Task CancelledBeforeThePaymentPosted_DoesNotTender()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        await _h.LinkAsync(16, CapInvNo);
        _h.Pos.FailNext(HttpMethod.Post, "/api/v2/transactions/payments");
        await Assert.ThrowsAsync<PosApiException>(() => _h.ImportAsync(58));

        _h.Woo.Order(58)["status"] = "cancelled";
        await _h.ImportAsync(58);

        Assert.Empty(_h.Pos.Payments);
        Assert.Equal(OrderExceptionKinds.ChangedAfterImport, Assert.Single(await _h.OpenExceptionsAsync()).Kind);
    }

    private static (int ProductId, decimal Quantity, decimal Price, decimal LineTotal, decimal Tax) LineFigures(JsonNode line) =>
        ((int)line["productId"]!, (decimal)line["quantity"]!, (decimal)line["price"]!, (decimal)line["lineTotal"]!, (decimal)line["taxAmount"]!);
}
