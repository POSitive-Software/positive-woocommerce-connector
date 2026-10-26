using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PA.Connectors.Core;
using PA.Connectors.Core.Secrets;
using PA.Connectors.Core.Stores;
using PA.Connectors.WooCommerce.Models;

namespace PA.Connectors.WooCommerce.Orders;

/// <summary>
/// webhook.health (design §4.1): the connector registers its own webhooks so
/// the owner never configures them. For each of <see cref="Topics"/> there must
/// be a webhook to <c>{webhookBaseUrl}/hooks/woocommerce/{id}</c>, signed with
/// the stored secret, status active. Missing → created (the secret is generated
/// and saved in the add-on's secret store first); paused/disabled → re-activated
/// (Woo disables a webhook after 5 failed deliveries, e.g. the tunnel was down,
/// so that also raises <c>webhook_unhealthy</c>); a different secret → replaced.
/// With no <see cref="ConnectorSettings.WebhookBaseUrl"/> it manages nothing and
/// the backstop poll alone imports orders.
/// </summary>
public sealed class WooWebhookHealth
{
    public const string TopicCreated = "order.created";
    public const string TopicUpdated = "order.updated";
    public static readonly IReadOnlyList<string> Topics = [TopicCreated, TopicUpdated];

    private readonly ConnectorServices _services;
    private readonly Func<WooClient> _client;
    private readonly IConnectorSecretStore? _secrets;
    private readonly ILogger _logger;
    private bool _skipLogged;

    public WooWebhookHealth(ConnectorServices services, Func<WooClient> client, IConnectorSecretStore? secrets)
    {
        _services = services;
        _client = client;
        _secrets = secrets;
        _logger = services.LoggerFactory.CreateLogger<WooWebhookHealth>();
    }

    private ConnectorSettings Settings => _services.Settings;

    /// <summary><c>{webhookBaseUrl}/hooks/woocommerce/{id}</c>, or null when no usable base URL is set.</summary>
    public static string? DeliveryUrl(ConnectorSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.WebhookBaseUrl)
            || !Uri.TryCreate(settings.WebhookBaseUrl.Trim(), UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            return null;
        return url.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/hooks/woocommerce/" + Uri.EscapeDataString(settings.Id);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var deliveryUrl = DeliveryUrl(Settings);
        if (deliveryUrl is null)
        {
            if (!_skipLogged)
            {
                _logger.LogInformation(
                    "Connector {ConnectorId} has no webhookBaseUrl; webhooks are not managed and orders arrive by the {Minutes}-minute poll only",
                    Settings.Id, Settings.OrderPollMinutes);
                _skipLogged = true;
            }
            return;
        }
        if (_secrets is null)
            throw new ConnectorJobException("webhook.health needs the connector secret store.", retryable: false);

        var secretName = ConnectorSecretNames.WooWebhookSecret(Settings.Id);
        var secret = _secrets.Get(secretName);
        var newSecret = string.IsNullOrEmpty(secret);
        if (newSecret)
        {
            // Saved BEFORE Woo knows it, so a delivery signed with it can always be verified.
            secret = GenerateSecret();
            _secrets.Set(secretName, secret);
            _logger.LogInformation("Connector {ConnectorId}: generated a new webhook secret", Settings.Id);
        }

        var client = _client();
        var ours = (await client.ListWebhooksAsync(cancellationToken))
            .Where(h => SameUrl(h.DeliveryUrl, deliveryUrl))
            .ToList();

        var reactivated = new List<string>();
        foreach (var topic in Topics)
        {
            var hook = ours.Where(h => string.Equals(h.Topic, topic, StringComparison.OrdinalIgnoreCase))
                .OrderBy(h => h.Status == WooWebhookStatuses.Active ? 0 : 1).ThenBy(h => h.Id)
                .FirstOrDefault();
            if (hook is null)
            {
                var created = await client.CreateWebhookAsync(new WooWebhookWrite
                {
                    Name = $"POSitive Anywhere ({Settings.Id}) {topic}",
                    Topic = topic,
                    DeliveryUrl = deliveryUrl,
                    Secret = secret,
                    Status = WooWebhookStatuses.Active,
                }, cancellationToken);
                _logger.LogInformation("Connector {ConnectorId}: created Woo webhook {WebhookId} {Topic} → {Url}",
                    Settings.Id, created.Id, topic, deliveryUrl);
                continue;
            }

            var inactive = !string.Equals(hook.Status, WooWebhookStatuses.Active, StringComparison.OrdinalIgnoreCase);
            // Woo returns the secret on reads; when it does not, a freshly generated one must still be sent.
            var wrongSecret = newSecret || (hook.Secret is not null && hook.Secret != secret);
            if (!inactive && !wrongSecret)
                continue;

            await client.UpdateWebhookAsync(hook.Id, new WooWebhookWrite
            {
                Status = inactive ? WooWebhookStatuses.Active : null,
                Secret = wrongSecret ? secret : null,
            }, cancellationToken);
            _logger.LogInformation("Connector {ConnectorId}: Woo webhook {WebhookId} {Topic}{Activated}{Secret}", Settings.Id, hook.Id,
                topic, inactive ? $" re-activated (was {hook.Status})" : "", wrongSecret ? " secret replaced" : "");
            if (inactive)
                reactivated.Add($"{topic} (#{hook.Id}, was {hook.Status})");
        }

        if (reactivated.Count > 0)
            await _services.Exceptions.RaiseAsync(new ConnectorExceptionInput(Settings.Id, ConnectorExceptionKinds.WebhookUnhealthy,
                "webhooks",
                $"WooCommerce had stopped delivering {string.Join(", ", reactivated)} to {deliveryUrl}; the connector re-activated it. " +
                "Woo disables a webhook after 5 failed deliveries — check that the POSitive server's tunnel is up. " +
                "Orders in between are picked up by the backstop poll."), cancellationToken);
    }

    internal static bool SameUrl(string? a, string b) =>
        a is not null && string.Equals(a.Trim().TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string GenerateSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
