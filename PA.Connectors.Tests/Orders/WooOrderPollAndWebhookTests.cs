using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Secrets;
using PA.Connectors.Core.Stores;
using PA.Connectors.WooCommerce.Orders;
using Xunit;

namespace PA.Tests.Connectors.Orders;

/// <summary>order.poll (backstop cursor) and webhook.health (self-registration) against the fake store.</summary>
public sealed class WooOrderPollAndWebhookTests : IAsyncLifetime
{
    private const string BaseUrl = "https://shop1.ngrok.app";
    private const string DeliveryUrl = BaseUrl + "/hooks/woocommerce/shop1";
    private OrderImportHarness _h = null!;

    public async Task InitializeAsync()
    {
        _h = await OrderImportHarness.CreateAsync();
        _h.Clock.Now = new DateTime(2026, 10, 8, 18, 30, 0, DateTimeKind.Utc); // the fixtures were modified at 18:12:01
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<string?> CursorAsync() => _h.Stores.Cursors.GetAsync(OrderImportHarness.Shop, ConnectorCursorNames.OrdersModifiedAfter);

    // --- order.poll -------------------------------------------------------------

    [Fact]
    public void RecurringJobs_PollEveryOrderPollMinutes_AndHealthHourly()
    {
        _h.Rebuild(s => s with { OrderPollMinutes = 7 });

        Assert.Contains(_h.Import.RecurringJobs, j => j.Kind == ConnectorJobKinds.OrderPoll && j.Interval == TimeSpan.FromMinutes(7));
        Assert.Contains(_h.Import.RecurringJobs, j => j.Kind == ConnectorJobKinds.WebhookHealth && j.Interval == TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Poll_EnqueuesImportableOrders_AdvancesTheCursorToWoosOwnTime_AndAsksForAHealthCheckOnce()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        _h.Woo.AddOrder("order-registered-coupon-shipping-fee.json");
        _h.Woo.AddOrder("order-guest-paid.json", o => { o["id"] = 70; o["status"] = "pending"; });

        await _h.RunAsync(ConnectorJobKinds.OrderPoll);

        // First poll: no cursor → look back an hour (17:30), less the overlap.
        Assert.Equal("2026-10-08T17:25:00", Assert.Single(_h.Woo.ModifiedAfterQueries));
        var jobs = await _h.DrainJobsAsync();
        Assert.Equal(["order.import:58", "order.import:59", "webhook.health:"],
            jobs.Select(j => $"{j.Kind}:{j.ResourceKey}").Order());
        Assert.Equal("2026-10-08T18:12:01.0000000Z", await CursorAsync());

        await _h.RunAsync(ConnectorJobKinds.OrderPoll);

        Assert.Equal("2026-10-08T18:07:01", _h.Woo.ModifiedAfterQueries[1]); // cursor − overlap: re-reads, never skips
        Assert.DoesNotContain(await _h.DrainJobsAsync(), j => j.Kind == ConnectorJobKinds.WebhookHealth);
    }

    [Fact]
    public async Task Poll_SkipsOrdersTheStoreAlreadyHasAtThisModifiedTime_AndPicksUpALaterChange()
    {
        _h.Woo.AddOrder("order-guest-paid.json");
        await _h.LinkAsync(16, 1001);
        await _h.ImportAsync(58);
        await _h.DrainJobsAsync();

        await _h.RunAsync(ConnectorJobKinds.OrderPoll);
        Assert.DoesNotContain(await _h.DrainJobsAsync(), j => j.Kind == ConnectorJobKinds.OrderImport);

        // A refund at 18:40 (store time) re-lists it.
        _h.Woo.AddOrder("order-refund.json", o => o["date_modified_gmt"] = "2026-10-08T18:40:00");
        await _h.RunAsync(ConnectorJobKinds.OrderPoll);

        Assert.Equal("58", Assert.Single(await _h.DrainJobsAsync(), j => j.Kind == ConnectorJobKinds.OrderImport).ResourceKey);
        Assert.Equal("2026-10-08T18:40:00.0000000Z", await CursorAsync());
    }

    [Fact]
    public async Task Poll_NothingNew_KeepsTheCursor_EvenWhenThisMachinesClockIsAhead()
    {
        await _h.Stores.Cursors.SetAsync(OrderImportHarness.Shop, ConnectorCursorNames.OrdersModifiedAfter, "2026-10-08T18:00:00.0000000Z");
        _h.Clock.Now = _h.Clock.Now.AddHours(5);

        await _h.RunAsync(ConnectorJobKinds.OrderPoll);

        Assert.Equal("2026-10-08T18:00:00.0000000Z", await CursorAsync());
        Assert.Equal("2026-10-08T17:55:00", Assert.Single(_h.Woo.ModifiedAfterQueries));
    }

    [Fact]
    public async Task Poll_OnHoldIsSkipped_UnlessImportOnHold()
    {
        _h.Woo.AddOrder("order-guest-paid.json", o => o["status"] = "on-hold");

        await _h.RunAsync(ConnectorJobKinds.OrderPoll);
        Assert.DoesNotContain(await _h.DrainJobsAsync(), j => j.Kind == ConnectorJobKinds.OrderImport);

        _h.Rebuild(s => s with { ImportOnHold = true });
        await _h.Stores.Cursors.SetAsync(OrderImportHarness.Shop, ConnectorCursorNames.OrdersModifiedAfter, null);
        await _h.RunAsync(ConnectorJobKinds.OrderPoll);
        Assert.Contains(await _h.DrainJobsAsync(), j => j.Kind == ConnectorJobKinds.OrderImport && j.ResourceKey == "58");
    }

    // --- webhook.health ----------------------------------------------------------

    [Fact]
    public async Task Health_WithoutWebhookBaseUrl_ManagesNothing()
    {
        await _h.RunAsync(ConnectorJobKinds.WebhookHealth);

        Assert.Empty(_h.Woo.Requests);
        Assert.Empty(_h.Secrets.Values);
    }

    [Fact]
    public async Task Health_CreatesBothWebhooks_WithAGeneratedStoredSecret_ThenLeavesThemAlone()
    {
        _h.Rebuild(s => s with { WebhookBaseUrl = BaseUrl + "/" });

        await _h.RunAsync(ConnectorJobKinds.WebhookHealth);

        var secret = _h.Secrets.Get(ConnectorSecretNames.WooWebhookSecret("shop1"));
        Assert.False(string.IsNullOrEmpty(secret));
        Assert.True(secret!.Length >= 40);
        Assert.Equal(["order.created", "order.updated"], _h.Woo.Webhooks.Select(h => (string)h["topic"]!).Order());
        Assert.All(_h.Woo.Webhooks, h =>
        {
            Assert.Equal(DeliveryUrl, (string?)h["delivery_url"]);
            Assert.Equal(secret, (string?)h["secret"]);
            Assert.Equal("active", (string?)h["status"]);
        });

        var before = _h.Woo.Requests.Count;
        await _h.RunAsync(ConnectorJobKinds.WebhookHealth);
        Assert.All(_h.Woo.Requests.Skip(before), r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal(2, _h.Woo.Webhooks.Count);
        Assert.Empty(await _h.OpenExceptionsAsync());
    }

    [Fact]
    public async Task Health_ReactivatesADisabledWebhook_FixesAWrongSecret_AndRaisesWebhookUnhealthy()
    {
        _h.Rebuild(s => s with { WebhookBaseUrl = BaseUrl });
        _h.Secrets.Set(ConnectorSecretNames.WooWebhookSecret("shop1"), "our-secret");
        var disabled = _h.Woo.AddWebhook("order.updated", DeliveryUrl, "disabled", "our-secret");
        var stale = _h.Woo.AddWebhook("order.created", DeliveryUrl, "active", "old-secret");
        var someoneElse = _h.Woo.AddWebhook("order.updated", "https://other.example/hook", "disabled", "x");

        await _h.RunAsync(ConnectorJobKinds.WebhookHealth);

        Assert.Equal("active", (string?)disabled["status"]);
        Assert.Equal("our-secret", (string?)stale["secret"]);
        Assert.Equal("disabled", (string?)someoneElse["status"]); // not ours: untouched
        Assert.Equal(3, _h.Woo.Webhooks.Count);
        var exception = Assert.Single(await _h.OpenExceptionsAsync());
        Assert.Equal(ConnectorExceptionKinds.WebhookUnhealthy, exception.Kind);
        Assert.Contains("order.updated", exception.Reason);
        Assert.Equal("our-secret", _h.Secrets.Get(ConnectorSecretNames.WooWebhookSecret("shop1")));
    }

    [Fact]
    public void DeliveryUrl_IsBuiltFromTheBase_OrNullWhenUnusable()
    {
        Assert.Equal(DeliveryUrl, WooWebhookHealth.DeliveryUrl(new() { Id = "shop1", WebhookBaseUrl = " https://shop1.ngrok.app/ " }));
        Assert.Null(WooWebhookHealth.DeliveryUrl(new() { Id = "shop1", WebhookBaseUrl = "" }));
        Assert.Null(WooWebhookHealth.DeliveryUrl(new() { Id = "shop1", WebhookBaseUrl = "ftp://x" }));
    }
}
