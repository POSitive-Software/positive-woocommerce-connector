namespace PA.Connectors.Core.Jobs;

/// <summary>Job kinds (ConnectorJob.Kind in connectors.db). One constant list so the receiver, scheduler and adapters agree.</summary>
public static class ConnectorJobKinds
{
    /// <summary>Import one platform order (ResourceKey = the platform order id). Single consumer per connector.</summary>
    public const string OrderImport = "order.import";

    /// <summary>Backstop poll: list orders modified since the cursor and enqueue order.import for each.</summary>
    public const string OrderPoll = "order.poll";

    /// <summary>Incremental price/stock push from the POSitive change feed (or for explicit invNos in the payload).</summary>
    public const string CatalogPush = "catalog.push";

    /// <summary>Nightly full compare-and-fix of every linked item.</summary>
    public const string CatalogReconcile = "catalog.reconcile";

    /// <summary>Check the platform webhooks exist and are active; re-activate or raise an exception.</summary>
    public const string WebhookHealth = "webhook.health";

    /// <summary>Kinds that must never run concurrently for one connector (design §2: orders import one at a time).</summary>
    public static bool IsSingleConsumer(string kind) => kind == OrderImport;
}

/// <summary>
/// Payload of a <see cref="ConnectorJobKinds.CatalogPush"/> for explicit items, e.g.
/// enqueued by order import right after an order lands (ResourceKey "order:&lt;id&gt;").
/// A catalog.push with no payload means "push from the change feed cursor".
/// </summary>
public sealed record CatalogPushPayload(IReadOnlyList<int> InvNos);

/// <summary>ConnectorJob.Status values.</summary>
public static class ConnectorJobStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>A row of the ConnectorJob table as claimed by the worker.</summary>
public sealed record ConnectorJob(
    long JobId,
    string ConnectorId,
    string Kind,
    string ResourceKey,
    string? Payload,
    string Status,
    int Attempts,
    DateTime NextAttemptAt,
    DateTime? LeaseUntil,
    string? LastError,
    DateTime CreatedAt);

/// <summary>What to enqueue. <paramref name="DueAt"/> null = now (UTC).</summary>
public sealed record JobRequest(
    string ConnectorId,
    string Kind,
    string ResourceKey = "",
    string? Payload = null,
    DateTime? DueAt = null);

/// <summary>Enqueue outcome: <see cref="Coalesced"/> = a pending job for the same (connector, kind, key) already existed and absorbed this one.</summary>
public sealed record EnqueueResult(long JobId, bool Coalesced);

/// <summary>What a failure did to the job.</summary>
public enum JobFailureOutcome
{
    /// <summary>Back to pending, retried after a backoff delay.</summary>
    Rescheduled,

    /// <summary>Out of attempts (or not retryable): job failed and an exception row raised.</summary>
    DeadLettered,

    /// <summary>A newer pending job for the same key exists; it carries the work (and the attempt count) on.</summary>
    Superseded,
}
