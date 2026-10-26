using System.Globalization;
using Microsoft.Extensions.Logging;
using PA.Connectors.Core;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Stores;
using PA.Connectors.WooCommerce.Models;

namespace PA.Connectors.WooCommerce.Orders;

/// <summary>
/// order.poll — the backstop for webhooks Woo never retries (design §4.1): list
/// orders modified after the cursor and enqueue an order.import for each one
/// that may need work. The request itself also flushes Woo's queued webhook
/// deliveries (Action Scheduler runs on the next request, S0bc).
/// <para>
/// Cursor rules (no gaps): the cursor only ever moves to a <c>date_modified_gmt</c>
/// Woo itself reported (never this machine's clock, which may differ from the
/// store's), only after the whole listing was read, and each poll re-reads an
/// <see cref="Overlap"/> before it so an order saved while the last poll ran
/// is not skipped. Re-listed orders whose modified time the order store already
/// holds are not re-enqueued.
/// </para>
/// <para>
/// Each poll also: records the stock hold of every listed order Woo holds stock for
/// but POSitive does not have (on-hold without importOnHold, exceptions —
/// <see cref="WooStockHolds"/>); re-reads held orders not seen for
/// <see cref="HoldRecheck"/> (a deleted or trashed order is not listed, and
/// would otherwise hold stock forever); and re-queues order.import for orders
/// whose last import job was dead-lettered on transient faults and that are not
/// fully imported, so a long outage never strands an order.
/// </para>
/// </summary>
public sealed class WooOrderPoll
{
    /// <summary>Re-read window before the cursor.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    /// <summary>
    /// With no cursor yet (first poll after connecting), look back this far —
    /// short on purpose: older orders were most likely handled by hand before
    /// the store was connected. (S5's connect wizard may seed the cursor.)
    /// </summary>
    public static readonly TimeSpan InitialLookback = TimeSpan.FromHours(1);

    /// <summary>A held order not listed by a poll for this long is re-read by id.</summary>
    public static readonly TimeSpan HoldRecheck = TimeSpan.FromHours(1);

    /// <summary>Held orders re-read per poll at most.</summary>
    public const int HoldRecheckBatch = 50;

    private readonly ConnectorServices _services;
    private readonly Func<WooClient> _client;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly WooStockHolds _holds;
    private bool _healthQueued;

    internal WooOrderPoll(ConnectorServices services, Func<WooClient> client, TimeProvider time, WooStockHolds holds)
    {
        _services = services;
        _client = client;
        _time = time;
        _logger = services.LoggerFactory.CreateLogger<WooOrderPoll>();
        _holds = holds;
    }

    private string ConnectorId => _services.Settings.Id;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // webhook.health is hourly; the first poll after a start also asks for one.
        if (!_healthQueued)
        {
            await _services.Jobs.EnqueueAsync(new JobRequest(ConnectorId, ConnectorJobKinds.WebhookHealth), cancellationToken);
            _healthQueued = true;
        }

        var stored = ParseUtc(await _services.Cursors.GetAsync(ConnectorId, ConnectorCursorNames.OrdersModifiedAfter, cancellationToken));
        var cursor = stored ?? _time.GetUtcNow().UtcDateTime - InitialLookback;
        var since = cursor - Overlap;

        var newest = cursor;
        int seen = 0, queued = 0;
        var listed = new HashSet<long>();
        var client = _client();
        await foreach (var order in client.GetOrdersModifiedAfterAsync(since, cancellationToken: cancellationToken))
        {
            seen++;
            listed.Add(order.Id);
            if (order.DateModifiedUtc is { } modified && modified > newest)
                newest = modified;
            var row = await _services.Orders.GetAsync(ConnectorId, order.Id, cancellationToken);
            if (row?.ImportStatus != ConnectorOrderStatuses.Imported)
                await _holds.UpdateAsync(order, imported: false, null, cancellationToken);
            if (NeedsImport(order, row))
            {
                await EnqueueImportAsync(order.Id, "poll", cancellationToken);
                queued++;
            }
        }

