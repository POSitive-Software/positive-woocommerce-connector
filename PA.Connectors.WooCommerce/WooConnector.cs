using Microsoft.Extensions.Logging;
using PA.Connectors.Core;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Secrets;
using PA.Connectors.WooCommerce.Catalog;
using PA.Connectors.WooCommerce.Orders;

namespace PA.Connectors.WooCommerce;

/// <summary>
/// The WooCommerce adapter for one store. It only routes: catalog jobs go to
/// <see cref="WooCatalogSync"/> (S2) and order jobs to <see cref="WooOrderImport"/> (S3).
/// A kind neither handles fails permanently into the exceptions list rather than looping.
/// </summary>
public sealed class WooConnector : IConnector
{
    private readonly ConnectorServices _services;
    private readonly Func<WooClient> _clientFactory;
    private readonly ILogger _logger;
    private readonly WooCatalogSync _catalog;
    private readonly WooOrderImport _orders;
    private readonly WooCatalogReviewJobs _review;

    // secrets: the add-on's secret store; webhook.health (S3) generates and saves the webhook secret there.
    public WooConnector(ConnectorServices services, Func<WooClient> clientFactory, IConnectorSecretStore? secrets = null)
    {
        _services = services;
        _clientFactory = clientFactory;
        _logger = services.LoggerFactory.CreateLogger<WooConnector>();
        _catalog = new WooCatalogSync(services, clientFactory);
        _orders = new WooOrderImport(services, clientFactory, secrets);
        _review = new WooCatalogReviewJobs(services, _catalog, clientFactory);
        RecurringJobs = [.. _catalog.RecurringJobs, .. _orders.RecurringJobs];
    }

    public ConnectorSettings Settings => _services.Settings;

    public IReadOnlyList<RecurringJob> RecurringJobs { get; }

    /// <summary>The catalog side (S2): RunMatchAsync / CreateInWooAsync for the S5 match review.</summary>
    public WooCatalogSync Catalog => _catalog;

    /// <summary>A client for this store, built from the DPAPI credentials on each call (so a key rotation takes effect without a restart).</summary>
    public WooClient CreateClient() => _clientFactory();

    public Task HandleAsync(ConnectorJob job, CancellationToken cancellationToken)
    {
        if (WooCatalogSync.Handles(job.Kind))
            return _catalog.HandleAsync(job, cancellationToken);
        if (WooOrderImport.Handles(job.Kind))
            return _orders.HandleAsync(job, cancellationToken);
        // S5: the match review's jobs (catalog.match / catalog.create / catalog.link), enqueued by the connector UI.
        if (ReviewJobKinds.Handles(job.Kind))
            return _review.HandleAsync(job, cancellationToken);

        _logger.LogWarning("WooCommerce connector {ConnectorId} has no handler for {JobKind}", Settings.Id, job.Kind);
        throw new ConnectorJobException($"The WooCommerce connector does not handle '{job.Kind}' jobs.", retryable: false);
    }
}

/// <summary>Creates <see cref="WooConnector"/>s; credentials come from the add-on's DPAPI store, keyed by connector id.</summary>
public sealed class WooConnectorFactory : IConnectorFactory
{
    /// <summary>Named HttpClient registration the factory asks IHttpClientFactory for.</summary>
    public const string HttpClientName = "woocommerce";

    private readonly IHttpClientFactory _httpClients;
    private readonly IConnectorSecretStore _secrets;

    public WooConnectorFactory(IHttpClientFactory httpClients, IConnectorSecretStore secrets)
    {
        _httpClients = httpClients;
        _secrets = secrets;
    }

    public string Platform => ConnectorPlatforms.WooCommerce;

    public IConnector Create(ConnectorServices services) =>
        new WooConnector(services, () => CreateClient(services), _secrets);

    private WooClient CreateClient(ConnectorServices services)
    {
        var settings = services.Settings;
        var key = _secrets.Get(ConnectorSecretNames.WooConsumerKey(settings.Id));
        var secret = _secrets.Get(ConnectorSecretNames.WooConsumerSecret(settings.Id));
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
            throw new ConnectorJobException(
                $"WooCommerce consumer key/secret for connector '{settings.Id}' are not set — reconnect the store.",
                retryable: false);
        if (!Uri.TryCreate(settings.StoreUrl, UriKind.Absolute, out var storeUrl))
            throw new ConnectorJobException($"Connector '{settings.Id}' has no valid storeUrl.", retryable: false);

        return new WooClient(
            _httpClients.CreateClient(HttpClientName),
            new WooClientOptions { StoreUrl = storeUrl, ConsumerKey = key, ConsumerSecret = secret },
            services.LoggerFactory.CreateLogger<WooClient>());
    }
}
