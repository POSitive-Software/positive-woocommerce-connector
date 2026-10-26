namespace PA.Connectors.WooCommerce.Catalog;

/// <summary>What one catalog run did — returned to the S5 match review and logged after every job.</summary>
public sealed class CatalogRunSummary
{
    /// <summary>match, reconcile, push (change feed), push-items (explicit invNos).</summary>
    public required string Mode { get; init; }

    /// <summary>Woo simple products + variations seen (full runs only).</summary>
    public int WooItems { get; internal set; }

    /// <summary>POSitive products read (web products for match/reconcile/push; the requested ones for push-items).</summary>
    public int PosItems { get; internal set; }

    /// <summary>New links made (SKU/barcode matches, plus existing API links taken over).</summary>
    public int Matched { get; internal set; }

    public int Ambiguous { get; internal set; }

    /// <summary>Woo products no POSitive web item matches; they stay unmanaged.</summary>
    public int UnmatchedWoo { get; internal set; }

    /// <summary>POSitive web items the connector will not create (matrix items, SKU held by another Woo product type).</summary>
    public int UnmatchedPos { get; internal set; }

    public int LinkFailed { get; internal set; }

    public int Created { get; internal set; }

    public int CreateFailed { get; internal set; }

    public int Pushed { get; internal set; }

    /// <summary>Linked items skipped because Woo already has exactly these values (hash or live compare).</summary>
    public int Unchanged { get; internal set; }

    public int PushFailed { get; internal set; }

    /// <summary>Linked Woo products that no longer exist (exception catalog_woo_missing).</summary>
    public int Missing { get; internal set; }

    /// <summary>Linked items moved to a different Woo product with the same SKU (it was deleted and re-made).</summary>
    public int Relinked { get; internal set; }

    public override string ToString() =>
        $"{Mode}: woo {WooItems}, pos {PosItems}, matched {Matched}, ambiguous {Ambiguous}, unmatched woo {UnmatchedWoo}/pos {UnmatchedPos}, " +
        $"link failed {LinkFailed}, created {Created} (failed {CreateFailed}), pushed {Pushed}, unchanged {Unchanged}, " +
        $"push failed {PushFailed}, missing {Missing}, relinked {Relinked}";
}
