using System.Globalization;
using PA.Connectors.Core;
using PA.Connectors.Core.Catalog;
using PA.Connectors.Core.Pos;
using PA.Connectors.WooCommerce;
using Xunit;

namespace PA.Tests.Connectors.Catalog;

/// <summary>The pure catalog pieces: money strings, the POSitive → Woo value mapping, the push hash.</summary>
public class CatalogValuesTests
{
    private static readonly ConnectorSettings Settings = new() { Id = "shop1" };

    private static PosProduct Product(decimal? price, decimal? sale, decimal? live, decimal? available = 100) => new(
        1, null, "sku-1", "Name", "Long", null, true, available, null, null,
        [new PosPrice(1, price, sale), new PosPrice(2, 30m, 25m)], 1, live);

    [Fact]
    public void Money_IsInvariant_RoundedToTheStoreDecimals_EvenUnderACommaCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            Assert.Equal("18.50", CatalogMoney.Format(18.5m, 2));
            Assert.Equal("1234.57", CatalogMoney.Format(1234.565m, 2));
            Assert.Equal("19", CatalogMoney.Format(18.5m, 0));
            Assert.Equal("0.125", CatalogMoney.Format(0.125m, 3));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Map_SaleOnlyWhenBelowThePrice_StockFromAvailableLiveLessBuffer()
    {
        var values = CatalogMapper.Map(Product(10m, 8m, 4.7m), Settings with { StockBuffer = 1 }, 2, includeText: false).Values!;
        Assert.Equal((10m, (decimal?)8m, 3), (values.RegularPrice, values.SalePrice, values.StockQuantity));
        Assert.Null(values.Name);

        Assert.Null(CatalogMapper.Map(Product(10m, 10m, 1), Settings, 2, false).Values!.SalePrice); // list's "no sale"
        Assert.Null(CatalogMapper.Map(Product(10m, 0m, 1), Settings, 2, false).Values!.SalePrice);
        Assert.Null(CatalogMapper.Map(Product(10m, 12m, 1), Settings, 2, false).Values!.SalePrice);
        Assert.Equal(0, CatalogMapper.Map(Product(10m, null, -4m), Settings, 2, false).Values!.StockQuantity);
        Assert.Equal(25m, CatalogMapper.Map(Product(10m, null, 1), Settings with { PriceLevel = 2 }, 2, false).Values!.SalePrice);
    }

    [Fact]
    public void Map_RefusesNoPrice_AndNoAvailableLive()
    {
        Assert.Contains("level 1", CatalogMapper.Map(Product(null, null, 1), Settings, 2, false).Error);
        Assert.Contains("availableLive", CatalogMapper.Map(Product(10m, null, null, available: 50), Settings, 2, false).Error);
    }

    [Fact]
    public void Hash_ChangesWithPushedValues_AndIgnoresTextUnlessTextIsPushed()
    {
        var a = new CatalogPushValues(10m, null, 3, "A", "x");
        Assert.Equal(a.Hash(2, false), (a with { Name = "B" }).Hash(2, false));
        Assert.NotEqual(a.Hash(2, true), (a with { Name = "B" }).Hash(2, true));
        Assert.NotEqual(a.Hash(2, false), (a with { StockQuantity = 4 }).Hash(2, false));
        Assert.NotEqual(a.Hash(2, false), (a with { SalePrice = 9m }).Hash(2, false));
        Assert.Equal(a.Hash(2, false), (a with { RegularPrice = 10.00m }).Hash(2, false));
    }

    [Fact]
    public void ManageStock_Parent_ReadsAsFalse()
    {
        var variation = System.Text.Json.JsonSerializer.Deserialize<PA.Connectors.WooCommerce.Models.WooVariation>(
            """{"id":27,"manage_stock":"parent","stock_quantity":null}""", WooJson.Options)!;
        Assert.False(variation.ManageStock);
    }
}
