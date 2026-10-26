using PA.Connectors.Core.Jobs;

namespace PA.Connectors.Core.Stores;

/// <summary>
/// Durable job queue (ConnectorJob in connectors.db). Enqueue coalesces: at most
/// one PENDING job per (connector, kind, resourceKey). Claim leases one due job;
/// order.import jobs are single-consumer per connector.
/// </summary>
public interface IConnectorJobStore
{
    Task<EnqueueResult> EnqueueAsync(JobRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Leases the next due job for <paramref name="connectorId"/> (pending and due,
    /// or running with an expired lease), increments its attempts and marks it
    /// running until now + <paramref name="lease"/>. Null when nothing is due.
    /// An order.import job is not handed out while another order.import job of
    /// the same connector holds a live lease.
    /// </summary>
    Task<ConnectorJob?> ClaimNextAsync(string connectorId, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Marks a claimed job done.</summary>
    Task CompleteAsync(long jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed attempt. Applies <paramref name="policy"/>: reschedule with
    /// backoff, or dead-letter (status failed + a <c>job_failed</c> exception row
    /// in the same transaction). If a newer pending job for the same key exists,
    /// the failed one is closed as superseded and the newer one carries on.
    /// </summary>
    Task<JobFailureOutcome> FailAsync(
        ConnectorJob job, string error, bool retryable, JobRetryPolicy policy,
        CancellationToken cancellationToken = default);

    /// <summary>Pending + running jobs (queue depth for the ops card).</summary>
    Task<int> CountOpenAsync(string connectorId, CancellationToken cancellationToken = default);

    /// <summary>Deletes done jobs completed before <paramref name="olderThanUtc"/>. Failed jobs are kept (their exception references them).</summary>
    Task<int> PurgeDoneAsync(DateTime olderThanUtc, CancellationToken cancellationToken = default);

    /// <summary>When a job of <paramref name="kind"/> last completed successfully for the connector (UTC), or null — for /health.</summary>
    Task<DateTime?> LastCompletedAtAsync(string connectorId, string kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resource keys of <paramref name="kind"/> whose LATEST job was dead-lettered after running out of
    /// attempts on transient faults (not a permanent failure) — work that would otherwise be stranded.
    /// The order poll re-queues them. Oldest first, at most <paramref name="max"/>.
    /// </summary>
    Task<IReadOnlyList<string>> ListStrandedAsync(string connectorId, string kind, int max = 100,
        CancellationToken cancellationToken = default);
}

/// <summary>Named watermarks per connector (ConnectorCursor).</summary>
public interface IConnectorCursorStore
{
    Task<string?> GetAsync(string connectorId, string cursorName, CancellationToken cancellationToken = default);

    Task SetAsync(string connectorId, string cursorName, string? value, CancellationToken cancellationToken = default);
}

/// <summary>Cursor names (ConnectorCursor.CursorName).</summary>
public static class ConnectorCursorNames
{
    /// <summary>POSitive change feed: last <c>modifiedSince</c> the catalog push consumed (UTC, ISO 8601).</summary>
    public const string PosModifiedSince = "pos.modifiedSince";

    /// <summary>Platform order backstop poll: last <c>modified_after</c> consumed (UTC, ISO 8601).</summary>
    public const string OrdersModifiedAfter = "orders.modifiedAfter";

    /// <summary>When each recurring job kind was last enqueued (suffix = kind).</summary>
    public static string LastScheduled(string kind) => "schedule." + kind;
}

/// <summary>Exception kinds (ConnectorException.Kind).</summary>
public static class ConnectorExceptionKinds
{
    /// <summary>A job ran out of attempts or failed permanently.</summary>
    public const string JobFailed = "job_failed";
    public const string UnmatchedProduct = "unmatched_product";
    public const string TotalsMismatch = "totals_mismatch";
    public const string OrderCancelled = "order_cancelled";
    public const string OrderRefunded = "order_refunded";
    public const string PushFailed = "push_failed";
    public const string WebhookUnhealthy = "webhook_unhealthy";
}

public static class ConnectorExceptionStatuses
{
    public const string Open = "open";
    public const string Retried = "retried";
    public const string Resolved = "resolved";
    public const string Ignored = "ignored";
}

/// <summary>An exception to raise. <paramref name="ExternalRef"/> = the platform reference (order id, product id); the open row for (connector, kind, ref) is reused.</summary>
public sealed record ConnectorExceptionInput(
    string ConnectorId,
    string Kind,
    string ExternalRef,
    string Reason,
    string? PosRef = null,
    string? Snapshot = null,
    long? JobId = null);

public sealed record ConnectorExceptionRow(
    long ExceptionId,
    string ConnectorId,
    string Kind,
    string ExternalRef,
    string? PosRef,
    string Reason,
    string? Snapshot,
    string Status,
    int Occurrences,
    long? JobId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? ResolvedBy,
    DateTime? ResolvedAt,
    string? Resolution = null);

/// <summary>The exceptions list (ConnectorException).</summary>
public interface IConnectorExceptionStore
{
    /// <summary>Opens an exception, or refreshes the open one for the same (connector, kind, ref): reason/snapshot replaced, occurrences + 1.</summary>
    Task<long> RaiseAsync(ConnectorExceptionInput exception, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConnectorExceptionRow>> ListAsync(
        string connectorId, string? status = ConnectorExceptionStatuses.Open, int max = 200,
        CancellationToken cancellationToken = default);

    Task<ConnectorExceptionRow?> GetAsync(long exceptionId, CancellationToken cancellationToken = default);

    /// <summary>Closes an open exception as retried / resolved / ignored. False when it was not open.</summary>
    Task<bool> CloseAsync(long exceptionId, string status, string resolvedBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes an open exception and records what fixed it (<paramref name="resolution"/>, plain text the
    /// exceptions page leads with: "Imported as POSitive order 50001919 on retry; …"). False when it was not open.
    /// </summary>
    Task<bool> CloseAsync(long exceptionId, string status, string resolvedBy, string? resolution,
        CancellationToken cancellationToken = default) => CloseAsync(exceptionId, status, resolvedBy, cancellationToken);

    /// <summary>
    /// Records the outcome on an exception already closed without one (e.g. closed as retried when the
    /// owner clicked Retry, then imported by that retry). False when it is open or already has one.
    /// </summary>
    Task<bool> SetResolutionAsync(long exceptionId, string resolution, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<int> CountOpenAsync(string connectorId, CancellationToken cancellationToken = default);
}

/// <summary>A linked item: POSitive invNo ⇄ platform id (+ parent id for variations).</summary>
public sealed record ConnectorItem(
    string ConnectorId,
    int InvNo,
    long ExternalId,
    long ExternalParentId,
    string? ExternalType,
    string? Sku,
    string? LastPushHash,
    DateTime? LastPushedAt);

/// <summary>Item map (ConnectorItem).</summary>
public interface IConnectorItemStore
{
    Task<ConnectorItem?> GetByInvNoAsync(string connectorId, int invNo, CancellationToken cancellationToken = default);

    Task<ConnectorItem?> GetByExternalIdAsync(string connectorId, long externalId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConnectorItem>> ListAsync(string connectorId, CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces the link for (connector, invNo). The push hash is kept when the external id is unchanged.</summary>
    Task UpsertAsync(ConnectorItem item, CancellationToken cancellationToken = default);

    Task SetPushHashAsync(string connectorId, int invNo, string pushHash, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(string connectorId, int invNo, CancellationToken cancellationToken = default);
}

public static class ConnectorOrderStatuses
{
    public const string Pending = "pending";
    public const string Imported = "imported";
    public const string Exception = "exception";
    public const string Ignored = "ignored";
}

/// <summary>
/// An imported (or attempted) platform order. <see cref="ImportedTotal"/> / <see cref="ImportedTax"/> =
/// the platform's grand total (incl. tax) and tax as imported; null on rows imported before schema 2.
/// </summary>
public sealed record ConnectorOrder(
    string ConnectorId,
    long ExternalOrderId,
    string? OrderNumber,
    int? InvoiceNo,
    string? OrdGuid,
    string ImportStatus,
    bool PaymentPosted,
    DateTime? PaymentPostedAt,
    string? ExternalStatus,
    DateTime? ExternalModifiedAt,
    string? LastError,
    DateTime? ImportedAt,
    decimal? ImportedTotal = null,
    decimal? ImportedTax = null);

/// <summary>One item of a platform order that holds stock on the platform but is not imported (ConnectorStockHold).</summary>
public sealed record ConnectorStockHoldLine(int InvNo, decimal Quantity);

/// <summary>Order map (ConnectorOrder).</summary>
public interface IConnectorOrderStore
{
    Task<ConnectorOrder?> GetAsync(string connectorId, long externalOrderId, CancellationToken cancellationToken = default);

    /// <summary>Inserts or updates the row (all columns except PaymentPosted/PaymentPostedAt, which only <see cref="TryMarkPaymentPostedAsync"/> sets).</summary>
    Task UpsertAsync(ConnectorOrder order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flips PaymentPosted 0 → 1. True only for the caller that flipped it, so
    /// a retry that finds it already set never tenders again (gap G2).
    /// </summary>
    Task<bool> TryMarkPaymentPostedAsync(string connectorId, long externalOrderId, CancellationToken cancellationToken = default);

    // --- stock holds: platform orders that hold platform stock but are not in POSitive ---

    /// <summary>
    /// Replaces the stock hold of one platform order with <paramref name="lines"/> (summed per invNo;
    /// empty = clear it). <paramref name="unresolvedLines"/> counts order lines no item could be found
    /// for (they cannot be subtracted). Returns the invNos whose held quantity changed, so the caller
    /// re-pushes exactly those.
    /// </summary>
    Task<IReadOnlyList<int>> SetStockHoldAsync(string connectorId, long externalOrderId, string? externalStatus,
        IReadOnlyList<ConnectorStockHoldLine> lines, int unresolvedLines, CancellationToken cancellationToken = default);

    /// <summary>The held quantity per invNo over every held order of the connector (the push subtracts it).</summary>
    Task<IReadOnlyDictionary<int, decimal>> GetStockHoldTotalsAsync(string connectorId, CancellationToken cancellationToken = default);

    /// <summary>The held orders last checked before <paramref name="checkedBeforeUtc"/>, oldest check first (the poll re-verifies them).</summary>
    Task<IReadOnlyList<long>> ListStockHoldOrdersAsync(string connectorId, DateTime checkedBeforeUtc, int max,
        CancellationToken cancellationToken = default);
}
