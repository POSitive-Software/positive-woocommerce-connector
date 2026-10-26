using System.Reflection;
using System.Text.Json;
using PA.Connectors.Core;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Stores;

namespace PA.Connectors.Host;

/// <summary>
/// GET /health — what the ops console's Connectors card (S5) shows: version,
/// worker state, and per connector the queue depth, open exceptions (things needing action),
/// items waiting in the match review (needsReview — not errors) and the
/// last successful catalog push / order import. Loopback only (the host never
/// listens elsewhere); also at /connectors/health, which PA.Server forwards behind the portal login.
/// </summary>
public static class HealthEndpoint
{
    public static string Version { get; } =
        typeof(HealthEndpoint).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(HealthEndpoint).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static void MapHealthEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", HealthAsync);
        // The same answer under /connectors, where PA.Server's forward (and so the ops console's
        // Connectors card) reaches the host.
        app.MapGet("/connectors/health", HealthAsync);
    }

    internal static async Task<IResult> HealthAsync(
        IConnectorConfigSource config, ConnectorWorker worker, ConnectorDatabase db, ConnectorStores stores,
        SqliteConnectorUiStore ui, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        List<ConnectorSettings> configured;
        string? configError = null;
        try
        {
            configured = config.Load().Connectors;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Viewers of the ops card get a fixed sentence; the path and the reason go to the host log.
            configured = [];
            configError = "The connector settings file could not be read. The details are in the connector host log (pa-connectors-*.log).";
            loggerFactory.CreateLogger(typeof(HealthEndpoint)).LogWarning("Health: {Path} could not be read: {Error}", config.Path, ex.Message);
        }

        var running = worker.RunningConnectorIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var connectors = new List<ConnectorHealth>();
        foreach (var connector in configured.Where(c => !string.IsNullOrWhiteSpace(c.Id)))
        {
            // Exceptions = things needing action; the match review's items are counted apart (not errors).
            var (exceptions, needsReview) = await ui.CountOpenSplitAsync(connector.Id, cancellationToken);
            connectors.Add(new ConnectorHealth(
                connector.Id,
                connector.Platform,
                connector.Enabled,
                running.Contains(connector.Id),
                connector.Enabled ? ConnectorConfigFile.Validate(connector) : [],
                await stores.Jobs.CountOpenAsync(connector.Id, cancellationToken),
                exceptions,
                await stores.Jobs.LastCompletedAtAsync(connector.Id, ConnectorJobKinds.CatalogPush, cancellationToken),
                await stores.Jobs.LastCompletedAtAsync(connector.Id, ConnectorJobKinds.OrderImport, cancellationToken),
                needsReview));
        }

        return Results.Ok(new HostHealth(
            Status: configError is null ? "ok" : "degraded",
            Service: ConnectorHostApp.ServiceName,
            Version: Version,
            Utc: DateTime.UtcNow,
            Worker: worker.State,
            SchemaVersion: await db.GetVersionAsync(cancellationToken),
            ConfigError: configError,
            QueueDepth: connectors.Sum(c => c.QueueDepth),
            OpenExceptions: connectors.Sum(c => c.OpenExceptions),
            NeedsReview: connectors.Sum(c => c.NeedsReview),
            LastPushAt: connectors.Max(c => c.LastPushAt),
            LastImportAt: connectors.Max(c => c.LastImportAt),
            Connectors: connectors));
    }

    public sealed record HostHealth(
        string Status,
        string Service,
        string Version,
        DateTime Utc,
        string Worker,
        int SchemaVersion,
        string? ConfigError,
        int QueueDepth,
        int OpenExceptions,
        int NeedsReview,
        DateTime? LastPushAt,
        DateTime? LastImportAt,
        IReadOnlyList<ConnectorHealth> Connectors);

    public sealed record ConnectorHealth(
        string Id,
        string Platform,
        bool Enabled,
        bool Running,
        IReadOnlyList<string> Problems,
        int QueueDepth,
        int OpenExceptions,
        DateTime? LastPushAt,
        DateTime? LastImportAt,
        int NeedsReview);
}
