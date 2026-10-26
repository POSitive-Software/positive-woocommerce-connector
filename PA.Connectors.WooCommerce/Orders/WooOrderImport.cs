using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PA.Connectors.Core;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Orders;
using PA.Connectors.Core.Pos;
using PA.Connectors.Core.Secrets;
using PA.Connectors.Core.Stores;
using PA.Connectors.WooCommerce.Models;

namespace PA.Connectors.WooCommerce.Orders;

/// <summary>
/// S3 (order import): order.import, order.poll backstop, webhook.health.
/// After a successful import it enqueues <see cref="ConnectorJobKinds.CatalogPush"/>
/// with a <see cref="CatalogPushPayload"/> of the ordered invNos so S2 re-pushes
/// their stock straight away.
/// <para>
/// order.import (design §4): re-fetch the order (the webhook payload is ignored),
/// gate on status, key the customer, resolve EVERY line, reconcile the totals,
/// then write in resumable steps — customer, transaction (409
/// transaction_exists = already imported), stock re-push, payment (409
/// payment_exists = already paid) — recording each step in the order store.
/// Problems the owner must fix (unmatched product, totals mismatch, a guest with
/// no email, data the API refuses) become exceptions and the job completes;
/// transient faults throw and the engine retries.
/// </para>
/// </summary>
public sealed class WooOrderImport
{
    /// <summary>Cursor holding the last post-import change already reported for an order (suffix = order id).</summary>
    public static string ChangeReportedCursor(long orderId) =>
        "orders.changeReported." + orderId.ToString(CultureInfo.InvariantCulture);

    /// <summary>How often webhook.health runs (it also runs once after each start, from the first poll).</summary>
    public static readonly TimeSpan WebhookHealthInterval = TimeSpan.FromHours(1);

    private readonly ConnectorServices _services;
    private readonly Func<WooClient> _client;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly WooOrderPoll _poll;
    private readonly WooWebhookHealth _webhooks;
    private readonly WooStockHolds _holds;

    public WooOrderImport(ConnectorServices services, Func<WooClient> client, IConnectorSecretStore? secrets = null,
        TimeProvider? time = null)
    {
        _services = services;
        _client = client;
        _time = time ?? TimeProvider.System;
        _logger = services.LoggerFactory.CreateLogger<WooOrderImport>();
        _holds = new WooStockHolds(services, _logger);
        _poll = new WooOrderPoll(services, client, _time, _holds);
        _webhooks = new WooWebhookHealth(services, client, secrets);
        RecurringJobs =
        [
            new RecurringJob(ConnectorJobKinds.OrderPoll, TimeSpan.FromMinutes(Math.Max(1, services.Settings.OrderPollMinutes))),
            new RecurringJob(ConnectorJobKinds.WebhookHealth, WebhookHealthInterval),
        ];
    }

    private string ConnectorId => _services.Settings.Id;

    /// <summary>Recurring order jobs (order.poll every orderPollMinutes, webhook.health hourly).</summary>
    public IReadOnlyList<RecurringJob> RecurringJobs { get; }

    public static bool Handles(string kind) =>
        kind is ConnectorJobKinds.OrderImport or ConnectorJobKinds.OrderPoll or ConnectorJobKinds.WebhookHealth;

    public Task HandleAsync(ConnectorJob job, CancellationToken cancellationToken) => job.Kind switch
    {
        ConnectorJobKinds.OrderImport => ImportAsync(job, cancellationToken),
        ConnectorJobKinds.OrderPoll => _poll.RunAsync(cancellationToken),
        ConnectorJobKinds.WebhookHealth => _webhooks.RunAsync(cancellationToken),
        _ => throw new ConnectorJobException($"Order jobs do not include '{job.Kind}'.", retryable: false),
    };

    /// <summary>Statuses imported under the settings (processing / completed, plus on-hold when importOnHold).</summary>
    public static bool IsImportable(string? status, ConnectorSettings settings) =>
        status is WooOrderStatuses.Processing or WooOrderStatuses.Completed
        || (settings.ImportOnHold && status == WooOrderStatuses.OnHold);

    /// <summary>
    /// Paid = Woo recorded a payment date, or the status says so (processing / completed). For a
    /// pay-on-delivery method (<see cref="ConnectorSettings.PayOnCompletionMethods"/>, default cod)
    /// Woo moves the order to processing before any money changed hands, so only completed counts.
    /// </summary>
    public static bool IsPaid(WooOrder order, ConnectorSettings settings)
    {
        if (PaysOnCompletion(order, settings))
            return order.Status == WooOrderStatuses.Completed;
        return order.DatePaidGmt is not null || order.Status is WooOrderStatuses.Processing or WooOrderStatuses.Completed;
    }

