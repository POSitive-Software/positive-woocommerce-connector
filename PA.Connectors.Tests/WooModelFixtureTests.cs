using System.Text.Json;
using PA.Connectors.WooCommerce;
using PA.Connectors.WooCommerce.Models;
using Xunit;

namespace PA.Tests.Connectors;

/// <summary>The Woo read models against every recorded dev-store response (spike S0bc).</summary>
public class WooModelFixtureTests
{
    /// <summary>Every fixture and the test that reads it — a new fixture must be added here.</summary>
    public static readonly string[] KnownFixtures =
    [
        "order-guest-paid.json",
        "order-refund.json",
        "order-registered-coupon-shipping-fee.json",
        "product-simple.json",
        "product-variable.json",
        "variation.json",
        "webhook-order-updated.body.json",
        "webhook-ping.txt",
    ];

    [Fact]
    public void EveryFixture_IsCopiedToOutput_AndCovered()
    {
        Assert.True(Directory.Exists(WooFixtures.Directory), $"fixtures not copied to {WooFixtures.Directory}");
        var onDisk = Directory.GetFiles(WooFixtures.Directory).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(KnownFixtures.Order(StringComparer.Ordinal), onDisk);
    }

    [Fact]
    public void SimpleProduct_MoneyStrings_StockAndDates()
    {
        var p = WooFixtures.Read<WooProduct>("product-simple.json");

        Assert.Equal(16, p.Id);
        Assert.Equal("Cap", p.Name);
        Assert.Equal(WooProductTypes.Simple, p.Type);
        Assert.Equal("publish", p.Status);
        Assert.Equal("woo-cap", p.Sku);
        Assert.Equal(16m, p.Price);
        Assert.Equal(18m, p.RegularPrice);
        Assert.Equal(16m, p.SalePrice);
        Assert.True(p.OnSale);
        Assert.True(p.ManageStock);
        Assert.Equal(49m, p.StockQuantity);
        Assert.Equal(0, p.ParentId);
        Assert.False(p.IsVariation);
        Assert.Empty(p.Variations);
        Assert.Equal(new DateTime(2026, 10, 8, 18, 12, 21, DateTimeKind.Utc), p.DateModifiedUtc);
        Assert.Equal(DateTimeKind.Utc, p.DateModifiedUtc!.Value.Kind);
    }

    [Fact]
    public void VariableProduct_BlankPricesAreNull_VariationsAndAttributes()
    {
        var p = WooFixtures.Read<WooProduct>("product-variable.json");

        Assert.Equal(11, p.Id);
        Assert.Equal(WooProductTypes.Variable, p.Type);
        Assert.Equal("woo-hoodie", p.Sku);
        Assert.Equal(42m, p.Price);
        Assert.Null(p.RegularPrice); // "" on a variable parent
        Assert.Null(p.SalePrice);
        Assert.False(p.ManageStock);
        Assert.Null(p.StockQuantity);
        Assert.Equal([34L, 27, 28, 29], p.Variations);
        Assert.Equal("Hoodies", Assert.Single(p.Categories).Name);
        Assert.Equal(["Blue", "Green", "Red"], p.Attributes.Single(a => a.Slug == "pa_color").Options);
    }

    [Fact]
    public void Variation_ParsesAsVariationAndAsProduct()
    {
        var v = WooFixtures.Read<WooVariation>("variation.json");
        Assert.Equal(27, v.Id);
        Assert.Equal(11, v.ParentId);
        Assert.Equal("woo-hoodie-red", v.Sku);
        Assert.Equal(45m, v.RegularPrice);
        Assert.Equal(42m, v.SalePrice);
        Assert.True(v.ManageStock);
        Assert.Equal(19m, v.StockQuantity);
        Assert.Equal("Red", v.Attributes.Single(a => a.Name == "color").Option);

        // GET /products?sku= hands variations back through the product shape (S0bc).
        var asProduct = WooFixtures.Read<WooProduct>("variation.json");
        Assert.True(asProduct.IsVariation);
        Assert.Equal(11, asProduct.ParentId);
    }

    [Fact]
    public void GuestOrder_Billing_Lines_TaxAndNote()
    {
        var o = WooFixtures.Read<WooOrder>("order-guest-paid.json");

        Assert.Equal(58, o.Id);
        Assert.Equal("58", o.Number);
        Assert.Equal(WooOrderStatuses.Processing, o.Status);
        Assert.True(o.IsGuest);
        Assert.False(o.PricesIncludeTax);
        Assert.Equal(34.85m, o.Total);
        Assert.Equal(2.85m, o.TotalTax);
        Assert.Equal("Gina.Guest@Example.com", o.Billing.Email); // mixed case preserved; the mapper lowercases
        Assert.Equal("300 Guest Rd", o.Billing.Address1);
        Assert.Equal("", o.Billing.Address2);
        Assert.Equal("WA", o.Shipping.State);
        Assert.Equal("Leave at the back door", o.CustomerNote);
        Assert.Equal("Credit card (Stripe)", o.PaymentMethodTitle);
        Assert.Equal(new DateTime(2026, 10, 8, 18, 12, 1, DateTimeKind.Utc), o.DatePaidUtc);

        var line = Assert.Single(o.LineItems);
        Assert.Equal(16, line.ProductId);
        Assert.Equal(0, line.VariationId);
        Assert.Equal(16, line.ItemId);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(32.00m, line.Total);
        Assert.Equal(2.85m, line.TotalTax);
        Assert.Equal(16m, line.Price); // a JSON number, unlike the other amounts
        Assert.Equal("woo-cap", line.Sku);

        var tax = Assert.Single(o.TaxLines);
        Assert.Equal(1, tax.RateId);
        Assert.Equal(8.9m, tax.RatePercent);
        Assert.Empty(o.ShippingLines);
        Assert.Empty(o.Refunds);
    }

