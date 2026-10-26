using System.Globalization;

namespace PA.Connectors.WooCommerce;

/// <summary>
/// What the connect wizard shows after "Test connection" (design §5 step 2). Read with light
/// calls only: <c>system_status</c> hung the dev store in S0, so the WordPress/WooCommerce
/// versions (which only it reports) are not shown. A setting the store does not expose is null.
/// </summary>
public sealed record WooStoreInfo(
    int? ProductCount,
    string? Currency,
    int? PriceDecimals,
    bool? TaxesEnabled,
    bool? PricesIncludeTax,
    bool? HighPerformanceOrderStorage)
{
    /// <summary>
    /// <see cref="WooClient.TestConnectionAsync"/> first — it throws on a wrong URL or keys, which is
    /// the test — then each setting on its own, so one the store hides does not fail the test.
    /// </summary>
    public static async Task<WooStoreInfo> ReadAsync(WooClient client, CancellationToken cancellationToken = default)
    {
        var connection = await client.TestConnectionAsync(cancellationToken);
        var currency = await SettingAsync(client, "general", "woocommerce_currency", cancellationToken);
        var decimals = await SettingAsync(client, "general", "woocommerce_price_num_decimals", cancellationToken);
        var calcTaxes = await SettingAsync(client, "general", "woocommerce_calc_taxes", cancellationToken);
        var inclusive = await SettingAsync(client, "tax", "woocommerce_prices_include_tax", cancellationToken);
        var hpos = await SettingAsync(client, "advanced", "woocommerce_custom_orders_table_enabled", cancellationToken);
        return new WooStoreInfo(
            connection.ProductCount,
            string.IsNullOrWhiteSpace(currency) ? null : currency.Trim(),
            int.TryParse(decimals, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d is >= 0 and <= 8 ? d : null,
            YesNo(calcTaxes),
            YesNo(inclusive),
            YesNo(hpos));
    }

    private static async Task<string?> SettingAsync(WooClient client, string group, string id, CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetSettingAsync(group, id, cancellationToken);
        }
        catch (WooApiException ex) when (!ex.Transient)
        {
            return null; // not exposed by this store (or not readable with these keys): shown as unknown
        }
    }

    private static bool? YesNo(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "yes" or "true" or "1" => true,
        "no" or "false" or "0" => false,
        _ => null,
    };
}
