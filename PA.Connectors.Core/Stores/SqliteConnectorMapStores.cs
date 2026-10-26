using Microsoft.Data.Sqlite;
using static PA.Connectors.Core.Stores.ConnectorDatabase;

namespace PA.Connectors.Core.Stores;

/// <summary>Watermarks over ConnectorCursor.</summary>
public sealed class SqliteConnectorCursorStore : IConnectorCursorStore
{
    private readonly ConnectorDatabase _db;

    public SqliteConnectorCursorStore(ConnectorDatabase db)
    {
        _db = db;
    }

    public Task<string?> GetAsync(string connectorId, string cursorName, CancellationToken cancellationToken = default) =>
        _db.ReadAsync(async connection => (string?)await ScalarAsync(connection, null,
            "SELECT Value FROM ConnectorCursor WHERE ConnectorId = $c AND CursorName = $n;",
            cancellationToken, ("$c", connectorId), ("$n", cursorName)), cancellationToken);

    public Task SetAsync(string connectorId, string cursorName, string? value, CancellationToken cancellationToken = default) =>
        _db.WriteAsync((connection, transaction) => ExecAsync(connection, transaction,
            """
            INSERT INTO ConnectorCursor (ConnectorId, CursorName, Value, UpdatedAt) VALUES ($c, $n, $v, $now)
            ON CONFLICT (ConnectorId, CursorName) DO UPDATE SET Value = excluded.Value, UpdatedAt = excluded.UpdatedAt;
            """, cancellationToken, ("$c", connectorId), ("$n", cursorName), ("$v", value),
            ("$now", FormatTime(_db.UtcNow()))), cancellationToken);
}

/// <summary>Item map over ConnectorItem.</summary>
public sealed class SqliteConnectorItemStore : IConnectorItemStore
{
    private const string Columns =
        "ConnectorId, InvNo, ExternalId, ExternalParentId, ExternalType, Sku, LastPushHash, LastPushedAt";

    private readonly ConnectorDatabase _db;

    public SqliteConnectorItemStore(ConnectorDatabase db)
    {
        _db = db;
    }

    public Task<ConnectorItem?> GetByInvNoAsync(string connectorId, int invNo, CancellationToken cancellationToken = default) =>
        _db.ReadAsync(async connection => (await QueryAsync(connection, null,
            $"SELECT {Columns} FROM ConnectorItem WHERE ConnectorId = $c AND InvNo = $inv;", Map, cancellationToken,
            ("$c", connectorId), ("$inv", invNo))).SingleOrDefault(), cancellationToken);

    public Task<ConnectorItem?> GetByExternalIdAsync(string connectorId, long externalId, CancellationToken cancellationToken = default) =>
        _db.ReadAsync(async connection => (await QueryAsync(connection, null,
            $"SELECT {Columns} FROM ConnectorItem WHERE ConnectorId = $c AND ExternalId = $ext;", Map, cancellationToken,
            ("$c", connectorId), ("$ext", externalId))).SingleOrDefault(), cancellationToken);

    public Task<IReadOnlyList<ConnectorItem>> ListAsync(string connectorId, CancellationToken cancellationToken = default) =>
        _db.ReadAsync<IReadOnlyList<ConnectorItem>>(async connection => await QueryAsync(connection, null,
            $"SELECT {Columns} FROM ConnectorItem WHERE ConnectorId = $c ORDER BY InvNo;", Map, cancellationToken,
            ("$c", connectorId)), cancellationToken);

