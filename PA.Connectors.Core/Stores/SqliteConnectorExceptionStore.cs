using Microsoft.Data.Sqlite;
using static PA.Connectors.Core.Stores.ConnectorDatabase;

namespace PA.Connectors.Core.Stores;

/// <summary>The exceptions list over ConnectorException in connectors.db.</summary>
public sealed class SqliteConnectorExceptionStore : IConnectorExceptionStore
{
    internal const int MaxRefLength = 200;
    internal const int MaxReasonLength = 1000;
    internal const int MaxPosRefLength = 100;

    private const string Columns =
        "ExceptionId, ConnectorId, Kind, ExternalRef, PosRef, Reason, Snapshot, Status, Occurrences, JobId, " +
        "CreatedAt, UpdatedAt, ResolvedBy, ResolvedAt, Resolution";

    internal const int MaxResolutionLength = 1000;

    private readonly ConnectorDatabase _db;

    public SqliteConnectorExceptionStore(ConnectorDatabase db)
    {
        _db = db;
    }

    public Task<long> RaiseAsync(ConnectorExceptionInput exception, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exception.ConnectorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(exception.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(exception.Reason);
        return _db.WriteAsync((connection, transaction) =>
            RaiseAsync(connection, transaction, FormatTime(_db.UtcNow()), exception, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Raise-or-refresh inside the caller's transaction (the job store's
    /// dead-letter uses it so the failed job and its exception commit together).
    /// The open row for (connector, kind, ref) gets the new reason, the snapshot
    /// if one is given, and occurrences + 1.
    /// </summary>
    internal static async Task<long> RaiseAsync(
        SqliteConnection connection, SqliteTransaction transaction, string now, ConnectorExceptionInput exception,
        CancellationToken cancellationToken)
    {
        var externalRef = Truncate(exception.ExternalRef ?? "", MaxRefLength);
        var reason = Truncate(exception.Reason, MaxReasonLength);
        var posRef = exception.PosRef is null ? null : Truncate(exception.PosRef, MaxPosRefLength);

        var existing = await ScalarAsync(connection, transaction,
            "SELECT ExceptionId FROM ConnectorException WHERE ConnectorId = $c AND Kind = $k AND ExternalRef = $ref AND Status = 'open';",
            cancellationToken, ("$c", exception.ConnectorId), ("$k", exception.Kind), ("$ref", externalRef));
        if (existing is long id)
        {
            await ExecAsync(connection, transaction,
                """
                UPDATE ConnectorException
                SET Reason = $reason,
                    Snapshot = COALESCE($snapshot, Snapshot),
                    PosRef = COALESCE($posRef, PosRef),
                    JobId = COALESCE($jobId, JobId),
                    Occurrences = Occurrences + 1,
                    UpdatedAt = $now
                WHERE ExceptionId = $id;
                """, cancellationToken, ("$reason", reason), ("$snapshot", exception.Snapshot), ("$posRef", posRef),
                ("$jobId", exception.JobId), ("$now", now), ("$id", id));
            return id;
        }

        return (long)(await ScalarAsync(connection, transaction,
            """
            INSERT INTO ConnectorException (ConnectorId, Kind, ExternalRef, PosRef, Reason, Snapshot, JobId, CreatedAt, UpdatedAt)
            VALUES ($c, $k, $ref, $posRef, $reason, $snapshot, $jobId, $now, $now);
            SELECT last_insert_rowid();
            """, cancellationToken, ("$c", exception.ConnectorId), ("$k", exception.Kind), ("$ref", externalRef),
            ("$posRef", posRef), ("$reason", reason), ("$snapshot", exception.Snapshot), ("$jobId", exception.JobId),
            ("$now", now)))!;
    }

    public Task<IReadOnlyList<ConnectorExceptionRow>> ListAsync(
        string connectorId, string? status = ConnectorExceptionStatuses.Open, int max = 200,
        CancellationToken cancellationToken = default) =>
        _db.ReadAsync<IReadOnlyList<ConnectorExceptionRow>>(async connection => await QueryAsync(connection, null,
            $"""
            SELECT {Columns} FROM ConnectorException
            WHERE ConnectorId = $c AND ($status IS NULL OR Status = $status)
            ORDER BY CreatedAt DESC, ExceptionId DESC
            LIMIT $max;
            """, Map, cancellationToken, ("$c", connectorId), ("$status", status), ("$max", Math.Clamp(max, 1, 1000))),
            cancellationToken);

    public Task<ConnectorExceptionRow?> GetAsync(long exceptionId, CancellationToken cancellationToken = default) =>
        _db.ReadAsync(async connection => (await QueryAsync(connection, null,
            $"SELECT {Columns} FROM ConnectorException WHERE ExceptionId = $id;", Map, cancellationToken,
            ("$id", exceptionId))).SingleOrDefault(), cancellationToken);

    public Task<bool> CloseAsync(long exceptionId, string status, string resolvedBy, CancellationToken cancellationToken = default) =>
        CloseAsync(exceptionId, status, resolvedBy, null, cancellationToken);

    public Task<bool> CloseAsync(long exceptionId, string status, string resolvedBy, string? resolution,
        CancellationToken cancellationToken = default)
    {
        if (status is not (ConnectorExceptionStatuses.Retried or ConnectorExceptionStatuses.Resolved
            or ConnectorExceptionStatuses.Ignored))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Close as retried, resolved or ignored.");

        return _db.WriteAsync(async (connection, transaction) =>
        {
            var now = FormatTime(_db.UtcNow());
            return await ExecAsync(connection, transaction,
                """
                UPDATE ConnectorException
                SET Status = $status, ResolvedBy = $by, ResolvedAt = $now, UpdatedAt = $now,
                    Resolution = COALESCE($resolution, Resolution)
                WHERE ExceptionId = $id AND Status = 'open';
                """, cancellationToken, ("$status", status), ("$by", Truncate(resolvedBy, 100)), ("$now", now),
                ("$resolution", string.IsNullOrWhiteSpace(resolution) ? null : Truncate(resolution.Trim(), MaxResolutionLength)),
                ("$id", exceptionId)) > 0;
        }, cancellationToken);
    }

    public Task<bool> SetResolutionAsync(long exceptionId, string resolution, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolution);
        return _db.WriteAsync(async (connection, transaction) => await ExecAsync(connection, transaction,
            """
            UPDATE ConnectorException
            SET Resolution = $resolution, UpdatedAt = $now
            WHERE ExceptionId = $id AND Status <> 'open' AND Resolution IS NULL;
            """, cancellationToken, ("$resolution", Truncate(resolution.Trim(), MaxResolutionLength)),
            ("$now", FormatTime(_db.UtcNow())), ("$id", exceptionId)) > 0, cancellationToken);
    }

    public Task<int> CountOpenAsync(string connectorId, CancellationToken cancellationToken = default) =>
        _db.ReadAsync(async connection => Convert.ToInt32(await ScalarAsync(connection, null,
            "SELECT COUNT(*) FROM ConnectorException WHERE ConnectorId = $c AND Status = 'open';",
            cancellationToken, ("$c", connectorId))), cancellationToken);

    private static ConnectorExceptionRow Map(SqliteDataReader r) => new(
        Long(r, "ExceptionId"),
        Str(r, "ConnectorId")!,
        Str(r, "Kind")!,
        Str(r, "ExternalRef") ?? "",
        Str(r, "PosRef"),
        Str(r, "Reason")!,
        Str(r, "Snapshot"),
        Str(r, "Status")!,
        (int)Long(r, "Occurrences"),
        NullableLong(r, "JobId"),
        ReadTime(r, "CreatedAt"),
        ReadTime(r, "UpdatedAt"),
        Str(r, "ResolvedBy"),
        ReadNullableTime(r, "ResolvedAt"),
        Str(r, "Resolution"));
}
