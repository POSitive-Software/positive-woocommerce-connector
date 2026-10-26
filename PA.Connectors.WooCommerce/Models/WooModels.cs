using System.Text.Json;
using System.Text.Json.Serialization;

namespace PA.Connectors.WooCommerce.Models;

// Read models for the wc/v3 resources the connector uses. Only the fields the
// connector maps are declared; everything else in a response is ignored.
// Shapes confirmed against the dev store (spike S0bc, PA.Connectors.Tests/Fixtures/WooCommerce).
// *_gmt dates come without an offset and ARE UTC; use the *Utc helpers.

/// <summary>Woo product types (<c>type</c>).</summary>
public static class WooProductTypes
{
    public const string Simple = "simple";
    public const string Variable = "variable";
    public const string Variation = "variation";
    public const string Grouped = "grouped";
    public const string External = "external";
}

/// <summary>Woo order statuses (<c>status</c>), without the wc- prefix.</summary>
public static class WooOrderStatuses
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string OnHold = "on-hold";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Refunded = "refunded";
    public const string Failed = "failed";
    public const string CheckoutDraft = "checkout-draft";

    /// <summary>Moved to the bin in wp-admin (the order is still readable by id). Treated like cancelled.</summary>
    public const string Trash = "trash";

    /// <summary>
    /// Statuses in which Woo has reduced (and keeps reduced) stock for the order: on-hold, processing,
    /// completed. Woo restores it on cancelled / refunded (restock) and never reduces it for pending,
    /// failed or checkout-draft.
    /// </summary>
    public static bool HoldsStock(string? status) => status is OnHold or Processing or Completed;

    /// <summary>The order is void on the platform: cancelled, refunded, or trashed.</summary>
    public static bool IsVoid(string? status) => status is Cancelled or Refunded or Trash;
}

internal static class WooDates
{
    public static DateTime? AsUtc(DateTime? gmt) =>
        gmt is null ? null : DateTime.SpecifyKind(gmt.Value, DateTimeKind.Utc);
}

public sealed class WooMeta
{
    public long Id { get; set; }
    public string? Key { get; set; }
    public JsonElement Value { get; set; }
    public string? DisplayKey { get; set; }
    public JsonElement DisplayValue { get; set; }
}

public sealed class WooCategoryRef
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Slug { get; set; }
}

/// <summary>A variation's chosen attribute value (<c>option</c>), or a product attribute (<c>options</c>).</summary>
public sealed class WooAttribute
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Slug { get; set; }
    public string? Option { get; set; }
    public List<string>? Options { get; set; }
    public bool? Variation { get; set; }
}

/// <summary>
/// A product — or a variation, since <c>GET /products?sku=</c> returns
/// variations too (type "variation", parent_id set).
/// </summary>
public sealed class WooProduct
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Slug { get; set; }
    public string? Permalink { get; set; }
    public string? Type { get; set; }
    public string? Status { get; set; }
    public string? Description { get; set; }
    public string? ShortDescription { get; set; }
    public string? Sku { get; set; }
    public decimal? Price { get; set; }
    public decimal? RegularPrice { get; set; }
    public decimal? SalePrice { get; set; }
    public bool OnSale { get; set; }

    /// <summary>A variation whose parent manages its stock reports <c>"parent"</c> (read as false: it does not manage its own).</summary>
    [JsonConverter(typeof(WooManageStockConverter))]
    public bool ManageStock { get; set; }
    public decimal? StockQuantity { get; set; }
    public string? StockStatus { get; set; }
    public long ParentId { get; set; }
    public List<long> Variations { get; set; } = [];
    public List<WooCategoryRef> Categories { get; set; } = [];
    public List<WooAttribute> Attributes { get; set; } = [];
    public List<WooMeta> MetaData { get; set; } = [];
    public DateTime? DateCreatedGmt { get; set; }
    public DateTime? DateModifiedGmt { get; set; }

    [JsonIgnore]
    public DateTime? DateModifiedUtc => WooDates.AsUtc(DateModifiedGmt);

    [JsonIgnore]
    public bool IsVariation => string.Equals(Type, WooProductTypes.Variation, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A variation from <c>GET /products/{id}/variations</c> (no <c>type</c> on older stores; always a variation).</summary>
public sealed class WooVariation
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Status { get; set; }
    public string? Description { get; set; }
    public string? Sku { get; set; }
    public decimal? Price { get; set; }
    public decimal? RegularPrice { get; set; }
    public decimal? SalePrice { get; set; }
    public bool OnSale { get; set; }

    /// <summary>A variation whose parent manages its stock reports <c>"parent"</c> (read as false: it does not manage its own).</summary>
    [JsonConverter(typeof(WooManageStockConverter))]
    public bool ManageStock { get; set; }
    public decimal? StockQuantity { get; set; }
    public string? StockStatus { get; set; }
    public long ParentId { get; set; }
    public List<WooAttribute> Attributes { get; set; } = [];
    public List<WooMeta> MetaData { get; set; } = [];
    public DateTime? DateModifiedGmt { get; set; }

    [JsonIgnore]
    public DateTime? DateModifiedUtc => WooDates.AsUtc(DateModifiedGmt);
}