    /// <summary>Same Woo id: the push hash/time are kept unless new ones are given. A relink (different id) resets them.</summary>
    public Task UpsertAsync(ConnectorItem item, CancellationToken cancellationToken = default) =>
        _db.WriteAsync((connection, transaction) => ExecAsync(connection, transaction,
            """
            INSERT INTO ConnectorItem
                (ConnectorId, InvNo, ExternalId, ExternalParentId, ExternalType, Sku, LastPushHash, LastPushedAt, CreatedAt, UpdatedAt)
            VALUES ($c, $inv, $ext, $parent, $type, $sku, $hash, $pushedAt, $now, $now)
            ON CONFLICT (ConnectorId, InvNo) DO UPDATE SET
                LastPushHash = CASE WHEN ExternalId = excluded.ExternalId
                                    THEN COALESCE(excluded.LastPushHash, LastPushHash) ELSE excluded.LastPushHash END,
                LastPushedAt = CASE WHEN ExternalId = excluded.ExternalId
                                    THEN COALESCE(excluded.LastPushedAt, LastPushedAt) ELSE excluded.LastPushedAt END,
                ExternalId = excluded.ExternalId,
                ExternalParentId = excluded.ExternalParentId,
                ExternalType = excluded.ExternalType,
                Sku = excluded.Sku,
                UpdatedAt = excluded.UpdatedAt;
            """, cancellationToken, ("$c", item.ConnectorId), ("$inv", item.InvNo), ("$ext", item.ExternalId),
            ("$parent", item.ExternalParentId), ("$type", item.ExternalType), ("$sku", item.Sku),
            ("$hash", item.LastPushHash), ("$pushedAt", FormatTime(item.LastPushedAt)),
            ("$now", FormatTime(_db.UtcNow()))), cancellationToken);

    public Task SetPushHashAsync(string connectorId, int invNo, string pushHash, CancellationToken cancellationToken = default) =>
        _db.WriteAsync((connection, transaction) =>
        {
            var now = FormatTime(_db.UtcNow());
            return ExecAsync(connection, transaction,
                """
                UPDATE ConnectorItem SET LastPushHash = $hash, LastPushedAt = $now, UpdatedAt = $now
                WHERE ConnectorId = $c AND InvNo = $inv;
                """, cancellationToken, ("$hash", pushHash), ("$now", now), ("$c", connectorId), ("$inv", invNo));
        }, cancellationToken);

    public Task<bool> RemoveAsync(string connectorId, int invNo, CancellationToken cancellationToken = default) =>
        _db.WriteAsync(async (connection, transaction) => await ExecAsync(connection, transaction,
            "DELETE FROM ConnectorItem WHERE ConnectorId = $c AND InvNo = $inv;", cancellationToken,
            ("$c", connectorId), ("$inv", invNo)) > 0, cancellationToken);

    private static ConnectorItem Map(SqliteDataReader r) => new(
        Str(r, "ConnectorId")!,
        (int)Long(r, "InvNo"),
        Long(r, "ExternalId"),
        Long(r, "ExternalParentId"),
        Str(r, "ExternalType"),
        Str(r, "Sku"),
        Str(r, "LastPushHash"),
        ReadNullableTime(r, "LastPushedAt"));
}

/// <summary>Order map over ConnectorOrder.</summary>
public sealed class SqliteConnectorOrderStore : IConnectorOrderStore
{
    private const string Columns =
        "ConnectorId, ExternalOrderId, OrderNumber, InvoiceNo, OrdGuid, ImportStatus, PaymentPosted, PaymentPostedAt, " +
        "ExternalStatus, ExternalModifiedAt, LastError, ImportedAt, ImportedTotal, ImportedTax";

    private readonly ConnectorDatabase _db;

    public SqliteConnectorOrderStore(ConnectorDatabase db)
    {
        _db = db;
    }

    public Task<ConnectorOrder?> GetAsync(string connectorId, long externalOrderId, CancellationToken cancellationToken = default) =>
        _db.ReadAsync(async connection => (await QueryAsync(connection, null,
            $"SELECT {Columns} FROM ConnectorOrder WHERE ConnectorId = $c AND ExternalOrderId = $o;", Map, cancellationToken,
            ("$c", connectorId), ("$o", externalOrderId))).SingleOrDefault(), cancellationToken);

