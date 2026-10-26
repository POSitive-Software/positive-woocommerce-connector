namespace PA.Connectors.Core.Pos;

/// <summary>
/// What the connector engine needs from POSitive — and it gets it ONLY through
/// the public V2 API (design §0, revision 2), exactly as an outside developer's
/// connector would. <see cref="PosApiClient"/> implements it over HTTP with the
/// connector's own API key, so every call runs in the connector's own tenant
/// (its DeveloperID): its APITableLink rows never collide with another
/// integrator's, a second Woo store, or a later Shopify connector.
/// <para>
/// Failures the API answers with an <c>errorCode</c> (validation_failed,
/// customer_exists, transaction_exists, no_writable_lines, ...) come back as
/// <see cref="PosResult{T}"/>, never as exceptions. Transient faults (server
/// down, 5xx, 429, timeouts) throw, and the job retries with backoff; a rejected
/// or missing API key throws a non-retryable ConnectorJobException.
/// </para>
/// </summary>
public partial interface IPosGateway
{
    // --- customers -------------------------------------------------------

    /// <summary>GET /api/v2/customers?externalId=: the customer linked to <paramref name="localCustomerId"/> in this tenant. errorCode customer_not_found (the list hides a record an operator is still adding, so that reads as not found too).</summary>
    Task<PosResult<PosCustomerRef>> FindCustomerAsync(
        string localCustomerId, CancellationToken cancellationToken = default);

    /// <summary>POST /api/v2/customers. errorCode validation_failed / customer_exists / lookupcode_in_use.</summary>
    Task<PosResult<PosCustomerRef>> CreateCustomerAsync(
        PosCustomer customer, CancellationToken cancellationToken = default);

    /// <summary>
    /// PATCH /api/v2/customers/{id}: only the NON-NULL fields of <paramref name="changes"/>
    /// are sent (null = keep; the connector never clears a field). errorCode
    /// validation_failed / customer_not_found / concurrent_update / lookupcode_in_use.
    /// </summary>
    Task<PosResult<PosCustomerRef>> PatchCustomerAsync(
        int customerId, PosCustomer changes, CancellationToken cancellationToken = default);

    /// <summary>POST /api/v2/customers/addresses?externalId=: add an address to the customer linked to <see cref="PosAddress.LocalCustomerId"/>. errorCode validation_failed / customer_not_found / address_exists / invalid_address_type.</summary>
    Task<PosResult<PosAddressRef>> AddAddressAsync(
        PosAddress address, CancellationToken cancellationToken = default);

    // --- transactions ----------------------------------------------------

    /// <summary>POST /api/v2/transactions. errorCode validation_failed / transaction_exists / no_writable_lines / customer_pending / reference_not_found / address_exists (the inline ShippingAddress's LocalAddressId is another customer's ship-to).</summary>
    Task<PosResult<PosTransactionCreated>> CreateTransactionAsync(
        PosTransaction transaction, CancellationToken cancellationToken = default);

    /// <summary>
    /// POST /api/v2/transactions/payments?externalId=. errorCode validation_failed / payment_invalid /
    /// transaction_not_found / payment_exists (the payment's LocalPaymentId was already recorded for this
    /// tenant: nothing was written, so the order is already paid; API item A1).
    /// </summary>
    Task<PosResult<PosPaymentPosted>> PostPaymentAsync(
        PosPayment payment, CancellationToken cancellationToken = default);

    // --- products --------------------------------------------------------

    /// <summary>GET /api/v2/products (paged). errorCode modified_since_unsupported when the store's procs predate the filter.</summary>
    Task<PosResult<PosProductPage>> ListProductsAsync(
        PosProductQuery query, CancellationToken cancellationToken = default);

    /// <summary>GET /api/v2/products?sku=: distinct invNos a sku/barcode matches (BARCODES cross-ref + the item's own barcode — the transaction line rule). Empty = none.</summary>
    Task<IReadOnlyList<int>> ResolveSkuAsync(string sku, CancellationToken cancellationToken = default);

    // --- APITableLink (this tenant only) ---------------------------------

    /// <summary>GET /api/v2/products?externalId=: the invNo linked to <paramref name="externalId"/> in the tenant's 'items' links, or null.</summary>
    Task<int?> FindLinkedProductAsync(
        string externalId, CancellationToken cancellationToken = default);

    /// <summary>GET /api/v2/products?invNo=: the tenant's external id for <paramref name="invNo"/>, or null when unlinked (or no such item).</summary>
    Task<string?> GetProductExternalIdAsync(
        int invNo, CancellationToken cancellationToken = default);

