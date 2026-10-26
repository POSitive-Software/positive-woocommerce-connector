using Microsoft.Extensions.Logging;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Pos;
using PA.Connectors.Core.Stores;

namespace PA.Connectors.Core;

/// <summary>
/// A platform adapter bound to one configured store. The engine (worker +
/// queue) owns scheduling, leasing, retries and dead-lettering; the adapter
/// only does the work of one job. It is never called concurrently for the
/// same connector.
/// </summary>
public interface IConnector
{
    ConnectorSettings Settings { get; }

    /// <summary>Jobs the engine enqueues on a cadence (e.g. catalog.push every pushIntervalSeconds).</summary>
    IReadOnlyList<RecurringJob> RecurringJobs { get; }

    /// <summary>
    /// Does one job. Return normally = done. Throw <see cref="ConnectorJobException"/>
    /// with Retryable=false for a permanent failure (straight to the exceptions
    /// list); any other exception is treated as transient and retried with backoff.
    /// Business exceptions the owner must handle (unmatched product, totals
    /// mismatch) are raised by the adapter through the exception store, and the
    /// job then completes — they are not job failures.
    /// </summary>
    Task HandleAsync(ConnectorJob job, CancellationToken cancellationToken);
}

/// <summary>A job kind the engine enqueues every <see cref="Interval"/>.</summary>
public sealed record RecurringJob(string Kind, TimeSpan Interval, string ResourceKey = "");

/// <summary>A job failure with an explicit retry decision.</summary>
public sealed class ConnectorJobException : Exception
{
    public ConnectorJobException(string message, bool retryable, Exception? inner = null)
        : base(message, inner)
    {
        Retryable = retryable;
    }

    public bool Retryable { get; }
}

/// <summary>
/// Everything an adapter may use, scoped to its connector. <see cref="Pos"/> is
/// bound to this connector's own API key, so every call runs in its own tenant.
/// </summary>
public sealed record ConnectorServices(
    ConnectorSettings Settings,
    IPosGateway Pos,
    IConnectorJobStore Jobs,
    IConnectorCursorStore Cursors,
    IConnectorExceptionStore Exceptions,
    IConnectorItemStore Items,
    IConnectorOrderStore Orders,
    ILoggerFactory LoggerFactory);

/// <summary>Builds the adapter for one platform (registered once per platform in DI).</summary>
public interface IConnectorFactory
{
    /// <summary>The <see cref="ConnectorSettings.Platform"/> value this factory serves.</summary>
    string Platform { get; }

    IConnector Create(ConnectorServices services);
}