    /// <summary>The order's payment_method is one of <see cref="ConnectorSettings.PayOnCompletionMethods"/> (case-insensitive).</summary>
    public static bool PaysOnCompletion(WooOrder order, ConnectorSettings settings) =>
        !string.IsNullOrWhiteSpace(order.PaymentMethod)
        && (settings.PayOnCompletionMethods ?? []).Any(m => string.Equals(m?.Trim(), order.PaymentMethod.Trim(), StringComparison.OrdinalIgnoreCase));

    // --- order.import -------------------------------------------------------

    private async Task ImportAsync(ConnectorJob job, CancellationToken cancellationToken)
    {
        if (!long.TryParse(job.ResourceKey, NumberStyles.None, CultureInfo.InvariantCulture, out var orderId) || orderId <= 0)
            throw new ConnectorJobException($"order.import needs a Woo order id as its key (got '{job.ResourceKey}').", retryable: false);

        var client = _client();
        var order = await client.GetOrderAsync(orderId, cancellationToken);
        var row = await _services.Orders.GetAsync(ConnectorId, orderId, cancellationToken);

        if (order is null)
        {
            // Deleted for good (404): nothing holds stock for it in Woo any more.
            await _holds.ClearAsync(orderId, cancellationToken);
            if (row?.ImportStatus == ConnectorOrderStatuses.Imported)
                await ReportChangeAsync(orderId, row, "deleted", $"Woo order {orderId} was deleted in WooCommerce after it was imported", null,
                    [], job.JobId, cancellationToken);
            else
                _logger.LogInformation("Woo order {OrderId} no longer exists; nothing to import", orderId);
            return;
        }

        // A crash between the create and recording it leaves the row pending: ask POSitive before
        // anything else, so an order that IS there gets its invoice number and its later changes
        // (cancel, refund) reported instead of being gated out as "not importable".
        if (row?.ImportStatus == ConnectorOrderStatuses.Pending)
            row = await RecoverPendingAsync(order, row, job.JobId, cancellationToken);

        if (row?.ImportStatus == ConnectorOrderStatuses.Imported)
        {
            await _holds.UpdateAsync(order, imported: true, null, cancellationToken);
            await FinishImportedAsync(order, row, job.JobId, cancellationToken);
            return;
        }

        if (!IsImportable(order.Status, _services.Settings))
        {
            // Not imported, but Woo may hold stock for it (on-hold without importOnHold): record that.
            await _holds.UpdateAsync(order, imported: false, null, cancellationToken);
            _logger.LogInformation("Woo order {OrderId} is '{Status}'; not imported", orderId, order.Status);
            return;
        }

        await ImportNewAsync(order, row, job.JobId, cancellationToken);
    }