    /// <summary>Never touches PaymentPosted / PaymentPostedAt — only <see cref="TryMarkPaymentPostedAsync"/> sets them.</summary>
    public Task UpsertAsync(ConnectorOrder order, CancellationToken cancellationToken = default) =>
        _db.WriteAsync((connection, transaction) => ExecAsync(connection, transaction,
            """
            INSERT INTO ConnectorOrder
                (ConnectorId, ExternalOrderId, OrderNumber, InvoiceNo, OrdGuid, ImportStatus,
                 ExternalStatus, ExternalModifiedAt, LastError, ImportedAt, ImportedTotal, ImportedTax, CreatedAt, UpdatedAt)
            VALUES ($c, $o, $num, $inv, $guid, $status, $extStatus, $extModified, $err, $importedAt, $total, $tax, $now, $now)
            ON CONFLICT (ConnectorId, ExternalOrderId) DO UPDATE SET
                OrderNumber = excluded.OrderNumber,
                InvoiceNo = excluded.InvoiceNo,
                OrdGuid = excluded.OrdGuid,
                ImportStatus = excluded.ImportStatus,
                ExternalStatus = excluded.ExternalStatus,
                ExternalModifiedAt = excluded.ExternalModifiedAt,
                LastError = excluded.LastError,
                ImportedAt = excluded.ImportedAt,
                ImportedTotal = excluded.ImportedTotal,
                ImportedTax = excluded.ImportedTax,
                UpdatedAt = excluded.UpdatedAt;
            """, cancellationToken, ("$c", order.ConnectorId), ("$o", order.ExternalOrderId), ("$num", order.OrderNumber),
            ("$inv", order.InvoiceNo), ("$guid", order.OrdGuid), ("$status", order.ImportStatus),
            ("$extStatus", order.ExternalStatus), ("$extModified", FormatTime(order.ExternalModifiedAt)),
            ("$err", order.LastError is null ? null : Truncate(order.LastError, 1000)),
            ("$importedAt", FormatTime(order.ImportedAt)), ("$total", FormatDecimal(order.ImportedTotal)),
            ("$tax", FormatDecimal(order.ImportedTax)), ("$now", FormatTime(_db.UtcNow()))), cancellationToken);

    public Task<bool> TryMarkPaymentPostedAsync(string connectorId, long externalOrderId, CancellationToken cancellationToken = default) =>
        _db.WriteAsync(async (connection, transaction) =>
        {
            var now = FormatTime(_db.UtcNow());
            return await ExecAsync(connection, transaction,
                """
                UPDATE ConnectorOrder SET PaymentPosted = 1, PaymentPostedAt = $now, UpdatedAt = $now
                WHERE ConnectorId = $c AND ExternalOrderId = $o AND PaymentPosted = 0;
                """, cancellationToken, ("$now", now), ("$c", connectorId), ("$o", externalOrderId)) > 0;
        }, cancellationToken);

    private static ConnectorOrder Map(SqliteDataReader r) => new(
        Str(r, "ConnectorId")!,
        Long(r, "ExternalOrderId"),
        Str(r, "OrderNumber"),
        NullableLong(r, "InvoiceNo") is { } invoice ? (int)invoice : null,
        Str(r, "OrdGuid"),
        Str(r, "ImportStatus")!,
        Long(r, "PaymentPosted") != 0,
        ReadNullableTime(r, "PaymentPostedAt"),
        Str(r, "ExternalStatus"),
        ReadNullableTime(r, "ExternalModifiedAt"),
        Str(r, "LastError"),
        ReadNullableTime(r, "ImportedAt"),
        ReadNullableDecimal(r, "ImportedTotal"),
        ReadNullableDecimal(r, "ImportedTax"));

    // --- stock holds ----------------------------------------------------------

