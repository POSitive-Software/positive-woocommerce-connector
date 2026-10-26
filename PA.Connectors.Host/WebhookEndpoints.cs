using System.Globalization;
using System.Text.Json;
using PA.Connectors.Core;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Secrets;
using PA.Connectors.Core.Stores;
using PA.Connectors.WooCommerce;

namespace PA.Connectors.Host;

/// <summary>
/// Webhook receivers. The platform reaches them through PA.Server's /hooks/*
/// forward (raw body and X-WC-* headers untouched, so the HMAC still verifies).
/// A receiver verifies, enqueues and answers — it never does the work inline
/// (design §4.1); the worker drains the queue.
/// <para>
/// WooCommerce (design §4.1): body capped at <see cref="MaxBodyBytes"/>; an
/// unknown connector id is 401 <c>invalid_signature</c>, the same answer as a bad
/// signature (so ids cannot be probed); Woo's unsigned creation ping (form body
/// <c>webhook_id=N</c>) is 200; the HMAC is verified over the raw bytes BEFORE
/// anything is parsed (401); only <c>order.created</c> / <c>order.updated</c>
/// deliveries become an <c>order.import</c> job keyed by the Woo order id
/// (duplicates coalesce; the job re-fetches the order and ignores this body);
/// any other topic is 200 and ignored, so Woo never counts it as a failure.
/// </para>
/// </summary>
public static class WebhookEndpoints
{
    /// <summary>Same cap as PA.Server's forward. A Woo order body is a few KB.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    public const string TopicHeader = "X-WC-Webhook-Topic";
    public const string DeliveryHeader = "X-WC-Webhook-Delivery-ID";

    /// <summary>The topics the connector registers (webhook.health) and imports from.</summary>
    public static readonly IReadOnlySet<string> OrderTopics =
        new HashSet<string>(["order.created", "order.updated"], StringComparer.OrdinalIgnoreCase);

    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/hooks/woocommerce/{connectorId}", ReceiveWooAsync);
    }

    internal static async Task<IResult> ReceiveWooAsync(
        HttpContext http, string connectorId, IConnectorConfigSource config, IConnectorSecretStore secrets,
        IConnectorJobStore jobs, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("PA.Connectors.Host.Webhooks");

        var body = await ReadBodyAsync(http.Request, cancellationToken);
        if (body is null)
            return Problem(StatusCodes.Status413PayloadTooLarge, "payload_too_large",
                $"Webhook bodies are limited to {MaxBodyBytes} bytes.");

        ConnectorSettings? connector;
        try
        {
            connector = config.Load().Connectors.FirstOrDefault(c =>
                string.Equals(c.Id, connectorId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Platform, ConnectorPlatforms.WooCommerce, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Webhook for {ConnectorId} refused: {Path} is unreadable", connectorId, config.Path);
            return Problem(StatusCodes.Status503ServiceUnavailable, "config_unreadable",
                "The connector configuration could not be read; the delivery will be picked up by the backstop poll.");
        }
        var signature = http.Request.Headers[WooWebhookSignature.HeaderName].ToString();
        if (connector is null)
        {
            // Same answer (and about the same work) as a bad signature, so the URL cannot be used
            // to find out which connector ids exist.
            WooWebhookSignature.Verify(body, UnknownConnectorSecret, signature);
            logger.LogWarning("Webhook rejected: no WooCommerce connector {ConnectorId} is configured", Clip(connectorId));
            return InvalidSignature();
        }

        // Woo's creation ping: unsigned form post "webhook_id=N". Answer 200, nothing else.
        if (WooWebhookSignature.IsPing(http.Request.ContentType, body))
            return Results.Ok(new { ok = true, ping = true });

        // Verify the RAW bytes before parsing anything.
        var secret = secrets.Get(ConnectorSecretNames.WooWebhookSecret(connector.Id));
        if (!WooWebhookSignature.Verify(body, secret, signature))
        {
            logger.LogWarning("Webhook for connector {ConnectorId} rejected: {Reason}", connector.Id,
                string.IsNullOrEmpty(secret) ? "no webhook secret stored" : "signature mismatch");
            return InvalidSignature();
        }

        var topic = http.Request.Headers[TopicHeader].ToString();
        if (!OrderTopics.Contains(topic))
            return Results.Ok(new { ok = true, ignored = true, topic = string.IsNullOrEmpty(topic) ? null : topic });

        long orderId;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("id", out var id)
                || !id.TryGetInt64(out orderId)
                || orderId <= 0)
                return Problem(StatusCodes.Status400BadRequest, "order_id_missing", "The body has no numeric order id.");
        }
        catch (JsonException)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_json", "The body is not JSON.");
        }

        var delivery = http.Request.Headers[DeliveryHeader].ToString();
        var queued = await jobs.EnqueueAsync(new JobRequest(
            connector.Id,
            ConnectorJobKinds.OrderImport,
            orderId.ToString(CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(new
            {
                source = "webhook",
                topic = string.IsNullOrEmpty(topic) ? null : topic,
                deliveryId = string.IsNullOrEmpty(delivery) ? null : delivery,
            })), cancellationToken);
        logger.LogInformation("Webhook {Topic} for order {OrderId} on connector {ConnectorId}: job {JobId}{Coalesced}",
            topic, orderId, connector.Id, queued.JobId, queued.Coalesced ? " (coalesced)" : "");
        return Results.Ok(new { ok = true, queued = true, jobId = queued.JobId, coalesced = queued.Coalesced });
    }

    /// <summary>The raw body, or null when it is over <see cref="MaxBodyBytes"/>.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
            return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>A throwaway HMAC key for unknown connector ids (the result is ignored; it only evens the timing).</summary>
    private static readonly string UnknownConnectorSecret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    /// <summary>401 for a bad signature and for an unknown connector id alike.</summary>
    internal static IResult InvalidSignature() =>
        Problem(StatusCodes.Status401Unauthorized, "invalid_signature", $"{WooWebhookSignature.HeaderName} does not match the body.");

    private static string Clip(string value)
    {
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length <= 60 ? cleaned : cleaned[..60];
    }

    private static IResult Problem(int status, string errorCode, string detail) =>
        Results.Problem(statusCode: status, detail: detail,
            extensions: new Dictionary<string, object?> { ["errorCode"] = errorCode });
}
