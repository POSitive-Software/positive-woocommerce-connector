using PA.Connectors.Core.Orders;
using PA.Connectors.Core.Pos;
using PA.Connectors.WooCommerce.Models;
using PA.Connectors.WooCommerce.Orders;
using Xunit;

namespace PA.Tests.Connectors.Orders;

/// <summary>The pure pieces: customer keys and fields, widths, and the totals reconciliation.</summary>
public class OrderMappingTests
{
    private static WooOrder Order(string fixture) => WooFixtures.Read<WooOrder>(fixture);

    [Fact]
    public void CustomerKeys_RegisteredById_GuestByLowercasedTrimmedEmail()
    {
        Assert.Equal("woo-c3", WooOrderMapper.LocalCustomerId(Order("order-registered-coupon-shipping-fee.json")));
        var guest = Order("order-guest-paid.json");
        guest.Billing.Email = "  Gina.Guest@Example.COM ";
        Assert.Equal("woo-ggina.guest@example.com", WooOrderMapper.LocalCustomerId(guest));
        guest.Billing.Email = "";
        Assert.Null(WooOrderMapper.LocalCustomerId(guest));
    }

    [Fact]
    public void GuestKey_OverTheApiWidth_IsHashed_AndStable()
    {
        var guest = Order("order-guest-paid.json");
        guest.Billing.Email = new string('a', 120) + "@example.com";
        var key = WooOrderMapper.LocalCustomerId(guest)!;
        Assert.True(key.Length <= 100);
        Assert.StartsWith("woo-gh", key);
        guest.Billing.Email = guest.Billing.Email.ToUpperInvariant();
        Assert.Equal(key, WooOrderMapper.LocalCustomerId(guest));
    }

    [Fact]
    public void Customer_RespectsWidths_AndBusinessesFileTheWorkPhone()
    {
        var order = Order("order-guest-paid.json");
        order.Billing.FirstName = new string('F', 40);
        order.Billing.LastName = new string('L', 40);
        var person = WooOrderMapper.Customer(order, "woo-gx");
        Assert.Equal(30, person.LastName!.Length);
        Assert.True(person.LastName.Length + 2 + person.FirstName!.Length <= 60); // "last, first" display name
        Assert.Equal("P", person.CustomerType);
        Assert.NotNull(person.HomePhone);

        order.Billing.Company = "Acme Widgets";
        var business = WooOrderMapper.Customer(order, "woo-gx");
        Assert.Equal(("B", "Acme Widgets", "509-555-0199", (string?)null),
            (business.CustomerType, business.Company, business.WorkPhone, business.HomePhone));
    }

    [Fact]
    public void Customer_WithNoName_FallsBackToTheEmailName()
    {
        var order = Order("order-guest-paid.json");
        order.Billing.FirstName = order.Billing.LastName = "";
        Assert.Equal("Gina.Guest", WooOrderMapper.Customer(order, "woo-gx").LastName);
    }

    [Fact]
    public void CustomerChanges_NullWhenNothingDiffers()
    {
        var wanted = WooOrderMapper.Customer(Order("order-guest-paid.json"), "woo-gx");
        var current = wanted with { Email = "GINA.GUEST@EXAMPLE.COM", HomePhone = "555-0199", HomePhoneAreaCode = "509" };
        Assert.Null(WooOrderMapper.CustomerChanges(wanted, current));
        Assert.Equal("Gina", WooOrderMapper.CustomerChanges(wanted, current with { FirstName = "G" })!.FirstName);
    }

    [Fact]
    public void Header_PurchaseOrderNumberIsCutTo30_AndTheNoteTo1000()
    {
        var order = Order("order-guest-paid.json");
        order.Number = new string('9', 40);
        order.CustomerNote = new string('n', 2000);
        var t = WooOrderMapper.Transaction(order, "woo-gx", [new WooLineResolution(1, 16, "woo-cap", "Cap", 1001, null)], null, TimeZoneInfo.Utc);
        Assert.Equal(30, t.PurchaseOrderNumber!.Length);
        Assert.Equal(1000, t.Note!.Length);
    }

    [Fact]
    public void LocalPickup_IsDeliveryMethodC_WithNoShipTo()
    {
        var order = Order("order-registered-coupon-shipping-fee.json");
        order.ShippingLines[0].MethodId = "local_pickup";
        var t = WooOrderMapper.Transaction(order, "woo-c3",
            [new WooLineResolution(3, 27, null, null, 1, null), new WooLineResolution(4, 14, null, null, 2, null)], null, TimeZoneInfo.Utc);
        Assert.Equal("C", t.DeliveryMethod);
        Assert.Null(t.ShippingAddress);
    }

    [Fact]
    public void StoreLocal_ConvertsWoosGmtToTheStoreZone_WithNoOffset()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("pst", TimeSpan.FromHours(-8), "pst", "pst");
        var local = WooOrderMapper.StoreLocal(new DateTime(2026, 10, 8, 18, 12, 1), zone)!.Value;
        Assert.Equal(new DateTime(2026, 10, 8, 10, 12, 1), local);
        Assert.Equal(DateTimeKind.Unspecified, local.Kind);
    }

    [Theory]
    [InlineData(32.00, 3, 10.6667)]
    [InlineData(37.80, 1, 37.80)]
    [InlineData(5.00, 0, 5.00)]
    public void UnitPrice_IsTotalOverQuantity_ToFourPlaces(decimal total, decimal quantity, decimal expected) =>
        Assert.Equal(expected, WooOrderMapper.UnitPrice(total, quantity));

    // --- reconciliation ----------------------------------------------------------

    private static PosTransaction Tx(decimal total, decimal tax, params (decimal Line, decimal Tax)[] lines) => new()
    {
        LocalTransactionId = "woo-1",
        Total = total,
        TaxAmount = tax,
        Lines = lines.Select(l => new PosTransactionLine { LineTotal = l.Line, TaxAmount = l.Tax }).ToList(),
    };

    [Fact]
    public void Reconcile_Order59_Balances()
    {
        var result = OrderReconciliation.Check(Tx(82.70m, 7.35m, (37.80m, 3.36m), (32.40m, 2.88m), (10m, 0.89m), (2.50m, 0.22m)),
            90.05m, 7.35m);
        Assert.True(result.Balanced, result.Describe());
    }

    [Fact]
    public void Reconcile_ToleratesACent_ButNotMore()
    {
        Assert.True(OrderReconciliation.Check(Tx(10m, 1m, (10.01m, 1m)), 11m, 1m).Balanced);
        var off = OrderReconciliation.Check(Tx(10m, 1m, (10.02m, 1m)), 11m, 1m);
        Assert.False(off.Balanced);
        Assert.Contains("lines add up to 10.02", off.Describe());
    }

    [Fact]
    public void Reconcile_LineTaxMayDriftHalfACentPerLine()
    {
        // 6 lines rounded one by one: up to 0.03 apart from the order tax.
        var lines = Enumerable.Repeat((1m, 0.105m), 6).ToArray();
        Assert.True(OrderReconciliation.Check(Tx(6m, 0.66m, lines), 6.66m, 0.66m).Balanced);
        Assert.False(OrderReconciliation.Check(Tx(6m, 0.70m, lines), 6.70m, 0.70m).Balanced);
    }

    [Fact]
    public void Reconcile_HeaderTaxMustBeTheOrderTax()
    {
        var result = OrderReconciliation.Check(Tx(10m, 0.5m, (10m, 1m)), 11m, 1m);
        Assert.False(result.Balanced);
        Assert.Contains("header tax 0.50", result.Describe());
    }
}