public sealed class WooAddress
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Company { get; set; }

    [JsonPropertyName("address_1")]
    public string? Address1 { get; set; }

    [JsonPropertyName("address_2")]
    public string? Address2 { get; set; }

    public string? City { get; set; }
    public string? State { get; set; }
    public string? Postcode { get; set; }
    public string? Country { get; set; }

    /// <summary>Billing only (shipping has no email).</summary>
    public string? Email { get; set; }

    public string? Phone { get; set; }
}

/// <summary>One tax rate's share of a line (<c>taxes[]</c>). Subtotal is "" on shipping/fee lines.</summary>
public sealed class WooLineTax
{
    public long Id { get; set; }
    public decimal? Total { get; set; }
    public decimal? Subtotal { get; set; }
}

public sealed class WooLineItem
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public long ProductId { get; set; }
    public long VariationId { get; set; }
    public decimal Quantity { get; set; }
    public string? TaxClass { get; set; }

    /// <summary>Before coupons, ex tax.</summary>
    public decimal Subtotal { get; set; }

    public decimal SubtotalTax { get; set; }

    /// <summary>After coupons, ex tax.</summary>
    public decimal Total { get; set; }

    public decimal TotalTax { get; set; }
    public List<WooLineTax> Taxes { get; set; } = [];
    public List<WooMeta> MetaData { get; set; } = [];
    public string? Sku { get; set; }

    /// <summary>Unit price after discount — a JSON NUMBER, unlike every other amount.</summary>
    public decimal? Price { get; set; }

    /// <summary>The parent product's name for a variation line; null otherwise.</summary>
    public string? ParentName { get; set; }

    /// <summary>The id the item map is keyed by: the variation when there is one.</summary>
    [JsonIgnore]
    public long ItemId => VariationId != 0 ? VariationId : ProductId;
}

public sealed class WooTaxLine
{
    public long Id { get; set; }
    public string? RateCode { get; set; }
    public long RateId { get; set; }
    public string? Label { get; set; }
    public bool Compound { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal ShippingTaxTotal { get; set; }
    public decimal? RatePercent { get; set; }
}

public sealed class WooShippingLine
{
    public long Id { get; set; }
    public string? MethodTitle { get; set; }
    public string? MethodId { get; set; }
    public string? InstanceId { get; set; }
    public decimal Total { get; set; }
    public decimal TotalTax { get; set; }
    public List<WooLineTax> Taxes { get; set; } = [];
    public string? TaxStatus { get; set; }
}

public sealed class WooFeeLine
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? TaxClass { get; set; }
    public string? TaxStatus { get; set; }

    /// <summary>The fee as entered ("" when Woo computed it).</summary>
    public decimal? Amount { get; set; }

    public decimal Total { get; set; }
    public decimal TotalTax { get; set; }
    public List<WooLineTax> Taxes { get; set; } = [];
}

public sealed class WooCouponLine
{
    public long Id { get; set; }
    public string? Code { get; set; }

    /// <summary>Ex tax.</summary>
    public decimal Discount { get; set; }

    public decimal DiscountTax { get; set; }
    public string? DiscountType { get; set; }
    public decimal? NominalAmount { get; set; }
    public bool FreeShipping { get; set; }
}

/// <summary>An order's <c>refunds[]</c> summary entry (totals are NEGATIVE).</summary>
public sealed class WooOrderRefundRef
{
    public long Id { get; set; }
    public string? Reason { get; set; }
    public decimal Total { get; set; }
    public decimal? TotalTax { get; set; }
}

