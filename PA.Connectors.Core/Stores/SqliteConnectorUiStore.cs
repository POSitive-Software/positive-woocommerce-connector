using Microsoft.Data.Sqlite;
using PA.Connectors.Core.Catalog;
using static PA.Connectors.Core.Stores.ConnectorDatabase;

namespace PA.Connectors.Core.Stores;

/// <summary>An item the owner chose to leave alone in the match review (ConnectorIgnore).</summary>
public sealed record ConnectorIgnoreRow(
    string ConnectorId, string ExternalRef, string Kind, string? Note, string IgnoredBy, DateTime IgnoredAt);

/// <summary>A queue row as the connector UI shows it (status page, "is the match still running?").</summary>
public sealed record ConnectorJobView(
    long JobId,
    string ConnectorId,
    string Kind,
    string ResourceKey,
    string? Payload,
    string Status,
    int Attempts,
    DateTime NextAttemptAt,
    string? LastError,
    bool FailedPermanently,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt);

/// <summary>
/// The S5 connector UI's reads over connectors.db (job rows, counts) and the match review's
/// ignore marks. Kept apart from the engine stores so the worker's contracts stay as they are.
/// </summary>
public sealed class SqliteConnectorUiStore
{
    /// <summary>
    /// The review exceptions an ignore mark suppresses (the match review's kinds,
    /// <see cref="CatalogExceptionKinds.Review"/>); the others always raise.
    /// </summary>
    public static readonly IReadOnlySet<string> IgnorableKinds = CatalogExceptionKinds.Review;

    private const string JobColumns =
        "JobId, ConnectorId, Kind, ResourceKey, Payload, Status, Attempts, NextAttemptAt, LastError, FailedPermanently, " +
        "CreatedAt, UpdatedAt, CompletedAt";

    private readonly ConnectorDatabase _db;

    public SqliteConnectorUiStore(ConnectorDatabase db)
    {
        _db = db;
    }

    // --- ignore marks --------------------------------------------------------

