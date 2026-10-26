using System.Text.Json;
using Microsoft.Data.Sqlite;
using PA.Connectors.Core.Jobs;
using static PA.Connectors.Core.Stores.ConnectorDatabase;

namespace PA.Connectors.Core.Stores;

/// <summary>
/// The job queue over ConnectorJob in connectors.db. Every write is one
/// IMMEDIATE transaction under the database's in-process write gate, which is
/// what makes "is another order.import of this connector running?" and the
/// claim atomic (the single-consumer rule); the lease covers a crash mid-job.
/// </summary>
public sealed class SqliteConnectorJobStore : IConnectorJobStore
{
    internal const int MaxErrorLength = 2000;

    private const string Columns =
        "JobId, ConnectorId, Kind, ResourceKey, Payload, Status, Attempts, NextAttemptAt, LeaseUntil, LastError, CreatedAt";

    private readonly ConnectorDatabase _db;

    public SqliteConnectorJobStore(ConnectorDatabase db)
    {
        _db = db;
    }

    public Task<EnqueueResult> EnqueueAsync(JobRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConnectorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Kind);
        var resourceKey = request.ResourceKey ?? "";

        return _db.WriteAsync(async (connection, transaction) =>
        {
            var now = FormatTime(_db.UtcNow());
            var due = request.DueAt is { } dueAt ? FormatTime(dueAt) : now;

            var existing = await ScalarAsync(connection, transaction,
                """
                SELECT JobId FROM ConnectorJob
                WHERE ConnectorId = $c AND Kind = $k AND ResourceKey = $r AND Status = 'pending';
                """, cancellationToken, ("$c", request.ConnectorId), ("$k", request.Kind), ("$r", resourceKey));
            if (existing is long id)
            {
                // Coalesce: the newer payload wins, and the earlier due time.
                await ExecAsync(connection, transaction,
                    """
                    UPDATE ConnectorJob
                    SET Payload = COALESCE($p, Payload),
                        NextAttemptAt = CASE WHEN $due < NextAttemptAt THEN $due ELSE NextAttemptAt END,
                        UpdatedAt = $now
                    WHERE JobId = $id;
                    """, cancellationToken, ("$p", request.Payload), ("$due", due), ("$now", now), ("$id", id));
                return new EnqueueResult(id, true);
            }

            var newId = (long)(await ScalarAsync(connection, transaction,
                """
                INSERT INTO ConnectorJob (ConnectorId, Kind, ResourceKey, Payload, Status, Attempts, NextAttemptAt, CreatedAt, UpdatedAt)
                VALUES ($c, $k, $r, $p, 'pending', 0, $due, $now, $now);
                SELECT last_insert_rowid();
                """, cancellationToken, ("$c", request.ConnectorId), ("$k", request.Kind), ("$r", resourceKey),
                ("$p", request.Payload), ("$due", due), ("$now", now)))!;
            return new EnqueueResult(newId, false);
        }, cancellationToken);
    }

    public Task<ConnectorJob?> ClaimNextAsync(string connectorId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var leaseLength = lease < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : lease;
        return _db.WriteAsync<ConnectorJob?>(async (connection, transaction) =>
        {
            var nowTime = _db.UtcNow();
            var now = FormatTime(nowTime);

            // An order.import of this connector holding a LIVE lease blocks every
            // other order.import (single consumer); an expired lease does not —
            // that job is itself reclaimable below.
            var id = await ScalarAsync(connection, transaction,
                """
                SELECT JobId FROM ConnectorJob
                WHERE ConnectorId = $c
                  AND ((Status = 'pending' AND NextAttemptAt <= $now)
                       OR (Status = 'running' AND LeaseUntil < $now))
                  AND NOT (Kind = $single AND EXISTS (
                        SELECT 1 FROM ConnectorJob busy
                        WHERE busy.ConnectorId = $c AND busy.Kind = $single
                          AND busy.Status = 'running' AND busy.LeaseUntil >= $now))
                ORDER BY NextAttemptAt, JobId
                LIMIT 1;
                """, cancellationToken, ("$c", connectorId), ("$now", now), ("$single", ConnectorJobKinds.OrderImport));
            if (id is not long jobId)
                return null;

            await ExecAsync(connection, transaction,
                """
                UPDATE ConnectorJob
                SET Status = 'running', Attempts = Attempts + 1, LeaseUntil = $lease, UpdatedAt = $now
                WHERE JobId = $id;
                """, cancellationToken, ("$lease", FormatTime(nowTime + leaseLength)), ("$now", now), ("$id", jobId));

            return (await QueryAsync(connection, transaction, $"SELECT {Columns} FROM ConnectorJob WHERE JobId = $id;",
                Map, cancellationToken, ("$id", jobId))).Single();
        }, cancellationToken);
    }

    public Task CompleteAsync(long jobId, CancellationToken cancellationToken = default) =>
        _db.WriteAsync(async (connection, transaction) =>
        {
            var now = FormatTime(_db.UtcNow());
            return await ExecAsync(connection, transaction,
                """
                UPDATE ConnectorJob
                SET Status = 'done', LeaseUntil = NULL, CompletedAt = $now, UpdatedAt = $now
                WHERE JobId = $id AND Status = 'running';
                """, cancellationToken, ("$now", now), ("$id", jobId));
        }, cancellationToken);

    public Task<JobFailureOutcome> FailAsync(
        ConnectorJob job, string error, bool retryable, JobRetryPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var decision = policy.Decide(job.Attempts, retryable);
        var lastError = Truncate(error, MaxErrorLength);

        // One transaction: the job's new state and (when giving up) its
        // exception row land together or not at all.
        return _db.WriteAsync(async (connection, transaction) =>
        {
            var nowTime = _db.UtcNow();
            var now = FormatTime(nowTime);
            var retryAt = FormatTime(nowTime + decision.Delay);

            var sibling = await ScalarAsync(connection, transaction,
                """
                SELECT JobId FROM ConnectorJob
                WHERE ConnectorId = $c AND Kind = $k AND ResourceKey = $r AND Status = 'pending' AND JobId <> $id;
                """, cancellationToken, ("$c", job.ConnectorId), ("$k", job.Kind), ("$r", job.ResourceKey), ("$id", job.JobId));

            if (decision.Retry && sibling is long siblingId)
            {
                // A newer delivery is already waiting: it does the work. Carry the
                // attempt count over so a poison key still runs out of attempts.
                await ExecAsync(connection, transaction,
                    """
                    UPDATE ConnectorJob
                    SET Attempts = MAX(Attempts, $attempts),
                        NextAttemptAt = MAX(NextAttemptAt, $retryAt),
                        UpdatedAt = $now
                    WHERE JobId = $sibling;
                    UPDATE ConnectorJob
                    SET Status = 'done', LeaseUntil = NULL, LastError = $err, CompletedAt = $now, UpdatedAt = $now
                    WHERE JobId = $id;
                    """, cancellationToken, ("$attempts", job.Attempts), ("$retryAt", retryAt), ("$now", now),
                    ("$sibling", siblingId), ("$err", lastError), ("$id", job.JobId));
                return JobFailureOutcome.Superseded;
            }

            if (decision.Retry)
            {
                await ExecAsync(connection, transaction,
                    """
                    UPDATE ConnectorJob
                    SET Status = 'pending', LeaseUntil = NULL, LastError = $err, NextAttemptAt = $retryAt, UpdatedAt = $now
                    WHERE JobId = $id;
                    """, cancellationToken, ("$err", lastError), ("$retryAt", retryAt), ("$now", now), ("$id", job.JobId));
                return JobFailureOutcome.Rescheduled;
            }

            await ExecAsync(connection, transaction,
                """
                UPDATE ConnectorJob
                SET Status = 'failed', LeaseUntil = NULL, LastError = $err, CompletedAt = $now, UpdatedAt = $now,
                    FailedPermanently = $permanent
                WHERE JobId = $id;
                """, cancellationToken, ("$err", lastError), ("$now", now), ("$permanent", retryable ? 0 : 1), ("$id", job.JobId));

            var snapshot = JsonSerializer.Serialize(new
            {
                jobId = job.JobId,
                kind = job.Kind,
                resourceKey = job.ResourceKey,
                attempts = job.Attempts,
                payload = job.Payload,
                error = lastError,
            });
            await SqliteConnectorExceptionStore.RaiseAsync(connection, transaction, now, new ConnectorExceptionInput(
                job.ConnectorId,
                ConnectorExceptionKinds.JobFailed,
                $"{job.Kind}:{job.ResourceKey}",
                $"{job.Kind} {job.ResourceKey} failed after {job.Attempts} attempt(s): {lastError}",
                PosRef: null,
                Snapshot: snapshot,
                JobId: job.JobId), cancellationToken);
            return JobFailureOutcome.DeadLettered;
        }, cancellationToken);
    }

    public Task<int> CountOpenAsync(string connectorId, CancellationToken cancellationToken = default) =>
        _db.ReadAsync(async connection => Convert.ToInt32(await ScalarAsync(connection, null,
            "SELECT COUNT(*) FROM ConnectorJob WHERE ConnectorId = $c AND Status IN ('pending', 'running');",
            cancellationToken, ("$c", connectorId))), cancellationToken);

    public Task<int> PurgeDoneAsync(DateTime olderThanUtc, CancellationToken cancellationToken = default) =>
        _db.WriteAsync((connection, transaction) => ExecAsync(connection, transaction,
            "DELETE FROM ConnectorJob WHERE Status = 'done' AND CompletedAt < $before;",
            cancellationToken, ("$before", FormatTime(olderThanUtc))), cancellationToken);

    public Task<DateTime?> LastCompletedAtAsync(string connectorId, string kind, CancellationToken cancellationToken = default) =>
        _db.ReadAsync(async connection =>
        {
            var value = await ScalarAsync(connection, null,
                "SELECT MAX(CompletedAt) FROM ConnectorJob WHERE ConnectorId = $c AND Kind = $k AND Status = 'done';",
                cancellationToken, ("$c", connectorId), ("$k", kind));
            return value is string text ? ParseTime(text) : (DateTime?)null;
        }, cancellationToken);

    public Task<IReadOnlyList<string>> ListStrandedAsync(string connectorId, string kind, int max = 100,
        CancellationToken cancellationToken = default) =>
        _db.ReadAsync<IReadOnlyList<string>>(async connection => await QueryAsync(connection, null,
            """
            SELECT j.ResourceKey FROM ConnectorJob j
            WHERE j.ConnectorId = $c AND j.Kind = $k AND j.Status = 'failed' AND j.FailedPermanently = 0
              AND j.JobId = (SELECT MAX(x.JobId) FROM ConnectorJob x
                             WHERE x.ConnectorId = j.ConnectorId AND x.Kind = j.Kind AND x.ResourceKey = j.ResourceKey)
            ORDER BY j.JobId
            LIMIT $max;
            """, r => Str(r, "ResourceKey") ?? "", cancellationToken,
            ("$c", connectorId), ("$k", kind), ("$max", Math.Max(1, max))), cancellationToken);

    private static ConnectorJob Map(SqliteDataReader r) => new(
        Long(r, "JobId"),
        Str(r, "ConnectorId")!,
        Str(r, "Kind")!,
        Str(r, "ResourceKey") ?? "",
        Str(r, "Payload"),
        Str(r, "Status")!,
        (int)Long(r, "Attempts"),
        ReadTime(r, "NextAttemptAt"),
        ReadNullableTime(r, "LeaseUntil"),
        Str(r, "LastError"),
        ReadTime(r, "CreatedAt"));
}