    private async Task ImportNewAsync(WooOrder order, ConnectorOrder? row, long jobId, CancellationToken cancellationToken)
    {
        var orderRef = Ref(order.Id);
        var localCustomerId = WooOrderMapper.LocalCustomerId(order);
        if (localCustomerId is null)
        {
            await _holds.UpdateAsync(order, imported: false, null, cancellationToken);
            await BlockAsync(order, row, OrderExceptionKinds.Rejected,
                $"Woo order #{order.Number} is a guest order with no billing email, so it cannot be matched to a POSitive customer. " +
                "Add an email to the order in WooCommerce, then retry.", null, jobId, cancellationToken);
            return;
        }

        // 1. Every line must resolve before anything is written (design §4.4).
        var resolved = await ResolveLinesAsync(order, cancellationToken);
        // Until POSitive has it, Woo's hold on its stock is ours to respect (cleared once imported).
        await _holds.UpdateAsync(order, imported: false, resolved, cancellationToken);
        var unmatched = resolved.Where(r => !r.Resolved).ToList();
        if (unmatched.Count > 0)
        {
            var list = string.Join("; ", unmatched.Select(u => $"line {u.LineId} '{u.Name}' (Woo item {u.WooItemId}, sku '{u.Sku}'): {u.Problem}"));
            await BlockAsync(order, row, OrderExceptionKinds.UnmatchedProduct,
                $"Woo order #{order.Number} has {unmatched.Count} line(s) with no POSitive item: {list}. Link the item(s), then retry.",
                JsonSerializer.Serialize(new { orderId = order.Id, order.Number, unmatched }), jobId, cancellationToken);
            return;
        }

        int? freight = null;
        if (order.ShippingLines.Any(s => s.Total != 0 || s.TotalTax != 0))
        {
            var matches = await _services.Pos.ResolveSkuAsync(WooOrderMapper.FreightSku, cancellationToken);
            freight = matches.Count == 1 ? matches[0] : null;
        }

        // 2. Map and reconcile on Woo's unrounded figures (dp=6) — still nothing written — then
        //    round to the cent so the lines and their tax add up to the header exactly.
        var unrounded = WooOrderMapper.Transaction(order, localCustomerId, resolved, freight, _time.LocalTimeZone);
        var balance = OrderReconciliation.Check(unrounded, order.Total, order.TotalTax);
        if (!balance.Balanced)
        {
            await BlockAsync(order, row, OrderExceptionKinds.TotalsMismatch,
                $"Woo order #{order.Number} does not add up: {balance.Describe()} Nothing was imported.",
                JsonSerializer.Serialize(new { orderId = order.Id, order.Total, order.TotalTax, balance, unrounded.Lines }),
                jobId, cancellationToken);
            return;
        }
        var transaction = WooOrderMapper.ToCents(unrounded);

        // 3. Customer (find / create / patch; idempotent by localCustomerId).
        var customerProblem = await EnsureCustomerAsync(order, localCustomerId, cancellationToken);
        if (customerProblem is not null)
        {
            await BlockAsync(order, row, OrderExceptionKinds.Rejected,
                $"Woo order #{order.Number}: POSitive refused the customer {localCustomerId}: {customerProblem}", null, jobId, cancellationToken);
            return;
        }

        // 4. Transaction. 409 transaction_exists = an earlier attempt wrote it.
        row = await SaveAsync(order, row, ConnectorOrderStatuses.Pending, null, null, null, cancellationToken);
        PosResult<PosTransactionCreated> created;
        try
        {
            created = await _services.Pos.CreateTransactionAsync(transaction, cancellationToken);
        }
        catch (PosApiException ex) when (ex.ErrorCode == PosErrorCodes.DatabaseUpgradeRequired)
        {
            // 503: the store skipped Prepare database; nothing was written. Tell the owner once (the
            // open row is refreshed on each retry), then rethrow so the job keeps retrying on the
            // order.import schedule (≈ 24 h) — the next try after the upgrade imports it.
            await RaiseAsync(OrderExceptionKinds.PosUpgradeRequired, orderRef,
                $"POSitive cannot take Woo order #{order.Number} yet: its database needs upgrading. Run Prepare database in the " +
                "POSitive Anywhere setup (or `PA.Server db upgrade`); the order is retried automatically.",
                null, null, jobId, cancellationToken);
            throw;
        }
        int? invoiceNo = null;
        decimal importedTotal = order.Total, importedTax = order.TotalTax;
        if (created.Success)
        {
            invoiceNo = created.Value!.TransactionId;
            var skipped = created.Value.Lines.Where(l => !string.Equals(l.Status, "Success", StringComparison.OrdinalIgnoreCase)).ToList();
            // Raised BEFORE the row says imported: a crash in between replays into the 409 path,
            // which re-derives it from the transaction POSitive holds.
            if (skipped.Count > 0)
                await RaiseAsync(OrderExceptionKinds.LinesSkipped, orderRef,
                    $"POSitive created invoice {invoiceNo} for Woo order #{order.Number} but skipped {skipped.Count} line(s): " +
                    string.Join("; ", skipped.Select(s => $"{s.LocalLineId}: {s.Status}")) + ". Add them to the invoice by hand.",
                    invoiceNo, JsonSerializer.Serialize(created.Value.Lines), jobId, cancellationToken);
        }
        else if (created.ErrorCode == PosErrorCodes.TransactionExists)
        {
            _logger.LogInformation("Woo order {OrderId} was already imported ({LocalTransactionId} exists); recording it", order.Id,
                transaction.LocalTransactionId);
            // The 409 names the existing transaction (G13); the lookup adds its figures and lines.
            invoiceNo = created.Value is { TransactionId: > 0 } existing ? existing.TransactionId : row.InvoiceNo;
            var found = await _services.Pos.FindTransactionAsync(Lookup(order, localCustomerId), cancellationToken);
            if (found.Success)
            {
                invoiceNo = found.Value!.TransactionId;
                (importedTotal, importedTax) = ImportedFigures(found.Value, order);
                await ReportSkippedOnRecoveryAsync(order, resolved, found.Value, jobId, cancellationToken);
            }
        }
        else if (created.ErrorCode == "customer_pending")
        {
            throw new ConnectorJobException($"Customer {localCustomerId} is still being added in POSitive; retrying.", retryable: true);
        }
        else
        {
            await BlockAsync(order, row, OrderExceptionKinds.Rejected,
                $"POSitive refused Woo order #{order.Number}: {created.Describe()}", JsonSerializer.Serialize(transaction), jobId,
                cancellationToken);
            return;
        }

        // 5. Imported: the row, the hold cleared, and the stock of every ordered item pushed (design §3).
        var imported = await RecordImportedAsync(order, row, invoiceNo, importedTotal, importedTax, resolved, cancellationToken);

        // 6. Payment, then post-import changes (a refund already on the order counts).
        await FinishImportedAsync(order, imported, jobId, cancellationToken);
    }

