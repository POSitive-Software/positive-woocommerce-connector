using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Pos;

namespace PA.Connectors.Core.Catalog;

/// <summary>Exception kinds the catalog push raises (ConnectorException.Kind), on top of <see cref="Stores.ConnectorExceptionKinds"/>.</summary>
public static class CatalogExceptionKinds
{
    /// <summary>A platform product that matches no POSitive web item (or has no SKU). It stays unmanaged. Ref = platform id.</summary>
    public const string UnmatchedPlatform = "catalog_unmatched_woo";

    /// <summary>A POSitive web item the connector will not create (a matrix item, or its SKU is held by a product type it does not link). Ref = "inv:&lt;invNo&gt;".</summary>
    public const string UnmatchedPos = "catalog_unmatched_pos";

    /// <summary>A SKU that matches more than one item on either side; nothing is linked. Ref = platform id or "inv:&lt;invNo&gt;".</summary>
    public const string Ambiguous = "catalog_ambiguous";

    /// <summary>The POSitive link endpoint refused the link (link_exists / product_not_found). Ref = platform id.</summary>
    public const string LinkConflict = "catalog_link_conflict";

    /// <summary>Creating the item on the platform failed. Ref = "inv:&lt;invNo&gt;".</summary>
    public const string CreateFailed = "catalog_create_failed";

    /// <summary>A linked platform product no longer exists; the item is no longer pushed. Ref = platform id.</summary>
    public const string PlatformMissing = "catalog_woo_missing";

    /// <summary>
    /// A link made by hand (match review / exceptions page) was refused when the job ran — an
    /// exception (someone asked for it and it did not happen), unlike the match's own
    /// <see cref="LinkConflict"/>, which is a review item. Ref = platform id, PosRef = the invNo.
    /// </summary>
    public const string ManualLinkRefused = "catalog_link_refused";

    /// <summary>Every catalog kind, so a later success can close the stale open rows.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        UnmatchedPlatform, UnmatchedPos, Ambiguous, LinkConflict, CreateFailed, PlatformMissing, ManualLinkRefused,
        Stores.ConnectorExceptionKinds.PushFailed,
    };

    /// <summary>
    /// What the match reports for the owner to review (Match review page), as opposed to exceptions
    /// (things that need action). A Woo product with no POSitive match is normal, not an error: these
    /// kinds are never counted as exceptions. Classification is by kind only — no stored data changes.
    /// </summary>
    public static readonly IReadOnlySet<string> Review = new HashSet<string>(StringComparer.Ordinal)
    {
        UnmatchedPlatform, UnmatchedPos, Ambiguous, LinkConflict,
    };

    /// <summary>True for a <see cref="Review"/> kind (match review item, not an exception).</summary>
    public static bool IsReview(string? kind) => kind is not null && Review.Contains(kind);

    /// <summary>The exception ref for a POSitive-side item.</summary>
    public static string PosRef(int invNo) => "inv:" + invNo.ToString(CultureInfo.InvariantCulture);
}

/// <summary><see cref="Stores.ConnectorItem.ExternalType"/> values the catalog push writes.</summary>
public static class CatalogItemTypes
{
    public const string Simple = "simple";
    public const string Variation = "variation";

    /// <summary>The platform product was deleted: the row is kept (so the item is not re-created) but never pushed.</summary>
    public const string Missing = "missing";
}

/// <summary>Money for the platform: decimal only, rounded to the store's decimals, invariant culture.</summary>
public static class CatalogMoney
{
    public const int DefaultDecimals = 2;

    public static decimal Round(decimal value, int decimals) =>
        Math.Round(value, Math.Clamp(decimals, 0, 8), MidpointRounding.AwayFromZero);

