namespace PA.Connectors.Core.Orders;

/// <summary>
/// Exception kinds the order import raises (ConnectorException.Kind; ExternalRef =
/// the platform order id). Prefixed <c>order_</c> so they never share an open row
/// with a catalog exception about a product id.
/// </summary>
public static class OrderExceptionKinds
{
    /// <summary>At least one order line has no linked or sku-matched POSitive item: nothing was written.</summary>
    public const string UnmatchedProduct = "order_unmatched_product";

    /// <summary>The mapped lines do not add up to the platform's totals: nothing was written.</summary>
    public const string TotalsMismatch = "order_totals_mismatch";

    /// <summary>Cancelled, refunded or partly refunded on the platform after it was imported (slice 1 has no reversal).</summary>
    public const string ChangedAfterImport = "order_changed_after_import";

    /// <summary>The customer cannot be keyed (guest without an email) or the API refused the customer/order data.</summary>
    public const string Rejected = "order_rejected";

    /// <summary>POSitive skipped some lines of a transaction it did create (e.g. an item deleted since it was linked).</summary>
    public const string LinesSkipped = "order_lines_skipped";

    /// <summary>
    /// POSitive refused the order with 503 database_upgrade_required (the store skipped Prepare
    /// database): nothing was written and the import keeps retrying on its long schedule. The owner
    /// runs Prepare database; the next retry imports the order and resolves this.
    /// </summary>
    public const string PosUpgradeRequired = "pos_upgrade_required";

    /// <summary>Kinds that block an import; a later successful import of the same order resolves them.</summary>
    public static readonly IReadOnlySet<string> Blocking = new HashSet<string>(StringComparer.Ordinal)
    {
        UnmatchedProduct, TotalsMismatch, Rejected, PosUpgradeRequired,
    };
}
