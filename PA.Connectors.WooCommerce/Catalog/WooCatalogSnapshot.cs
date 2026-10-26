using PA.Connectors.Core.Catalog;
using PA.Connectors.WooCommerce.Models;

namespace PA.Connectors.WooCommerce.Catalog;

/// <summary>
/// One Woo product or variation as the catalog push sees it. <see cref="Kind"/> is
/// <see cref="CatalogItemTypes.Simple"/> or <see cref="CatalogItemTypes.Variation"/> for the
/// items the connector links and pushes; variable parents, grouped and external products
/// carry their Woo type and are never linked (their SKU still counts for uniqueness).
/// </summary>
internal sealed record WooItem(
    long Id,
    long ParentId,
    string Kind,
    string? Sku,
    string? Status,
    decimal? RegularPrice,
    decimal? SalePrice,
    bool ManageStock,
    decimal? StockQuantity,
    string? Name,
    string? Description)
{
    public bool Linkable => Kind is CatalogItemTypes.Simple or CatalogItemTypes.Variation;

    public bool IsVariation => Kind == CatalogItemTypes.Variation;

    public bool Trashed => string.Equals(Status, "trash", StringComparison.OrdinalIgnoreCase);

    public static WooItem From(WooProduct p)
    {
        var kind = p.IsVariation || p.ParentId != 0 && string.IsNullOrEmpty(p.Type)
            ? CatalogItemTypes.Variation
            : string.Equals(p.Type, WooProductTypes.Simple, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(p.Type)
                ? CatalogItemTypes.Simple
                : p.Type!.ToLowerInvariant();
        return new WooItem(p.Id, kind == CatalogItemTypes.Variation ? p.ParentId : 0, kind, p.Sku, p.Status,
            p.RegularPrice, p.SalePrice, p.ManageStock, p.StockQuantity, p.Name, p.Description);
    }

    public static WooItem From(WooVariation v, long parentId) =>
        new(v.Id, parentId, CatalogItemTypes.Variation, v.Sku, v.Status,
            v.RegularPrice, v.SalePrice, v.ManageStock, v.StockQuantity, v.Name, v.Description);
}

/// <summary>Every product and variation of the store, read once per full run (match / reconcile).</summary>
internal sealed class WooSnapshot
{
    private readonly Dictionary<string, List<WooItem>> _bySku;

    private WooSnapshot(List<WooItem> items)
    {
        Items = items;
        ById = items.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        _bySku = items.Where(i => CatalogMapper.SkuKey(i.Sku) is not null)
            .GroupBy(i => CatalogMapper.SkuKey(i.Sku)!)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    public IReadOnlyList<WooItem> Items { get; }

    public IReadOnlyDictionary<long, WooItem> ById { get; }

    public IEnumerable<WooItem> Linkable => Items.Where(i => i.Linkable);

    /// <summary>Every item (any type) whose SKU equals <paramref name="skuKey"/> (a <see cref="CatalogMapper.SkuKey"/>).</summary>
    public IReadOnlyList<WooItem> WithSku(string? skuKey) =>
        skuKey is not null && _bySku.TryGetValue(skuKey, out var items) ? items : [];

    /// <summary>
    /// Pages every product (Woo's default status "any": draft, pending, private and published,
    /// not trash), then the variations of each variable product. Descriptions are only fetched
    /// when they are compared (overwriteTitleDescription).
    /// </summary>
    public static async Task<WooSnapshot> LoadAsync(WooClient client, bool withText, CancellationToken cancellationToken)
    {
        // manage_stock is left out on purpose: in-sync is judged on stock_quantity, which Woo
        // reports as null whenever the item does not manage its own stock.
        // The name is small and the match review shows it; the description only when it is compared.
        var fields = "id,type,status,sku,parent_id,regular_price,sale_price,stock_quantity,name" + (withText ? ",description" : "");
        var items = new List<WooItem>();
        var variableIds = new List<long>();
        await foreach (var product in client.GetAllProductsAsync(
                           new Dictionary<string, string> { ["_fields"] = fields + ",variations" }, cancellationToken))
        {
            var item = WooItem.From(product);
            if (item.Trashed)
                continue;
            items.Add(item with { ManageStock = product.StockQuantity is not null });
            if (string.Equals(product.Type, WooProductTypes.Variable, StringComparison.OrdinalIgnoreCase))
                variableIds.Add(product.Id);
        }

        foreach (var parentId in variableIds)
        {
            foreach (var variation in await client.GetVariationsAsync(parentId, cancellationToken))
            {
                var item = WooItem.From(variation, parentId);
                if (!item.Trashed)
                    items.Add(item with { ManageStock = variation.StockQuantity is not null });
            }
        }
        return new WooSnapshot(items);
    }

    /// <summary>For tests and single lookups.</summary>
    public static WooSnapshot Of(IEnumerable<WooItem> items) => new(items.ToList());
}