    /// <summary>
    /// The row is pending: a create was attempted and its outcome not recorded. If POSitive has the
    /// transaction, record the order as imported (with its invoice number) and carry on as for any
    /// imported order; otherwise leave the row as it is and import normally.
    /// </summary>
    private async Task<ConnectorOrder> RecoverPendingAsync(WooOrder order, ConnectorOrder row, long jobId, CancellationToken cancellationToken)
    {
        if (WooOrderMapper.LocalCustomerId(order) is not { } localCustomerId)
            return row;
        var found = await _services.Pos.FindTransactionAsync(Lookup(order, localCustomerId), cancellationToken);
        if (!found.Success)
            return row;

        _logger.LogInformation("Woo order {OrderId}: an earlier attempt created POSitive transaction {InvoiceNo} ({MatchedBy}); recording it",
            order.Id, found.Value!.TransactionId, found.Value.MatchedBy);
        var resolved = await ResolveLinesAsync(order, cancellationToken);
        await ReportSkippedOnRecoveryAsync(order, resolved, found.Value, jobId, cancellationToken);
        var (total, tax) = ImportedFigures(found.Value, order);
        return await RecordImportedAsync(order, row, found.Value.TransactionId, total, tax, resolved, cancellationToken);
    }

    private async Task<ConnectorOrder> RecordImportedAsync(WooOrder order, ConnectorOrder? row, int? invoiceNo, decimal importedTotal,
        decimal importedTax, IReadOnlyList<WooLineResolution> resolved, CancellationToken cancellationToken)
    {
        var imported = await SaveAsync(order, row, ConnectorOrderStatuses.Imported, invoiceNo, null, _time.GetUtcNow().UtcDateTime,
            cancellationToken, importedTotal, importedTax);
        await ResolveBlockingExceptionsAsync(order, invoiceNo, resolved, cancellationToken);
        await _holds.UpdateAsync(order, imported: true, resolved, cancellationToken);
        await EnqueueStockPushAsync(order.Id, resolved, cancellationToken);
        return imported;
    }

    private static PosTransactionLookup Lookup(WooOrder order, string localCustomerId) =>
        new(WooOrderMapper.LocalTransactionId(order.Id), localCustomerId, WooOrderMapper.Cut(order.Number, 30),
            order.Total - order.TotalTax, order.TotalTax);

    /// <summary>What POSitive holds (total + tax), else the order as Woo has it now.</summary>
    private static (decimal Total, decimal Tax) ImportedFigures(PosTransactionFound found, WooOrder order) =>
        found is { Total: { } total, TaxAmount: { } tax } ? (total + tax, tax) : (order.Total, order.TotalTax);

    /// <summary>
    /// On a recovery (409, or a pending row) the create's per-line answer is lost; when POSitive shows
    /// the transaction's lines, a product line it does not have is reported as skipped again.
    /// </summary>
    private async Task ReportSkippedOnRecoveryAsync(WooOrder order, IReadOnlyList<WooLineResolution> resolved, PosTransactionFound found,
        long jobId, CancellationToken cancellationToken)
    {
        if (found.ProductIds is null)
            return;
        var present = found.ProductIds.Where(p => p is not null).Select(p => p!.Value).ToList();
        var missing = new List<WooLineResolution>();
        foreach (var line in resolved.Where(r => r.InvNo is not null))
        {
            if (!present.Remove(line.InvNo!.Value))
                missing.Add(line);
        }
        if (missing.Count == 0)
            return;
        await RaiseAsync(OrderExceptionKinds.LinesSkipped, Ref(order.Id),
            $"POSitive holds invoice {found.TransactionId} for Woo order #{order.Number} without {missing.Count} of its line(s): " +
            string.Join("; ", missing.Select(m => $"woo-li{m.LineId.ToString(CultureInfo.InvariantCulture)} (item {m.InvNo})")) +
            ". Add them to the invoice by hand.",
            found.TransactionId, JsonSerializer.Serialize(new { found.ProductIds, missing }), jobId, cancellationToken);
    }

    /// <summary>The order's total or tax differs from what was imported (rows from before schema 2 never compare).</summary>
    public static bool EditedAfterImport(WooOrder order, ConnectorOrder row) =>
        (row.ImportedTotal is { } total && order.Total != total) || (row.ImportedTax is { } tax && order.TotalTax != tax);

