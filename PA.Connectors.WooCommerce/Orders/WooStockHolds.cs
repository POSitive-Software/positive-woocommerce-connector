using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PA.Connectors.Core;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Stores;
using PA.Connectors.WooCommerce.Models;

namespace PA.Connectors.WooCommerce.Orders;

/// <summary>
/// Keeps ConnectorStockHold in step with Woo: an order Woo holds stock for (on-hold, processing,
/// completed) that is NOT imported into POSitive — an on-hold order with importOnHold off, or one
/// held as an exception (unmatched product, totals mismatch, guest without email, rejected), or one
/// whose import is still retrying — is recorded per item, and every push subtracts it
/// (<c>CatalogMapper</c>). Without it a push would hand Woo's own hold back to the web: oversell.
/// <para>
/// Lines resolve through the item map (or, during an import, the import's own resolution). A line
/// no item can be found for cannot be subtracted; it is counted (UnresolvedLines) and logged. The
/// hold is cleared when the order is imported (POSitive's availableLive then counts it), cancelled,
/// refunded, trashed, deleted, or back in pending/failed. Whenever the held quantity of an item
/// changes, a stock push for the order's items is queued (key <c>order:&lt;id&gt;</c>, the same key the
/// import uses, so they coalesce).
/// </para>
/// </summary>
internal sealed class WooStockHolds
{
    private readonly ConnectorServices _services;
    private readonly ILogger _logger;

    public WooStockHolds(ConnectorServices services, ILogger logger)
    {
        _services = services;
        _logger = logger;
    }

    private string ConnectorId => _services.Settings.Id;

    /// <summary>
    /// Records (or clears) the hold for <paramref name="order"/> as Woo has it now.
    /// <paramref name="imported"/> = POSitive has the order. <paramref name="resolved"/> = the import's
    /// resolution of <see cref="WooOrder.LineItems"/> (index-aligned), else the item map is used.
    /// </summary>
    public async Task UpdateAsync(WooOrder order, bool imported, IReadOnlyList<WooLineResolution>? resolved,
        CancellationToken cancellationToken)
    {
        var holds = !imported && WooOrderStatuses.HoldsStock(order.Status);
        var lines = new List<ConnectorStockHoldLine>();
        var allInvNos = new List<int>();
        var unresolved = 0;
        for (var i = 0; i < order.LineItems.Count; i++)
        {
            var line = order.LineItems[i];
            var invNo = resolved is not null && i < resolved.Count
                ? resolved[i].InvNo
                : line.ItemId == 0 ? null : (await _services.Items.GetByExternalIdAsync(ConnectorId, line.ItemId, cancellationToken))?.InvNo;
            if (invNo is { } inv)
            {
                allInvNos.Add(inv);
                if (holds && line.Quantity > 0)
                    lines.Add(new ConnectorStockHoldLine(inv, line.Quantity));
            }
            else if (holds)
            {
                unresolved++;
            }
        }

        var changed = await _services.Orders.SetStockHoldAsync(ConnectorId, order.Id, order.Status, holds ? lines : [],
            holds ? unresolved : 0, cancellationToken);
        if (holds && unresolved > 0)
            _logger.LogWarning(
                "Woo order {OrderId} ('{Status}', not in POSitive) holds Woo stock, but {Unresolved} line(s) match no POSitive item, so their quantity cannot be held back from the push",
                order.Id, order.Status, unresolved);
        if (changed.Count > 0)
            await EnqueuePushAsync(order.Id, changed.Concat(allInvNos), cancellationToken);
    }

    /// <summary>The order is gone (deleted in Woo): drop its hold and re-push what it held.</summary>
    public async Task ClearAsync(long orderId, CancellationToken cancellationToken)
    {
        var changed = await _services.Orders.SetStockHoldAsync(ConnectorId, orderId, null, [], 0, cancellationToken);
        if (changed.Count > 0)
            await EnqueuePushAsync(orderId, changed, cancellationToken);
    }

    /// <summary>catalog.push of exactly these items (key order:&lt;id&gt;).</summary>
    public Task EnqueuePushAsync(long orderId, IEnumerable<int> invNos, CancellationToken cancellationToken) =>
        EnqueuePushAsync(_services, orderId, invNos, cancellationToken);

    internal static async Task EnqueuePushAsync(ConnectorServices services, long orderId, IEnumerable<int> invNos,
        CancellationToken cancellationToken)
    {
        var list = invNos.Where(i => i > 0).Distinct().Order().ToList();
        if (list.Count == 0)
            return;
        await services.Jobs.EnqueueAsync(new JobRequest(
            services.Settings.Id, ConnectorJobKinds.CatalogPush, "order:" + orderId.ToString(CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(new CatalogPushPayload(list))), cancellationToken);
    }
}