    public Task<IReadOnlyList<int>> SetStockHoldAsync(string connectorId, long externalOrderId, string? externalStatus,
        IReadOnlyList<ConnectorStockHoldLine> lines, int unresolvedLines, CancellationToken cancellationToken = default) =>
        _db.WriteAsync<IReadOnlyList<int>>(async (connection, transaction) =>
        {
            var now = FormatTime(_db.UtcNow());
            var before = (await QueryAsync(connection, transaction,
                    "SELECT InvNo, Quantity FROM ConnectorStockHold WHERE ConnectorId = $c AND ExternalOrderId = $o;",
                    r => ((int)Long(r, "InvNo"), ReadNullableDecimal(r, "Quantity") ?? 0m), cancellationToken,
                    ("$c", connectorId), ("$o", externalOrderId)))
                .ToDictionary(x => x.Item1, x => x.Item2);
            var after = lines.Where(l => l.InvNo > 0).GroupBy(l => l.InvNo)
                .Select(g => (InvNo: g.Key, Quantity: g.Sum(l => l.Quantity)))
                .Where(x => x.Quantity > 0)
                .ToDictionary(x => x.InvNo, x => x.Quantity);

            await ExecAsync(connection, transaction,
                "DELETE FROM ConnectorStockHold WHERE ConnectorId = $c AND ExternalOrderId = $o;", cancellationToken,
                ("$c", connectorId), ("$o", externalOrderId));
            foreach (var (invNo, quantity) in after)
                await ExecAsync(connection, transaction,
                    """
                    INSERT INTO ConnectorStockHold (ConnectorId, ExternalOrderId, InvNo, Quantity, ExternalStatus, UnresolvedLines, CheckedAt)
                    VALUES ($c, $o, $inv, $qty, $status, $unresolved, $now);
                    """, cancellationToken, ("$c", connectorId), ("$o", externalOrderId), ("$inv", invNo),
                    ("$qty", FormatDecimal(quantity)), ("$status", externalStatus), ("$unresolved", unresolvedLines), ("$now", now));

            return before.Keys.Union(after.Keys)
                .Where(i => before.GetValueOrDefault(i) != after.GetValueOrDefault(i))
                .Order().ToList();
        }, cancellationToken);

    public Task<IReadOnlyDictionary<int, decimal>> GetStockHoldTotalsAsync(string connectorId, CancellationToken cancellationToken = default) =>
        _db.ReadAsync<IReadOnlyDictionary<int, decimal>>(async connection =>
            (await QueryAsync(connection, null,
                "SELECT InvNo, Quantity FROM ConnectorStockHold WHERE ConnectorId = $c;",
                r => ((int)Long(r, "InvNo"), ReadNullableDecimal(r, "Quantity") ?? 0m), cancellationToken, ("$c", connectorId)))
            .GroupBy(x => x.Item1)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Item2)), cancellationToken);

    public Task<IReadOnlyList<long>> ListStockHoldOrdersAsync(string connectorId, DateTime checkedBeforeUtc, int max,
        CancellationToken cancellationToken = default) =>
        _db.ReadAsync<IReadOnlyList<long>>(async connection => await QueryAsync(connection, null,
            """
            SELECT ExternalOrderId FROM ConnectorStockHold
            WHERE ConnectorId = $c
            GROUP BY ExternalOrderId
            HAVING MIN(CheckedAt) < $before
            ORDER BY MIN(CheckedAt), ExternalOrderId
            LIMIT $max;
            """, r => Long(r, "ExternalOrderId"), cancellationToken,
            ("$c", connectorId), ("$before", FormatTime(checkedBeforeUtc)), ("$max", Math.Max(1, max))), cancellationToken);
}

/// <summary>All five stores over one database — what the host registers.</summary>
public sealed record ConnectorStores(
    IConnectorJobStore Jobs,
    IConnectorCursorStore Cursors,
    IConnectorExceptionStore Exceptions,
    IConnectorItemStore Items,
    IConnectorOrderStore Orders)
{
    public static ConnectorStores Sqlite(ConnectorDatabase db) => new(
        new SqliteConnectorJobStore(db),
        new SqliteConnectorCursorStore(db),
        new SqliteConnectorExceptionStore(db),
        new SqliteConnectorItemStore(db),
        new SqliteConnectorOrderStore(db));
}