    /// <summary>
    /// For an imported order: post the payment if it is now paid and not yet posted — never for a void
    /// order, and never when Woo's total no longer matches what was imported (the payment would not
    /// match the invoice; the change is reported instead); then report a cancel/refund/trash/edit.
    /// </summary>
    private async Task FinishImportedAsync(WooOrder order, ConnectorOrder row, long jobId, CancellationToken cancellationToken)
    {
        var voided = WooOrderStatuses.IsVoid(order.Status);
        var edited = EditedAfterImport(order, row);
        var amount = row.ImportedTotal ?? order.Total;
        if (!row.PaymentPosted && IsPaid(order, _services.Settings) && !voided && !edited && amount != 0)
        {
            // The tender comes from the paymentTenders setting (an unmapped method: card, tender R).
            var tender = PaymentTenders.For(_services.Settings, order.PaymentMethod);
            var payment = await _services.Pos.PostPaymentAsync(new PosPayment
            {
                LocalTransactionId = WooOrderMapper.LocalTransactionId(order.Id),
                LocalPaymentId = WooOrderMapper.LocalPaymentId(order.Id),
                PaymentType = PaymentTenders.PosPaymentType(tender),
                Amount = amount,
                CreatedAt = WooOrderMapper.StoreLocal(order.DatePaidUtc ?? order.DateCreatedUtc, _time.LocalTimeZone),
            }, cancellationToken);

            if (payment.Success || payment.ErrorCode == PosErrorCodes.PaymentExists)
            {
                // Posted before flipping: a crash in between replays into 409 payment_exists, never a second tender.
                await _services.Orders.TryMarkPaymentPostedAsync(ConnectorId, order.Id, cancellationToken);
                _logger.LogInformation("Woo order {OrderId}: payment {Amount} as {Tender} ({PaymentMethod}) {Outcome}", order.Id, amount,
                    PaymentTenders.Describe(tender), order.PaymentMethod ?? "no payment method", payment.Success ? "posted" : "was already posted");
            }
            else
            {
                await RaiseAsync(OrderExceptionKinds.Rejected, Ref(order.Id),
                    $"Woo order #{order.Number} was imported (invoice {row.InvoiceNo?.ToString(CultureInfo.InvariantCulture) ?? "?"}) " +
                    $"but POSitive refused its payment of {amount.ToString("0.00", CultureInfo.InvariantCulture)} as {PaymentTenders.Describe(tender)}: {payment.Describe()}. " +
                    "Take the payment in POSitive, or retry.", row.InvoiceNo, null, jobId, cancellationToken);
            }
        }

        await CheckChangesAsync(order, row, jobId, cancellationToken);
    }

    /// <summary>
    /// Raises <see cref="OrderExceptionKinds.ChangedAfterImport"/> once per change:
    /// the change "signature" (cancelled/refunded/trash status + refund ids + an edited
    /// total) is kept in a cursor, so redeliveries and polls of the same state stay quiet
    /// and a new refund reports again. Each new report also re-pushes the order's items:
    /// Woo restocks a cancelled or refunded order itself, and POSitive (which still holds
    /// the order until the owner adjusts it) is the stock authority.
    /// </summary>
    private async Task CheckChangesAsync(WooOrder order, ConnectorOrder row, long jobId, CancellationToken cancellationToken)
    {
        if (row.ExternalStatus != order.Status || row.ExternalModifiedAt != order.DateModifiedUtc)
            await _services.Orders.UpsertAsync(row with { ExternalStatus = order.Status, ExternalModifiedAt = order.DateModifiedUtc },
                cancellationToken);

        var voided = WooOrderStatuses.IsVoid(order.Status);
        var edited = EditedAfterImport(order, row);
        var parts = new List<string>();
        if (voided)
            parts.Add("status:" + order.Status);
        parts.AddRange(order.Refunds.Select(r => r.Id).Order().Select(id => "refund:" + id.ToString(CultureInfo.InvariantCulture)));
        if (edited)
            parts.Add("total:" + order.Total.ToString("0.00", CultureInfo.InvariantCulture) + "/" +
                      order.TotalTax.ToString("0.00", CultureInfo.InvariantCulture));
        if (parts.Count == 0)
            return;

        var what = new List<string>();
        if (order.Status == WooOrderStatuses.Trash)
            what.Add("was moved to the trash");
        else if (voided)
            what.Add($"is now '{order.Status}'");
        if (edited)
            what.Add($"was edited after import (total {M(row.ImportedTotal ?? order.Total)} incl. tax {M(row.ImportedTax ?? order.TotalTax)} " +
                     $"is now {M(order.Total)} incl. tax {M(order.TotalTax)})" +
                     (row.PaymentPosted ? "" : "; its payment was NOT posted, take it in POSitive"));
        if (order.Refunds.Count > 0)
            what.Add($"has {order.Refunds.Count} refund(s) totalling {(-order.Refunds.Sum(r => r.Total)).ToString("0.00", CultureInfo.InvariantCulture)}" +
                     " (" + string.Join("; ", order.Refunds.Select(r =>
                         $"#{r.Id} {(-r.Total).ToString("0.00", CultureInfo.InvariantCulture)}{(string.IsNullOrWhiteSpace(r.Reason) ? "" : " '" + r.Reason.Trim() + "'")}")) + ")");
        var invNos = await MappedInvNosAsync(order, cancellationToken);
        await ReportChangeAsync(order.Id, row, string.Join(",", parts),
            $"Woo order #{order.Number} {string.Join(" and ", what)} after it was imported",
            JsonSerializer.Serialize(new { orderId = order.Id, order.Status, order.Refunds, order.Total, order.TotalTax, row.ImportedTotal, row.ImportedTax }),
            invNos, jobId, cancellationToken);
    }