    /// <summary>Marks <paramref name="externalRef"/> ignored (replacing an earlier mark).</summary>
    public Task IgnoreAsync(string connectorId, string externalRef, string kind, string ignoredBy, string? note = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalRef);
        return _db.WriteAsync(async (connection, transaction) => await ExecAsync(connection, transaction,
            """
            INSERT INTO ConnectorIgnore (ConnectorId, ExternalRef, Kind, Note, IgnoredBy, IgnoredAt)
            VALUES ($c, $ref, $kind, $note, $by, $now)
            ON CONFLICT (ConnectorId, ExternalRef) DO UPDATE
            SET Kind = excluded.Kind, Note = excluded.Note, IgnoredBy = excluded.IgnoredBy, IgnoredAt = excluded.IgnoredAt;
            """, cancellationToken, ("$c", connectorId), ("$ref", Truncate(externalRef, SqliteConnectorExceptionStore.MaxRefLength)),
            ("$kind", kind), ("$note", note is null ? null : Truncate(note, 500)), ("$by", Truncate(ignoredBy, 100)),
            ("$now", FormatTime(_db.UtcNow()))), cancellationToken);
    }

    public Task<bool> UnignoreAsync(string connectorId, string externalRef, CancellationToken cancellationToken = default) =>
        _db.WriteAsync(async (connection, transaction) => await ExecAsync(connection, transaction,
            "DELETE FROM ConnectorIgnore WHERE ConnectorId = $c AND ExternalRef = $ref;", cancellationToken,
            ("$c", connectorId), ("$ref", externalRef)) > 0, cancellationToken);

    public Task<bool> IsIgnoredAsync(string connectorId, string externalRef, CancellationToken cancellationToken = default) =>
        _db.ReadAsync(async connection => await ScalarAsync(connection, null,
            "SELECT 1 FROM ConnectorIgnore WHERE ConnectorId = $c AND ExternalRef = $ref;", cancellationToken,
            ("$c", connectorId), ("$ref", Truncate(externalRef ?? "", SqliteConnectorExceptionStore.MaxRefLength))) is not null,
            cancellationToken);

    public Task<IReadOnlyList<ConnectorIgnoreRow>> ListIgnoredAsync(string connectorId, CancellationToken cancellationToken = default) =>
        _db.ReadAsync<IReadOnlyList<ConnectorIgnoreRow>>(async connection => await QueryAsync(connection, null,
            "SELECT ConnectorId, ExternalRef, Kind, Note, IgnoredBy, IgnoredAt FROM ConnectorIgnore WHERE ConnectorId = $c ORDER BY IgnoredAt DESC;",
            r => new ConnectorIgnoreRow(Str(r, "ConnectorId")!, Str(r, "ExternalRef")!, Str(r, "Kind")!, Str(r, "Note"),
                Str(r, "IgnoredBy")!, ReadTime(r, "IgnoredAt")),
            cancellationToken, ("$c", connectorId)), cancellationToken);

    // --- jobs ------------------------------------------------------------------

    public Task<ConnectorJobView?> GetJobAsync(long jobId, CancellationToken cancellationToken = default) =>
        _db.ReadAsync(async connection => (await QueryAsync(connection, null,
            $"SELECT {JobColumns} FROM ConnectorJob WHERE JobId = $id;", MapJob, cancellationToken, ("$id", jobId)))
            .SingleOrDefault(), cancellationToken);

    /// <summary>Newest first; <paramref name="kinds"/> null = every kind.</summary>
    public Task<IReadOnlyList<ConnectorJobView>> ListJobsAsync(string connectorId, IReadOnlyCollection<string>? kinds = null,
        int max = 20, CancellationToken cancellationToken = default)
    {
        var kindList = kinds is { Count: > 0 } ? string.Join(",", kinds.Select((_, i) => "$k" + i)) : null;
        var args = new List<(string, object?)> { ("$c", connectorId), ("$max", Math.Clamp(max, 1, 500)) };
        if (kinds is { Count: > 0 })
            args.AddRange(kinds.Select((k, i) => ("$k" + i, (object?)k)));
        return _db.ReadAsync<IReadOnlyList<ConnectorJobView>>(async connection => await QueryAsync(connection, null,
            $"""
            SELECT {JobColumns} FROM ConnectorJob
            WHERE ConnectorId = $c {(kindList is null ? "" : $"AND Kind IN ({kindList})")}
            ORDER BY JobId DESC
            LIMIT $max;
            """, MapJob, cancellationToken, [.. args]), cancellationToken);
    }

    // --- counts ------------------------------------------------------------------

    /// <summary>
    /// Exceptions per status (open / retried / resolved / ignored) for the connector. With
    /// <paramref name="exceptionsOnly"/> the match review's kinds (<see cref="CatalogExceptionKinds.Review"/>)
    /// are left out: they are review items, not exceptions.
    /// </summary>
    public Task<IReadOnlyDictionary<string, int>> CountExceptionsByStatusAsync(string connectorId,
        CancellationToken cancellationToken = default) => CountExceptionsByStatusAsync(connectorId, false, cancellationToken);

    public Task<IReadOnlyDictionary<string, int>> CountExceptionsByStatusAsync(string connectorId, bool exceptionsOnly,
        CancellationToken cancellationToken = default) =>
        _db.ReadAsync<IReadOnlyDictionary<string, int>>(async connection =>
        {
            var (filter, args) = exceptionsOnly ? NotReview() : ("", new List<(string, object?)>());
            args.Add(("$c", connectorId));
            var rows = await QueryAsync(connection, null,
                $"SELECT Status, COUNT(*) AS N FROM ConnectorException WHERE ConnectorId = $c {filter} GROUP BY Status;",
                r => (Status: Str(r, "Status")!, Count: (int)Long(r, "N")), cancellationToken, [.. args]);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [ConnectorExceptionStatuses.Open] = 0,
                [ConnectorExceptionStatuses.Retried] = 0,
                [ConnectorExceptionStatuses.Resolved] = 0,
                [ConnectorExceptionStatuses.Ignored] = 0,
            };
            foreach (var (status, count) in rows)
                counts[status] = count;
            return counts;
        }, cancellationToken);

    /// <summary>
    /// Open rows split the way the pages show them: <c>Exceptions</c> = things needing action,
    /// <c>NeedsReview</c> = the match review's items (<see cref="CatalogExceptionKinds.Review"/>).
    /// </summary>
    public Task<(int Exceptions, int NeedsReview)> CountOpenSplitAsync(string connectorId, CancellationToken cancellationToken = default) =>
        _db.ReadAsync(async connection =>
        {
            var rows = await QueryAsync(connection, null,
                "SELECT Kind, COUNT(*) AS N FROM ConnectorException WHERE ConnectorId = $c AND Status = 'open' GROUP BY Kind;",
                r => (Kind: Str(r, "Kind")!, Count: (int)Long(r, "N")), cancellationToken, ("$c", connectorId));
            var review = rows.Where(r => CatalogExceptionKinds.IsReview(r.Kind)).Sum(r => r.Count);
            return (rows.Sum(r => r.Count) - review, review);
        }, cancellationToken);

    /// <summary>SQL that leaves the review kinds out (AND Kind NOT IN (…)) and its parameters.</summary>
    private static (string Sql, List<(string, object?)> Args) NotReview()
    {
        var kinds = CatalogExceptionKinds.Review.OrderBy(k => k, StringComparer.Ordinal).ToList();
        return ($"AND Kind NOT IN ({string.Join(",", kinds.Select((_, i) => "$r" + i))})",
            kinds.Select((k, i) => ("$r" + i, (object?)k)).ToList());
    }

    /// <summary>Platform orders holding stock that POSitive does not have yet (ConnectorStockHold), and the items they hold.</summary>
    public Task<(int Orders, int Items)> CountStockHoldsAsync(string connectorId, CancellationToken cancellationToken = default) =>
        _db.ReadAsync(async connection =>
        {
            var orders = Convert.ToInt32(await ScalarAsync(connection, null,
                "SELECT COUNT(DISTINCT ExternalOrderId) FROM ConnectorStockHold WHERE ConnectorId = $c;", cancellationToken,
                ("$c", connectorId)));
            var items = Convert.ToInt32(await ScalarAsync(connection, null,
                "SELECT COUNT(DISTINCT InvNo) FROM ConnectorStockHold WHERE ConnectorId = $c AND InvNo > 0;", cancellationToken,
                ("$c", connectorId)));
            return (orders, items);
        }, cancellationToken);

    /// <summary>Linked items (the item map, not counting ones whose platform product went missing).</summary>
    public Task<int> CountLinkedItemsAsync(string connectorId, CancellationToken cancellationToken = default) =>
        _db.ReadAsync(async connection => Convert.ToInt32(await ScalarAsync(connection, null,
            "SELECT COUNT(*) FROM ConnectorItem WHERE ConnectorId = $c AND COALESCE(ExternalType, '') <> $missing;",
            cancellationToken, ("$c", connectorId), ("$missing", CatalogItemTypes.Missing))), cancellationToken);

    private static ConnectorJobView MapJob(SqliteDataReader r) => new(
        Long(r, "JobId"),
        Str(r, "ConnectorId")!,
        Str(r, "Kind")!,
        Str(r, "ResourceKey") ?? "",
        Str(r, "Payload"),
        Str(r, "Status")!,
        (int)Long(r, "Attempts"),
        ReadTime(r, "NextAttemptAt"),
        Str(r, "LastError"),
        Long(r, "FailedPermanently") != 0,
        ReadTime(r, "CreatedAt"),
        ReadTime(r, "UpdatedAt"),
        ReadNullableTime(r, "CompletedAt"));
}

