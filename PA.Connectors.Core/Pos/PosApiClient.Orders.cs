using System.Globalization;

namespace PA.Connectors.Core.Pos;

/// <summary>S3 (order import) implementations of <see cref="IPosGateway"/>.Orders members.</summary>
public sealed partial class PosApiClient
{
    public async Task<PosResult<PosCustomerDetails>> FindCustomerDetailsAsync(
        string localCustomerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localCustomerId))
            return PosResult<PosCustomerDetails>.Fail(PosErrorCodes.CustomerNotFound, "No localCustomerId given.");

        var page = await GetAsync<PagedDto<CustomerContactDto>>(
            $"/api/v2/customers?externalId={Q(localCustomerId)}&pageSize=1", cancellationToken);
        if (!page.Success)
            return Rewrap<PagedDto<CustomerContactDto>, PosCustomerDetails>(page);
        var c = page.Value!.Items?.FirstOrDefault();
        if (c is null)
            return PosResult<PosCustomerDetails>.Fail(PosErrorCodes.CustomerNotFound,
                $"No customer is linked to localCustomerId '{localCustomerId}' for this key.");

        return PosResult<PosCustomerDetails>.Ok(new PosCustomerDetails(c.CustomerId, c.ExternalId ?? localCustomerId, new PosCustomer
        {
            LocalCustomerId = c.ExternalId ?? localCustomerId,
            CustomerType = c.CustomerType,
            LookupCode = c.LookupCode,
            Email = c.Email,
            Company = c.Company,
            FirstName = c.FirstName,
            LastName = c.LastName,
            HomePhone = c.HomePhone,
            HomePhoneAreaCode = c.HomePhoneAreaCode,
            WorkPhone = c.WorkPhone,
            WorkPhoneAreaCode = c.WorkPhoneAreaCode,
            CellPhone = c.CellPhone,
            FaxPhone = c.FaxPhone,
            FaxPhoneAreaCode = c.FaxPhoneAreaCode,
        }));
    }

    /// <summary>404 errorCode of GET /transactions/pending?externalId= when the store already invoiced the transaction.</summary>
    internal const string TransactionInvoiced = "transaction_invoiced";

    public async Task<PosResult<PosTransactionFound>> FindTransactionAsync(
        PosTransactionLookup lookup, CancellationToken cancellationToken = default)
    {
        var localTransactionId = lookup.LocalTransactionId.Trim();
        if (localTransactionId.Length == 0)
            return NotFound("no localTransactionId given");

        // 1. Still pending (the usual case: it was created moments ago) — the exact lookup by OUR id (G13).
        var pending = await GetAsync<PagedDto<PendingSummaryDto>>(
            $"/api/v2/transactions/pending?externalId={Q(localTransactionId)}&pageSize=1", cancellationToken);
        if (pending.Success)
        {
            // Only a row that carries our id: a server without the externalId filter would answer the whole list.
            var hit = pending.Value?.Items?.FirstOrDefault(t =>
                string.Equals(t.ExternalId?.Trim(), localTransactionId, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
                return NotFound("the pending list did not answer with this localTransactionId (a POSitive server without the pending list's externalId filter?)");
            var detail = await GetAsync<PendingDetailDto>(
                $"/api/v2/transactions/pending/{hit.TransactionNumber.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
            var lines = detail.Success ? detail.Value?.Lines?.Select(l => l.ProductId).ToList() : null;
            return PosResult<PosTransactionFound>.Ok(new PosTransactionFound(
                hit.TransactionNumber, hit.Total, hit.TaxAmount, lines, "pending: externalId"));
        }

        // 2. Already invoiced: the 404 names the pending number and the invoice(s); the invoice carries
        //    our id as originalPendingLocalTransactionId and the figures as posted.
        if (pending.ErrorCode == TransactionInvoiced && pending.Problem?.TransactionId is { } number)
        {
            var newest = pending.Problem.InvoiceTransactionNumbers?.FirstOrDefault();
            if (newest is > 0)
            {
                var invoice = OrThrow(await GetAsync<InvoiceDetailDto>(
                    $"/api/v2/invoices/{newest.Value.ToString(CultureInfo.InvariantCulture)}", cancellationToken), "invoice lookup").Invoice;
                if (invoice is not null && string.Equals(invoice.OriginalPendingLocalTransactionId?.Trim(), localTransactionId,
                        StringComparison.OrdinalIgnoreCase))
                    return PosResult<PosTransactionFound>.Ok(new PosTransactionFound(
                        invoice.OriginalPendingTransactionNumber ?? number, invoice.Total, invoice.TaxAmount, null,
                        "invoice: originalPendingLocalTransactionId"));
            }
            return PosResult<PosTransactionFound>.Ok(new PosTransactionFound(number, null, null, null, "invoiced: transactionId"));
        }

        if (pending.ErrorCode == PosErrorCodes.TransactionNotFound)
            return NotFound(pending.Detail ?? "no pending transaction or invoice carries this localTransactionId");

        // Anything else (400, an unexpected 404) is a broken server or a bug, not data.
        throw new PosApiException($"POSitive API pending transaction lookup failed: {pending.ErrorCode}: {pending.Detail}", null, pending.ErrorCode);

        static PosResult<PosTransactionFound> NotFound(string why) =>
            PosResult<PosTransactionFound>.Fail(PosErrorCodes.TransactionNotFound, $"No transaction found: {why}.");
    }

    /// <summary>PendingSummaryDto fields the lookup reads.</summary>
    internal sealed record PendingSummaryDto(int TransactionNumber, string? ExternalId, decimal? Total, decimal? TaxAmount);

    internal sealed record PendingLineDto(int? ProductId);

    internal sealed record PendingDetailDto(PendingSummaryDto? Transaction, IReadOnlyList<PendingLineDto>? Lines);

    /// <summary>InvoiceSummaryDto fields the lookup reads.</summary>
    internal sealed record InvoiceSummaryDto(
        int TransactionNumber, string? OriginalPendingLocalTransactionId, int? OriginalPendingTransactionNumber,
        decimal? Total, decimal? TaxAmount);

    internal sealed record InvoiceDetailDto(InvoiceSummaryDto? Invoice);

    /// <summary>The CustomerListDto fields the import compares (docs/openapi/v2.json).</summary>
    internal sealed record CustomerContactDto(
        int CustomerId, string? ExternalId, string? CustomerType, string? LookupCode, string? Email, string? Company,
        string? FirstName, string? LastName, string? HomePhone, string? HomePhoneAreaCode, string? WorkPhone,
        string? WorkPhoneAreaCode, string? CellPhone, string? FaxPhone, string? FaxPhoneAreaCode);
}