    private async Task ReportChangeAsync(long orderId, ConnectorOrder row, string signature, string text, string? snapshot,
        IReadOnlyList<int> repush, long jobId, CancellationToken cancellationToken)
    {
        var cursor = ChangeReportedCursor(orderId);
        if (await _services.Cursors.GetAsync(ConnectorId, cursor, cancellationToken) == signature)
            return;
        var invoice = row.InvoiceNo is { } n ? $"POSitive invoice {n.ToString(CultureInfo.InvariantCulture)}" : "POSitive";
        await RaiseAsync(OrderExceptionKinds.ChangedAfterImport, Ref(orderId),
            $"{text} as {invoice}. The connector does not reverse it: adjust the POSitive transaction by hand, then Mark as handled.",
            row.InvoiceNo, snapshot, jobId, cancellationToken);
        await _holds.EnqueuePushAsync(orderId, repush, cancellationToken);
        await _services.Cursors.SetAsync(ConnectorId, cursor, signature, cancellationToken);
    }

    /// <summary>The order's product lines that the item map links (no POSitive calls).</summary>
    private async Task<IReadOnlyList<int>> MappedInvNosAsync(WooOrder order, CancellationToken cancellationToken)
    {
        var invNos = new List<int>();
        foreach (var line in order.LineItems.Where(l => l.ItemId != 0))
        {
            if (await _services.Items.GetByExternalIdAsync(ConnectorId, line.ItemId, cancellationToken) is { } item)
                invNos.Add(item.InvNo);
        }
        return invNos;
    }