/// <summary>
/// The exception store the connectors write through, with the match review's ignore marks
/// applied: a review exception (<see cref="SqliteConnectorUiStore.IgnorableKinds"/>) for an
/// ignored item is not raised (returns 0). Everything else passes straight through.
/// </summary>
public sealed class IgnoreAwareExceptionStore : IConnectorExceptionStore
{
    private readonly IConnectorExceptionStore _inner;
    private readonly SqliteConnectorUiStore _ui;

    public IgnoreAwareExceptionStore(IConnectorExceptionStore inner, SqliteConnectorUiStore ui)
    {
        _inner = inner;
        _ui = ui;
    }

    public async Task<long> RaiseAsync(ConnectorExceptionInput exception, CancellationToken cancellationToken = default)
    {
        if (SqliteConnectorUiStore.IgnorableKinds.Contains(exception.Kind)
            && await _ui.IsIgnoredAsync(exception.ConnectorId, exception.ExternalRef, cancellationToken))
            return 0;
        return await _inner.RaiseAsync(exception, cancellationToken);
    }

    public Task<IReadOnlyList<ConnectorExceptionRow>> ListAsync(string connectorId, string? status = ConnectorExceptionStatuses.Open,
        int max = 200, CancellationToken cancellationToken = default) => _inner.ListAsync(connectorId, status, max, cancellationToken);

    public Task<ConnectorExceptionRow?> GetAsync(long exceptionId, CancellationToken cancellationToken = default) =>
        _inner.GetAsync(exceptionId, cancellationToken);

    public Task<bool> CloseAsync(long exceptionId, string status, string resolvedBy, CancellationToken cancellationToken = default) =>
        _inner.CloseAsync(exceptionId, status, resolvedBy, cancellationToken);

    public Task<bool> CloseAsync(long exceptionId, string status, string resolvedBy, string? resolution,
        CancellationToken cancellationToken = default) =>
        _inner.CloseAsync(exceptionId, status, resolvedBy, resolution, cancellationToken);

    public Task<bool> SetResolutionAsync(long exceptionId, string resolution, CancellationToken cancellationToken = default) =>
        _inner.SetResolutionAsync(exceptionId, resolution, cancellationToken);

    public Task<int> CountOpenAsync(string connectorId, CancellationToken cancellationToken = default) =>
        _inner.CountOpenAsync(connectorId, cancellationToken);
}
