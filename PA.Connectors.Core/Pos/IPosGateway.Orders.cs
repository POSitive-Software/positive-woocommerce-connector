namespace PA.Connectors.Core.Pos;

/// <summary>Order-side gateway members added by S3 (order import): customers, transactions, payments.</summary>
public partial interface IPosGateway
{
    /// <summary>
    /// GET /api/v2/customers?externalId=: the customer linked to <paramref name="localCustomerId"/>
    /// in this tenant WITH its contact fields (so the import PATCHes only what changed).
    /// errorCode customer_not_found. The default (for gateways that cannot read the
    /// fields) returns the id with no fields, which makes the import patch every
    /// contact field it has.
    /// </summary>
    async Task<PosResult<PosCustomerDetails>> FindCustomerDetailsAsync(
        string localCustomerId, CancellationToken cancellationToken = default)
    {
        var found = await FindCustomerAsync(localCustomerId, cancellationToken);
        return found.Success
            ? PosResult<PosCustomerDetails>.Ok(new PosCustomerDetails(found.Value!.CustomerId, found.Value.LocalCustomerId, new PosCustomer()))
            : PosResult<PosCustomerDetails>.Fail(found.ErrorCode ?? PosErrorCodes.CustomerNotFound, found.Detail, found.ValidationErrors);
    }

    /// <summary>
    /// The transaction an earlier create wrote for <see cref="PosTransactionLookup.LocalTransactionId"/>
    /// (a crash between the create and recording it, or a 409 transaction_exists), with its invoice
    /// number and, when the API shows them, its product lines. errorCode transaction_not_found when
    /// there is none (or it cannot be told apart from another one).
    /// <para>
    /// <see cref="PosApiClient"/> looks it up exactly by the localTransactionId (G13, closed by ticket
    /// 08b29615 A6): GET /transactions/pending?externalId= (then GET /transactions/pending/{number} for the
    /// lines); a 404 transaction_invoiced names the invoice, read with GET /invoices/{id}, whose
    /// <c>originalPendingLocalTransactionId</c> is the same id. 404 transaction_not_found = not found.
    /// The default (gateways that cannot look) finds nothing, which leaves the create's own 409 guard.
    /// </para>
    /// </summary>
    Task<PosResult<PosTransactionFound>> FindTransactionAsync(
        PosTransactionLookup lookup, CancellationToken cancellationToken = default) =>
        Task.FromResult(PosResult<PosTransactionFound>.Fail(PosErrorCodes.TransactionNotFound, "This gateway cannot look transactions up."));
}

/// <summary>A customer as the list returns it: POSitive id plus the writable contact fields (phones as stored: area code separate).</summary>
public sealed record PosCustomerDetails(int CustomerId, string? LocalCustomerId, PosCustomer Fields);

/// <summary>
/// What identifies a transaction the connector created: its localTransactionId (the key the lookup uses), and the
/// customer, PO number and figures it was sent with (for gateways that cannot look up by id).
/// </summary>
public sealed record PosTransactionLookup(
    string LocalTransactionId,
    string LocalCustomerId,
    string? PurchaseOrderNumber,
    decimal Total,
    decimal TaxAmount);

/// <summary>
/// A transaction found by <see cref="IPosGateway.FindTransactionAsync"/>. <see cref="ProductIds"/> = the
/// productId of each line (null entries = non-inventory lines); null when the API did not show the lines.
/// <see cref="Total"/> excludes tax, as on the create.
/// </summary>
public sealed record PosTransactionFound(
    int TransactionId,
    decimal? Total,
    decimal? TaxAmount,
    IReadOnlyList<int?>? ProductIds,
    string MatchedBy);