        if (stored is null || newest > cursor)
            await _services.Cursors.SetAsync(ConnectorId, ConnectorCursorNames.OrdersModifiedAfter, FormatUtc(newest), cancellationToken);
        if (queued > 0)
            _logger.LogInformation("Order poll for {ConnectorId}: {Seen} order(s) modified since {Since:o}, {Queued} queued for import",
                ConnectorId, seen, since, queued);

        await RecheckHoldsAsync(client, listed, cancellationToken);
        await RequeueStrandedAsync(cancellationToken);
    }

    /// <summary>Held orders the listing did not show for a while: re-read them (deleted = 404, trashed = status trash).</summary>
    private async Task RecheckHoldsAsync(WooClient client, HashSet<long> listed, CancellationToken cancellationToken)
    {
        var stale = await _services.Orders.ListStockHoldOrdersAsync(ConnectorId, _time.GetUtcNow().UtcDateTime - HoldRecheck,
            HoldRecheckBatch, cancellationToken);
        foreach (var orderId in stale.Where(id => !listed.Contains(id)))
        {
            var order = await client.GetOrderAsync(orderId, cancellationToken);
            if (order is null)
            {
                await _holds.ClearAsync(orderId, cancellationToken);
                continue;
            }
            var row = await _services.Orders.GetAsync(ConnectorId, orderId, cancellationToken);
            await _holds.UpdateAsync(order, row?.ImportStatus == ConnectorOrderStatuses.Imported, null, cancellationToken);
        }
    }

    /// <summary>
    /// order.import jobs dead-lettered after running out of attempts on transient faults (an outage
    /// longer than the order.import schedule) whose order is not fully imported (not imported, or its
    /// payment not posted): queued again, so nothing is stranded.
    /// </summary>
    private async Task RequeueStrandedAsync(CancellationToken cancellationToken)
    {
        var requeued = 0;
        foreach (var key in await _services.Jobs.ListStrandedAsync(ConnectorId, ConnectorJobKinds.OrderImport, 100, cancellationToken))
        {
            if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var orderId) || orderId <= 0)
                continue;
            var row = await _services.Orders.GetAsync(ConnectorId, orderId, cancellationToken);
            if (row is { ImportStatus: ConnectorOrderStatuses.Imported, PaymentPosted: true })
                continue;
            await EnqueueImportAsync(orderId, "poll-requeue", cancellationToken);
            requeued++;
        }
        if (requeued > 0)
            _logger.LogWarning("Order poll for {ConnectorId}: re-queued {Count} order import(s) that had run out of retries", ConnectorId, requeued);
    }

    private Task EnqueueImportAsync(long orderId, string source, CancellationToken cancellationToken) =>
        _services.Jobs.EnqueueAsync(new JobRequest(ConnectorId, ConnectorJobKinds.OrderImport,
            orderId.ToString(CultureInfo.InvariantCulture), $$"""{"source":"{{source}}"}"""), cancellationToken);

    /// <summary>
    /// Worth an order.import: the store has never seen it at this modified time,
    /// and it is importable now — or it was imported (a cancel / refund to report,
    /// or a payment to post). Never-imported orders in other statuses are skipped.
    /// </summary>
    private bool NeedsImport(WooOrder order, ConnectorOrder? row)
    {
        if (row?.ExternalModifiedAt is { } known && order.DateModifiedUtc is { } modified && modified <= known)
            return false;
        // Imported: a cancel / refund to report, or a payment to post. Pending: a create whose outcome
        // was never recorded — the import asks POSitive whatever the status is now.
        if (row?.ImportStatus is ConnectorOrderStatuses.Imported or ConnectorOrderStatuses.Pending)
            return true;
        return WooOrderImport.IsImportable(order.Status, _services.Settings);
    }

    private static string FormatUtc(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static DateTime? ParseUtc(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
}