    /// <summary>"18.50" for 18.5 at 2 decimals; never a culture's comma, never an exponent.</summary>
    public static string Format(decimal value, int decimals)
    {
        var d = Math.Clamp(decimals, 0, 8);
        return Round(value, d).ToString("F" + d.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// What the connector publishes for one item. Prices are already rounded to the
/// store's decimals; <see cref="SalePrice"/> null = no active sale (sent as "" to
/// remove one). <see cref="Name"/>/<see cref="Description"/> are null unless
/// they are to be sent (create, or overwriteTitleDescription).
/// </summary>
public sealed record CatalogPushValues(
    decimal RegularPrice,
    decimal? SalePrice,
    int StockQuantity,
    string? Name,
    string? Description)
{
    /// <summary>
    /// Hash of exactly the values sent on a push — the skip test. Title and
    /// description count only when <paramref name="includeText"/> (they are
    /// otherwise not pushed, so a create's text must not make the next push differ).
    /// </summary>
    public string Hash(int decimals, bool includeText)
    {
        var canonical = new StringBuilder("v1|")
            .Append(CatalogMoney.Format(RegularPrice, decimals)).Append('|')
            .Append(SalePrice is { } sale ? CatalogMoney.Format(sale, decimals) : "").Append('|')
            .Append(StockQuantity.ToString(CultureInfo.InvariantCulture)).Append('|');
        if (includeText)
            canonical.Append(Name).Append('\u001f').Append(Description);
        else
            canonical.Append('-');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))[..32].ToLowerInvariant();
    }
}

/// <summary>The outcome of mapping one POSitive product: values, or why it cannot be pushed.</summary>
public sealed record CatalogMapResult(CatalogPushValues? Values, string? Error)
{
    public bool Success => Values is not null;
}

/// <summary>
/// POSitive product → platform values (design §3). Pure, so it is tested on its own.
/// <list type="bullet">
/// <item>regular price = the configured price level's price;</item>
/// <item>sale price = that level's sale price when it is active, i.e. more than 0 and below the
/// regular price after rounding (the list reports "no sale" as salePrice = price);</item>
/// <item>stock = availableLive − stockBuffer − pendingPlatformQuantity, whole units rounded down, never
/// below 0. It is NEVER <c>available</c>: that figure ignores API orders until POSitive recounts (spike
/// S0a), so an item without availableLive is refused rather than published overstated.
/// <c>pendingPlatformQuantity</c> = what the platform holds for orders that are not in POSitive
/// (ConnectorStockHold: on-hold orders not imported, orders held as exceptions); without it a push
/// would hand the platform's own hold back to the web and oversell.</item>
/// </list>
/// Every push (change feed, explicit items, reconcile, create) goes through here, so they all use
/// the same formula.
/// </summary>
public static class CatalogMapper
{
    public static CatalogMapResult Map(PosProduct product, ConnectorSettings settings, int decimals, bool includeText,
        decimal pendingPlatformQuantity = 0)
    {
        var level = product.Prices.FirstOrDefault(p => p.Level == settings.PriceLevel);
        if (level?.Price is not { } rawPrice || rawPrice < 0)
            return new CatalogMapResult(null,
                $"POSitive item {product.InvNo} has no price at level {settings.PriceLevel} (the connector's priceLevel).");
        if (product.AvailableLive is not { } live)
            return new CatalogMapResult(null,
                $"POSitive item {product.InvNo} came without availableLive; the server is older than the connector (API item A2). Stock is not pushed from 'available', which overstates it.");

        var regular = CatalogMoney.Round(rawPrice, decimals);
        decimal? sale = level.SalePrice is { } rawSale && CatalogMoney.Round(rawSale, decimals) is var s && s > 0 && s < regular
            ? s
            : null;

        var stock = Math.Floor(live - settings.StockBuffer - Math.Max(0, pendingPlatformQuantity));
        var quantity = stock <= 0 ? 0 : stock >= int.MaxValue ? int.MaxValue : (int)stock;

        return new CatalogMapResult(new CatalogPushValues(
            regular, sale, quantity,
            includeText ? NameOf(product) : null,
            includeText ? DescriptionOf(product) : null), null);
    }

    /// <summary>The platform description: the item's extended (web) description, else its long description.</summary>
    public static string? DescriptionOf(PosProduct product) =>
        Blank(product.ExtendedDescription) ?? Blank(product.LongDescription);

    /// <summary>A matrix parent or one of its cells: slice 1 links these to existing Woo variations by SKU but never creates them (design §9.5).</summary>
    public static bool IsMatrix(PosProduct product) => product.MatrixItemCount > 0 || product.ParentInvNo is > 0;

    /// <summary>The platform product name: the POSitive description, else the SKU, else "Item &lt;invNo&gt;".</summary>
    public static string NameOf(PosProduct product) =>
        Blank(product.Description) ?? Blank(product.Sku) ?? "Item " + product.InvNo.ToString(CultureInfo.InvariantCulture);

    /// <summary>Case-insensitive, trimmed SKU key (Woo's ?sku= ignores case, S0bc); null when blank.</summary>
    public static string? SkuKey(string? sku) => Blank(sku)?.ToUpperInvariant();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Reads a <see cref="CatalogPushPayload"/> as enqueued by order import (any property casing).</summary>
public static class CatalogPayloads
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The payload's distinct positive invNos; null when there is no payload (= push from the change feed).</summary>
    public static IReadOnlyList<int>? ReadInvNos(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        CatalogPushPayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<CatalogPushPayload>(payload, Options);
        }
        catch (JsonException ex)
        {
            throw new ConnectorJobException($"catalog.push payload is not a CatalogPushPayload: {ex.Message}", retryable: false, ex);
        }
        return (parsed?.InvNos ?? []).Where(i => i > 0).Distinct().ToList();
    }

    public static string Write(IEnumerable<int> invNos) =>
        JsonSerializer.Serialize(new CatalogPushPayload(invNos.Distinct().ToList()), Options);
}