    /// <summary>
    /// PUT /api/v2/products/{invNo}/link with <c>{ localProductId }</c> (API item A3). Writes the
    /// tenant's 'items' link invNo → <paramref name="externalId"/>, so /api/v2 reads show it as
    /// externalId and transaction lines resolve it as localProductId. Ok(true) = linked now (201);
    /// Ok(false) = it already had exactly this id (200, nothing written). errorCode
    /// validation_failed / product_not_found (no such invNo) / link_exists (the id is on a
    /// different item, or the item has another id; the API checks and writes atomically).
    /// </summary>
    Task<PosResult<bool>> LinkProductAsync(
        int invNo, string externalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// DELETE /api/v2/products/{invNo}/link: removes the tenant's link from the item. Ok(true) =
    /// removed (204); Ok(false) = there was none (404 link_not_found). errorCode product_not_found.
    /// </summary>
    Task<PosResult<bool>> UnlinkProductAsync(int invNo, CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of a gateway call. <see cref="ErrorCode"/> is the HTTP API's
/// errorCode for the same failure; <see cref="Detail"/> its detail text;
/// <see cref="ValidationErrors"/> the per-field messages of a validation_failed.
/// </summary>
public sealed record PosResult<T>(
    bool Success,
    T? Value,
    string? ErrorCode = null,
    string? Detail = null,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null)
{
    public static PosResult<T> Ok(T value) => new(true, value);

    public static PosResult<T> Fail(string errorCode, string? detail = null,
        IReadOnlyDictionary<string, string[]>? validationErrors = null) =>
        new(false, default, errorCode, detail, validationErrors);

    /// <summary>"errorCode: detail" plus the field errors — for exception reasons and logs.</summary>
    public string Describe()
    {
        if (Success)
            return "ok";
        var text = Detail is null ? ErrorCode ?? "error" : $"{ErrorCode}: {Detail}";
        if (ValidationErrors is { Count: > 0 })
            text += " (" + string.Join("; ", ValidationErrors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}")) + ")";
        return text;
    }
}

/// <summary>errorCodes the gateway checks for or raises itself.</summary>
public static class PosErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string CustomerNotFound = "customer_not_found";
    public const string InvalidCustomerId = "invalid_customer_id";
    public const string NoWritableLines = "no_writable_lines";
    /// <summary>409 from PUT /products/{invNo}/link (A3): the id is on another item, or the item has another id.</summary>
    public const string LinkExists = "link_exists";
    /// <summary>404 from DELETE /products/{invNo}/link (A3): the item has no link for this tenant.</summary>
    public const string LinkNotFound = "link_not_found";
    public const string ProductNotFound = "product_not_found";
    /// <summary>409 from the payment routes: this LocalPaymentId was already recorded (A1). Already paid.</summary>
    public const string PaymentExists = "payment_exists";
    /// <summary>409 from POST /transactions: this localTransactionId was already written for the tenant.</summary>
    public const string TransactionExists = "transaction_exists";
    public const string TransactionNotFound = "transaction_not_found";
    /// <summary>
    /// 503 from POST /transactions (and the line appends): the store skipped Prepare database, so
    /// POSitive refuses orders (it cannot hold their stock). Nothing was written. Transient — it
    /// arrives as a <see cref="PosApiException"/> and the job retries on its long schedule.
    /// </summary>
    public const string DatabaseUpgradeRequired = "database_upgrade_required";
}

/// <summary>Customer fields (the CustomerWriteDto writable set). For create, LocalCustomerId is required.</summary>
public sealed record PosCustomer
{
    public string? LocalCustomerId { get; init; }
    public string? CustomerType { get; init; }
    public string? LookupCode { get; init; }
    public string? Email { get; init; }
    public string? Company { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? HomePhone { get; init; }
    public string? HomePhoneAreaCode { get; init; }
    public string? WorkPhone { get; init; }
    public string? WorkPhoneAreaCode { get; init; }
    public string? CellPhone { get; init; }
    public string? FaxPhone { get; init; }
    public string? FaxPhoneAreaCode { get; init; }
    public string? TaxVat { get; init; }
    public int? TaxGroupId { get; init; }
}

public sealed record PosCustomerRef(int CustomerId, string? LocalCustomerId);

/// <summary>An address for the customer linked to <see cref="LocalCustomerId"/> (AddressWriteDto fields).</summary>
public sealed record PosAddress
{
    public string? LocalCustomerId { get; init; }
    public string? LocalAddressId { get; init; }
    public string? Company { get; init; }
    public string? Contact { get; init; }
    /// <summary>Address type as the API takes it (e.g. SHIPPING / BILLING).</summary>
    public string? Type { get; init; }
    public string? DefaultAddress { get; init; }
    public string? Address1 { get; init; }
    public string? Address2 { get; init; }
    public string? Address3 { get; init; }
    public string? City { get; init; }
    public string? Region { get; init; }
    public string? Postcode { get; init; }
    public string? CountryCode { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public int? TaxGroupId { get; init; }
}

public sealed record PosAddressRef(int CustomerId, int? AddressId, string? LocalAddressId);

/// <summary>A new transaction (TransactionCreateDto fields). Total EXCLUDES tax.</summary>
public sealed record PosTransaction
{
    public required string LocalTransactionId { get; init; }
    public string? LocalCustomerId { get; init; }
    public int? CustomerId { get; init; }
    public string? LocalShippingAddressId { get; init; }
    /// <summary>The ship-to inline (API item A4): created or reused by the API in the same write. Not with LocalShippingAddressId.</summary>
    public PosShipTo? ShippingAddress { get; init; }
    public string? TransactionType { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public string? DeliveryMethod { get; init; }
    public string? PurchaseOrderNumber { get; init; }
    public decimal TaxAmount { get; init; }
    public decimal Total { get; init; }
    public string? Note { get; init; }
    public string? SalespersonId { get; init; }
    public IReadOnlyList<PosTransactionLine> Lines { get; init; } = [];
}

/// <summary>An inline ship-to (the API's InlineShippingAddressDto fields).</summary>
public sealed record PosShipTo
{
    public string? LocalAddressId { get; init; }
    public string? Company { get; init; }
    public string? Contact { get; init; }
    public string? Address1 { get; init; }
    public string? Address2 { get; init; }
    public string? City { get; init; }
    public string? Region { get; init; }
    public string? Postcode { get; init; }
    public string? CountryCode { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
}

/// <summary>One line. Product by ProductId &gt; LocalProductId &gt; Sku (first present wins); none = non-inventory line.</summary>
public sealed record PosTransactionLine
{
    public string? LocalLineId { get; init; }
    public string? LocalProductId { get; init; }
    public int? ProductId { get; init; }
    public string? Sku { get; init; }
    public string? Description { get; init; }
    public decimal Quantity { get; init; }
    public decimal Price { get; init; }
    public decimal TaxAmount { get; init; }
    public decimal DiscountPercent { get; init; }
    public decimal LineTotal { get; init; }
    public string? Note { get; init; }
}

public sealed record PosLineResult(string? LocalLineId, string Status);

public sealed record PosTransactionCreated(int TransactionId, string? LocalTransactionId, IReadOnlyList<PosLineResult> Lines);

/// <summary>A payment against the transaction linked to <see cref="LocalTransactionId"/>. PaymentType C / R / A.</summary>
public sealed record PosPayment
{
    public required string LocalTransactionId { get; init; }
    public required string PaymentType { get; init; }
    public decimal Amount { get; init; }
    public decimal VatAmount { get; init; }
    public DateTime? CreatedAt { get; init; }
    /// <summary>The replay guard (A1): the same id twice for this tenant answers payment_exists and writes nothing. Max 100.</summary>
    public string? LocalPaymentId { get; init; }
}

public sealed record PosPaymentPosted(int TransactionId, int? PaymentId);

/// <summary>Product list query (GET /api/v2/products parameters the connector uses).</summary>
public sealed record PosProductQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 100;
    public int? InvNo { get; init; }
    public string? Sku { get; init; }
    public bool WebstoreOnly { get; init; }
    public int DivisionId { get; init; }
    public DateTime? ModifiedSince { get; init; }
}

public sealed record PosPrice(int Level, decimal? Price, decimal? SalePrice);

/// <summary>
/// The product fields the connector maps (a subset of the API's ProductDto).
/// <see cref="AvailableLive"/> (API item A2) is the stock figure to push to the store:
/// it counts open orders, including the ones the connector itself imported, at once;
/// <see cref="Available"/> only does after POSitive recounts the item.
/// </summary>
public sealed record PosProduct(
    int InvNo,
    string? ExternalId,
    string? Sku,
    string? Description,
    string? LongDescription,
    int? ParentInvNo,
    bool? WebProduct,
    decimal? Available,
    decimal? InStock,
    decimal? HeldInStock,
    IReadOnlyList<PosPrice> Prices,
    int? DivisionId,
    decimal? AvailableLive = null,
    string? ExtendedDescription = null,
    int MatrixItemCount = 0);

public sealed record PosProductPage(IReadOnlyList<PosProduct> Items, int Page, int PageSize, int TotalItems);
