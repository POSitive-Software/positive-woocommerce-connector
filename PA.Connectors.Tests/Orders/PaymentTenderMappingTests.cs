using PA.Connectors.Core;
using Xunit;

namespace PA.Tests.Connectors.Orders;

/// <summary>
/// The paymentTenders setting: each Woo payment_method posts its payment as the
/// POSitive tender it is mapped to; unmapped methods keep the old behaviour (charge card, tender R).
/// Same harness as <see cref="WooOrderImportTests"/>.
/// </summary>
public sealed class PaymentTenderMappingTests : IAsyncLifetime
{
    private const int CapInvNo = 1001;
    private OrderImportHarness _h = null!;

    public async Task InitializeAsync() => _h = await OrderImportHarness.CreateAsync();

    public async Task DisposeAsync() => await _h.DisposeAsync();

    // --- the mapping itself --------------------------------------------------------------

    [Fact]
    public void Default_IsEmpty_AndEveryMethodPostsAsCard()
    {
        var settings = new ConnectorSettings();
        Assert.Empty(settings.PaymentTenders);
        Assert.Equal(PaymentTenders.Card, PaymentTenders.For(settings, "stripe"));
        Assert.Equal(PaymentTenders.Card, PaymentTenders.For(settings, null));
        Assert.Equal("R", PaymentTenders.PosPaymentType(PaymentTenders.For(settings, "stripe")));
    }

    [Theory]
    [InlineData("cheque", "check", "C")]
    [InlineData("CHEQUE", "check", "C")]   // payment_method ids compare case-insensitively
    [InlineData(" bacs ", "onAccount", "A")]
    [InlineData("stripe", "card", "R")]    // not mapped
    [InlineData("", "card", "R")]
    public void For_MapsTheMethod_ToItsTenderAndApiPaymentType(string method, string tender, string paymentType)
    {
        var settings = new ConnectorSettings
        {
            PaymentTenders = new() { ["cheque"] = "check", ["bacs"] = "onaccount" },
        };

        Assert.Equal(tender, PaymentTenders.For(settings, method));
        Assert.Equal(paymentType, PaymentTenders.PosPaymentType(PaymentTenders.For(settings, method)));
    }

    [Fact]
    public void ConfigFile_ReadsTheMap_AndValidateRefusesAnUnknownTender()
    {
        var parsed = ConnectorConfigFile.Parse("""
            { "connectors": [ { "id": "s", "storeUrl": "https://s.example.com", "paymentTenders": { "cheque": "check" } } ] }
            """).Connectors[0];
        Assert.Equal("check", parsed.PaymentTenders["cheque"]);
        Assert.Empty(ConnectorConfigFile.Validate(parsed));

        var cash = parsed with { PaymentTenders = new() { ["cod"] = "cash" } };
        var error = Assert.Single(ConnectorConfigFile.Validate(cash));
        Assert.StartsWith("paymentTenders ", error, StringComparison.Ordinal);
        Assert.Contains("'cash'", error);
        Assert.Equal(PaymentTenders.Card, PaymentTenders.For(cash, "cod")); // never posted as an unknown type
    }

    // --- posting -----------------------------------------------------------------------------

    [Fact]
    public async Task UnmappedMethod_PostsChargeCard_AsBefore()
    {
        _h.Rebuild(s => s with { PaymentTenders = new() { ["cheque"] = "check" } });
        _h.Woo.AddOrder("order-guest-paid.json"); // payment_method stripe
        await _h.LinkAsync(16, CapInvNo);

        await _h.ImportAsync(58);

        Assert.Equal("R", Assert.Single(_h.Pos.Payments).PaymentType);
    }

    [Theory]
    [InlineData("check", "C")]
    [InlineData("onAccount", "A")]
    public async Task MappedMethod_PostsItsTender(string tender, string paymentType)
    {
        _h.Rebuild(s => s with { PaymentTenders = new() { ["stripe"] = tender } });
        _h.Woo.AddOrder("order-guest-paid.json");
        await _h.LinkAsync(16, CapInvNo);

        await _h.ImportAsync(58);

        var payment = Assert.Single(_h.Pos.Payments);
        Assert.Equal((paymentType, 34.85m, "woo-58-pay"), (payment.PaymentType, payment.Amount, payment.LocalPaymentId));
    }

    [Fact]
    public async Task PayOnCompletionMethod_StillPaysOnlyWhenCompleted_WithItsMappedTender()
    {
        _h.Rebuild(s => s with { PaymentTenders = new() { ["cod"] = "check" } });
        var order = _h.Woo.AddOrder("order-guest-paid.json", o =>
        {
            o["payment_method"] = "cod";
            o["date_paid"] = null;
            o["date_paid_gmt"] = null;
        });
        await _h.LinkAsync(16, CapInvNo);

        await _h.ImportAsync(58);
        Assert.Single(_h.Pos.Transactions);
        Assert.Empty(_h.Pos.Payments);

        order["status"] = "completed";
        await _h.ImportAsync(58);
        Assert.Equal("C", Assert.Single(_h.Pos.Payments).PaymentType);
    }
}