    [Fact]
    public void RegisteredOrder_Coupon_Shipping_Fee_ReconcileExactly()
    {
        var o = WooFixtures.Read<WooOrder>("order-registered-coupon-shipping-fee.json");

        Assert.Equal(59, o.Id);
        Assert.Equal(3, o.CustomerId);
        Assert.False(o.IsGuest);
        Assert.Equal(90.05m, o.Total);
        Assert.Equal(7.35m, o.TotalTax);

        var variationLine = o.LineItems.Single(l => l.VariationId != 0);
        Assert.Equal(27, variationLine.ItemId);
        Assert.Equal("Hoodie", variationLine.ParentName);
        Assert.Equal(42.00m, variationLine.Subtotal);
        Assert.Equal(37.80m, variationLine.Total);
        Assert.Equal(37.8m, variationLine.Price);
        Assert.Equal("red", variationLine.MetaData.Single(m => m.Key == "pa_color").Value.GetString());

        var coupon = Assert.Single(o.CouponLines);
        Assert.Equal("spike10", coupon.Code);
        Assert.Equal(7.80m, coupon.Discount);
        Assert.Equal(10m, coupon.NominalAmount);

        var shipping = Assert.Single(o.ShippingLines);
        Assert.Equal("Flat rate", shipping.MethodTitle);
        Assert.Equal(10.00m, shipping.Total);
        Assert.Equal(0.89m, shipping.TotalTax);
        Assert.Null(Assert.Single(shipping.Taxes).Subtotal); // "" on shipping/fee taxes

        var fee = Assert.Single(o.FeeLines);
        Assert.Equal("Gift wrap", fee.Name);
        Assert.Null(fee.Amount); // ""
        Assert.Equal(2.50m, fee.Total);

        // Spike S0bc: lines 37.80 + 32.40 + shipping 10.00 + fee 2.50 = total − total_tax.
        var exTax = o.LineItems.Sum(l => l.Total) + o.ShippingLines.Sum(s => s.Total) + o.FeeLines.Sum(f => f.Total);
        Assert.Equal(82.70m, exTax);
        Assert.Equal(o.Total - o.TotalTax, exTax);
        Assert.Equal(o.TotalTax,
            o.LineItems.Sum(l => l.TotalTax) + o.ShippingLines.Sum(s => s.TotalTax) + o.FeeLines.Sum(f => f.TotalTax));
    }

    [Fact]
    public void Refund_OrderSummaryAndFullRefund()
    {
        var order = WooFixtures.ReadProperty<WooOrder>("order-refund.json", "order");
        Assert.Equal(WooOrderStatuses.Processing, order.Status); // a partial refund does not change the status
        var summary = Assert.Single(order.Refunds);
        Assert.Equal(60, summary.Id);
        Assert.Equal(-17.42m, summary.Total);
        Assert.Equal(-1.42m, summary.TotalTax);

        var refund = WooFixtures.ReadProperty<WooRefund>("order-refund.json", "refund");
        Assert.Equal(60, refund.Id);
        Assert.Equal(17.42m, refund.Amount);
        Assert.False(refund.RefundedPayment);
        var line = Assert.Single(refund.LineItems);
        Assert.Equal(-1m, line.Quantity);
        Assert.Equal(-16.00m, line.Total);
        Assert.Equal("woo-cap", line.Sku);
        Assert.Equal(-1.42m, Assert.Single(refund.TaxLines).TaxTotal);
    }

    [Fact]
    public void WebhookBody_IsAFullOrder()
    {
        var o = WooFixtures.Read<WooOrder>("webhook-order-updated.body.json");
        Assert.Equal(59, o.Id);
        Assert.Equal(2, o.LineItems.Count);
        Assert.Equal("updated by spike", o.CustomerNote);
        Assert.Equal(new DateTime(2026, 10, 8, 18, 13, 14, DateTimeKind.Utc), o.DateModifiedUtc);
    }

    [Theory]
    [InlineData("\"37.80\"", 37.80)]
    [InlineData("37.8", 37.8)]
    [InlineData("\"-17.42\"", -17.42)]
    [InlineData("\"0\"", 0)]
    [InlineData("\"1e2\"", 100)]
    public void Money_ParsesInvariant(string json, double expected)
    {
        Assert.Equal((decimal)expected, JsonSerializer.Deserialize<decimal>(json, WooJson.Options));
    }

    [Fact]
    public void Money_IsCultureIndependent()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal(37.80m, JsonSerializer.Deserialize<decimal>("\"37.80\"", WooJson.Options));
            Assert.Equal("\"16.5\"", JsonSerializer.Serialize(16.5m, WooJson.Options));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Money_BlankIsNull_GarbageThrows()
    {
        Assert.Null(JsonSerializer.Deserialize<decimal?>("\"\"", WooJson.Options));
        Assert.Null(JsonSerializer.Deserialize<decimal?>("null", WooJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<decimal?>("\"12,50\"", WooJson.Options));
    }

    [Fact]
    public void ProductWrite_SendsMoneyAsStrings_AndOmitsNulls()
    {
        var json = JsonSerializer.Serialize(new WooProductWrite
        {
            Id = 16, RegularPrice = WooJson.FormatMoney(18.5m), SalePrice = "", ManageStock = true, StockQuantity = 7,
        }, WooJson.Options);

        Assert.Equal("""{"id":16,"regular_price":"18.5","sale_price":"","manage_stock":true,"stock_quantity":7}""", json);
    }
}