    private static string M(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    // --- steps ----------------------------------------------------------------

    /// <summary>
    /// Each Woo line → invNo: the item map first (variation id when non-zero,
    /// else product id), then the line's sku through the API's own sku rule
    /// (exactly one match). The sku fallback is case-insensitive, like Woo's.
    /// </summary>
    private async Task<IReadOnlyList<WooLineResolution>> ResolveLinesAsync(WooOrder order, CancellationToken cancellationToken)
    {
        var results = new List<WooLineResolution>();
        foreach (var line in order.LineItems)
        {
            var linked = line.ItemId == 0 ? null : await _services.Items.GetByExternalIdAsync(ConnectorId, line.ItemId, cancellationToken);
            if (linked is not null)
            {
                results.Add(new WooLineResolution(line.Id, line.ItemId, line.Sku, line.Name, linked.InvNo, null));
                continue;
            }

            var sku = WooOrderMapper.Clean(line.Sku);
            if (sku is null)
            {
                results.Add(new WooLineResolution(line.Id, line.ItemId, line.Sku, line.Name, null, "not linked and has no sku"));
                continue;
            }
            var matches = await _services.Pos.ResolveSkuAsync(sku, cancellationToken);
            results.Add(matches.Count switch
            {
                1 => new WooLineResolution(line.Id, line.ItemId, sku, line.Name, matches[0], null),
                0 => new WooLineResolution(line.Id, line.ItemId, sku, line.Name, null, "not linked, and no POSitive item has this sku/barcode"),
                _ => new WooLineResolution(line.Id, line.ItemId, sku, line.Name, null,
                    $"not linked, and the sku matches {matches.Count} POSitive items"),
            });
        }
        return results;
    }

    /// <summary>Find by localCustomerId, create if absent, PATCH changed contact fields, add the billing address. Null = ok; else the API's refusal.</summary>
    private async Task<string?> EnsureCustomerAsync(WooOrder order, string localCustomerId, CancellationToken cancellationToken)
    {
        var wanted = WooOrderMapper.Customer(order, localCustomerId);
        var found = await _services.Pos.FindCustomerDetailsAsync(localCustomerId, cancellationToken);
        if (!found.Success && found.ErrorCode != PosErrorCodes.CustomerNotFound)
            return found.Describe();

        if (!found.Success)
        {
            var created = await _services.Pos.CreateCustomerAsync(wanted, cancellationToken);
            if (!created.Success && created.ErrorCode == "customer_exists")
            {
                // Created by an earlier attempt whose answer was lost, or still being added.
                found = await _services.Pos.FindCustomerDetailsAsync(localCustomerId, cancellationToken);
                if (!found.Success)
                    throw new ConnectorJobException($"Customer {localCustomerId} exists but cannot be read yet; retrying.", retryable: true);
            }
            else if (!created.Success)
            {
                return created.Describe();
            }
            else
            {
                // Not the local customer id: a guest's is their billing email.
                _logger.LogInformation("Woo order {OrderId}: created POSitive customer {CustomerId}", order.Id, created.Value!.CustomerId);
            }
        }

        if (found.Success)
        {
            var changes = WooOrderMapper.CustomerChanges(wanted, found.Value!.Fields);
            if (changes is not null)
            {
                var patched = await _services.Pos.PatchCustomerAsync(found.Value.CustomerId, changes, cancellationToken);
                if (!patched.Success) // contact details never block an order
                    _logger.LogWarning("Woo order {OrderId}: could not update customer {CustomerId}: {Problem}", order.Id,
                        found.Value.CustomerId, patched.Describe());
            }
        }

        // The billing address lands once: POSitive answers address_exists when the customer already has one.
        if (WooOrderMapper.BillingAddress(order, localCustomerId) is { } billing)
        {
            var added = await _services.Pos.AddAddressAsync(billing, cancellationToken);
            if (!added.Success && added.ErrorCode != "address_exists")
                _logger.LogWarning("Woo order {OrderId}: the customer's billing address was not stored: {Problem}", order.Id,
                    added.Describe());
        }
        return null;
    }

    private Task EnqueueStockPushAsync(long orderId, IReadOnlyList<WooLineResolution> resolved, CancellationToken cancellationToken) =>
        _holds.EnqueuePushAsync(orderId, resolved.Where(r => r.InvNo is not null).Select(r => r.InvNo!.Value), cancellationToken);

    // --- bookkeeping ------------------------------------------------------------

    /// <summary>Records the order as an exception and raises <paramref name="kind"/>; nothing was written to POSitive.</summary>
    private async Task BlockAsync(WooOrder order, ConnectorOrder? row, string kind, string reason, string? snapshot, long jobId,
        CancellationToken cancellationToken)
    {
        // No reason in the log: it can name the customer (e.g. the guest key is the billing email).
        _logger.LogWarning("Woo order {OrderId} not imported ({Kind}); the reason is on the Exceptions page", order.Id, kind);
        await SaveAsync(order, row, ConnectorOrderStatuses.Exception, row?.InvoiceNo, reason, null, cancellationToken);
        await RaiseAsync(kind, Ref(order.Id), reason, null, snapshot, jobId, cancellationToken);
    }

    private async Task<ConnectorOrder> SaveAsync(WooOrder order, ConnectorOrder? row, string status, int? invoiceNo, string? error,
        DateTime? importedAt, CancellationToken cancellationToken, decimal? importedTotal = null, decimal? importedTax = null)
    {
        var saved = new ConnectorOrder(ConnectorId, order.Id, order.Number, invoiceNo ?? row?.InvoiceNo, row?.OrdGuid, status,
            row?.PaymentPosted ?? false, row?.PaymentPostedAt, order.Status, order.DateModifiedUtc, error, importedAt ?? row?.ImportedAt,
            importedTotal ?? row?.ImportedTotal, importedTax ?? row?.ImportedTax);
        await _services.Orders.UpsertAsync(saved, cancellationToken);
        return saved;
    }

    /// <summary>
    /// Opens (or refreshes) the order's exception and logs it like the catalog's ("Order exception
    /// &lt;kind&gt; &lt;order id&gt;: raised …"). The log line names the kind, the Woo order id, the
    /// occurrence and the POSitive invoice only — never the reason, which can carry the customer's
    /// name or email (the reason stays on the Exceptions page).
    /// </summary>
    private async Task<long> RaiseAsync(string kind, string orderRef, string reason, int? invoiceNo, string? snapshot, long jobId,
        CancellationToken cancellationToken)
    {
        var id = await _services.Exceptions.RaiseAsync(new ConnectorExceptionInput(ConnectorId, kind, orderRef, reason,
            invoiceNo?.ToString(CultureInfo.InvariantCulture), snapshot, jobId), cancellationToken);
        var occurrences = (await _services.Exceptions.GetAsync(id, cancellationToken))?.Occurrences ?? 1;
        _logger.LogInformation("Order exception {Kind} {Ref}: {Outcome} (exception {ExceptionId}{Invoice})", kind, orderRef,
            occurrences > 1 ? $"raised again, occurrence {occurrences.ToString(CultureInfo.InvariantCulture)}" : "raised",
            id, invoiceNo is { } n ? ", invoice " + n.ToString(CultureInfo.InvariantCulture) : "");
        return id;
    }

    /// <summary>
    /// A retry that now imports closes the order's open blocking exceptions as resolved, with what fixed
    /// them ("Imported as POSitive order 50001919 on retry; line 14 'Demo Widget' linked to POSitive item
    /// 900996"); one the owner already closed with Retry gets the same outcome recorded.
    /// </summary>
    private async Task ResolveBlockingExceptionsAsync(WooOrder order, int? invoiceNo, IReadOnlyList<WooLineResolution> resolved,
        CancellationToken cancellationToken)
    {
        var orderRef = Ref(order.Id);
        foreach (var row in await _services.Exceptions.ListAsync(ConnectorId, null, 500, cancellationToken))
        {
            if (row.ExternalRef != orderRef || !OrderExceptionKinds.Blocking.Contains(row.Kind))
                continue;
            var resolution = Resolution(row, invoiceNo, resolved);
            if (row.Status == ConnectorExceptionStatuses.Open
                && await _services.Exceptions.CloseAsync(row.ExceptionId, ConnectorExceptionStatuses.Resolved, "connector: imported on retry",
                    resolution, cancellationToken))
                _logger.LogInformation("Order exception {Kind} {Ref}: {Outcome} (exception {ExceptionId}{Invoice})", row.Kind, row.ExternalRef,
                    "resolved, imported on retry", row.ExceptionId,
                    invoiceNo is { } n ? ", invoice " + n.ToString(CultureInfo.InvariantCulture) : "");
            else if (row.Status == ConnectorExceptionStatuses.Retried && row.Resolution is null
                     && await _services.Exceptions.SetResolutionAsync(row.ExceptionId, resolution, cancellationToken))
                _logger.LogInformation("Order exception {Kind} {Ref}: {Outcome} (exception {ExceptionId}{Invoice})", row.Kind, row.ExternalRef,
                    "resolved by the retry", row.ExceptionId,
                    invoiceNo is { } m ? ", invoice " + m.ToString(CultureInfo.InvariantCulture) : "");
        }
    }

    /// <summary>The outcome text for a blocking exception the import just got past.</summary>
    internal static string Resolution(ConnectorExceptionRow row, int? invoiceNo, IReadOnlyList<WooLineResolution> resolved)
    {
        var text = invoiceNo is { } n
            ? $"Imported as POSitive order {n.ToString(CultureInfo.InvariantCulture)} on retry"
            : "Imported into POSitive on retry";
        if (row.Kind == OrderExceptionKinds.UnmatchedProduct && UnmatchedLineIds(row.Snapshot) is { Count: > 0 } lineIds)
        {
            var links = resolved.Where(r => r.InvNo is not null && lineIds.Contains(r.LineId))
                .Select(r => $"line {r.LineId.ToString(CultureInfo.InvariantCulture)}"
                             + (string.IsNullOrWhiteSpace(r.Name) ? "" : $" '{r.Name.Trim()}'")
                             + $" linked to POSitive item {r.InvNo!.Value.ToString(CultureInfo.InvariantCulture)}")
                .ToList();
            if (links.Count > 0)
                text += "; " + string.Join("; ", links);
        }
        return text + ".";
    }

    /// <summary>The line ids an order_unmatched_product snapshot lists ({"unmatched":[{"LineId":…}]}); empty when unreadable.</summary>
    private static HashSet<long> UnmatchedLineIds(string? snapshot)
    {
        var ids = new HashSet<long>();
        if (string.IsNullOrWhiteSpace(snapshot))
            return ids;
        try
        {
            using var doc = JsonDocument.Parse(snapshot);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "unmatched", StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var line in property.Value.EnumerateArray())
                {
                    if (line.ValueKind != JsonValueKind.Object)
                        continue;
                    foreach (var field in line.EnumerateObject())
                    {
                        if (string.Equals(field.Name, "lineId", StringComparison.OrdinalIgnoreCase) && field.Value.TryGetInt64(out var id))
                            ids.Add(id);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // An unreadable snapshot only loses the per-line detail.
        }
        return ids;
    }

    private static string Ref(long orderId) => orderId.ToString(CultureInfo.InvariantCulture);
}