public sealed class WooOrder
{
    public long Id { get; set; }
    public long ParentId { get; set; }
    public string? Number { get; set; }
    public string? OrderKey { get; set; }
    public string? Status { get; set; }
    public string? Currency { get; set; }
    public string? Version { get; set; }
    public bool PricesIncludeTax { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal DiscountTax { get; set; }
    public decimal ShippingTotal { get; set; }
    public decimal ShippingTax { get; set; }
    public decimal CartTax { get; set; }

    /// <summary>Grand total INCLUDING tax.</summary>
    public decimal Total { get; set; }

    public decimal TotalTax { get; set; }

    /// <summary>0 = guest checkout.</summary>
    public long CustomerId { get; set; }

    public string? CustomerNote { get; set; }
    public WooAddress Billing { get; set; } = new();
    public WooAddress Shipping { get; set; } = new();
    public string? PaymentMethod { get; set; }
    public string? PaymentMethodTitle { get; set; }
    public string? TransactionId { get; set; }
    public string? CreatedVia { get; set; }
    public bool NeedsPayment { get; set; }
    public DateTime? DateCreatedGmt { get; set; }
    public DateTime? DateModifiedGmt { get; set; }
    public DateTime? DatePaidGmt { get; set; }
    public DateTime? DateCompletedGmt { get; set; }
    public List<WooLineItem> LineItems { get; set; } = [];
    public List<WooTaxLine> TaxLines { get; set; } = [];
    public List<WooShippingLine> ShippingLines { get; set; } = [];
    public List<WooFeeLine> FeeLines { get; set; } = [];
    public List<WooCouponLine> CouponLines { get; set; } = [];
    public List<WooOrderRefundRef> Refunds { get; set; } = [];
    public List<WooMeta> MetaData { get; set; } = [];

    [JsonIgnore]
    public DateTime? DateCreatedUtc => WooDates.AsUtc(DateCreatedGmt);

    [JsonIgnore]
    public DateTime? DateModifiedUtc => WooDates.AsUtc(DateModifiedGmt);

    [JsonIgnore]
    public DateTime? DatePaidUtc => WooDates.AsUtc(DatePaidGmt);

    [JsonIgnore]
    public bool IsGuest => CustomerId == 0;
}

/// <summary>A refund from <c>GET /orders/{id}/refunds</c>. Amount is positive; line totals negative.</summary>
public sealed class WooRefund
{
    public long Id { get; set; }
    public DateTime? DateCreatedGmt { get; set; }
    public decimal Amount { get; set; }
    public string? Reason { get; set; }
    public long RefundedBy { get; set; }
    public bool RefundedPayment { get; set; }
    public List<WooLineItem> LineItems { get; set; } = [];
    public List<WooShippingLine> ShippingLines { get; set; } = [];
    public List<WooTaxLine> TaxLines { get; set; } = [];
    public List<WooFeeLine> FeeLines { get; set; } = [];
    public List<WooMeta> MetaData { get; set; } = [];

    [JsonIgnore]
    public DateTime? DateCreatedUtc => WooDates.AsUtc(DateCreatedGmt);
}

/// <summary>Webhook statuses: Woo sets <c>disabled</c> itself after 5 consecutive delivery failures.</summary>
public static class WooWebhookStatuses
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Disabled = "disabled";
}

/// <summary>One entry of GET payment_gateways (<c>id</c> is what an order's <c>payment_method</c> holds).</summary>
public sealed class WooPaymentGateway
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? MethodTitle { get; set; }
    public bool Enabled { get; set; }
}

public sealed class WooWebhook
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Status { get; set; }
    public string? Topic { get; set; }
    public string? Resource { get; set; }
    public string? Event { get; set; }
    public List<string> Hooks { get; set; } = [];
    public string? DeliveryUrl { get; set; }
    public string? Secret { get; set; }
    public DateTime? DateCreatedGmt { get; set; }
    public DateTime? DateModifiedGmt { get; set; }
}

/// <summary>Body for POST/PUT /webhooks. Null fields are not sent.</summary>
public sealed class WooWebhookWrite
{
    public string? Name { get; set; }
    public string? Status { get; set; }
    public string? Topic { get; set; }
    public string? DeliveryUrl { get; set; }
    public string? Secret { get; set; }
}

/// <summary>
/// A product/variation create or update. Null fields are not sent. Money goes
/// as Woo strings (<see cref="WooJson.FormatMoney"/>); SalePrice "" REMOVES the sale.
/// </summary>
public sealed class WooProductWrite
{
    /// <summary>Required for update; absent for create.</summary>
    public long? Id { get; set; }

    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Status { get; set; }
    public string? Sku { get; set; }
    public string? RegularPrice { get; set; }
    public string? SalePrice { get; set; }
    public bool? ManageStock { get; set; }
    public int? StockQuantity { get; set; }
    public string? Description { get; set; }
    public string? ShortDescription { get; set; }
}

/// <summary>Body for POST /products/batch (Woo caps create+update+delete at 100 per request).</summary>
public sealed class WooBatchRequest
{
    public List<WooProductWrite>? Create { get; set; }
    public List<WooProductWrite>? Update { get; set; }
    public List<long>? Delete { get; set; }
}

public sealed class WooError
{
    public string? Code { get; set; }
    public string? Message { get; set; }
}

/// <summary>One entry of a batch response: the resource on success, or <see cref="Error"/>.</summary>
public sealed class WooBatchItem
{
    public long Id { get; set; }
    public string? Sku { get; set; }
    public WooError? Error { get; set; }
}

public sealed class WooBatchResponse
{
    public List<WooBatchItem> Create { get; set; } = [];
    public List<WooBatchItem> Update { get; set; } = [];
    public List<WooBatchItem> Delete { get; set; } = [];
}
