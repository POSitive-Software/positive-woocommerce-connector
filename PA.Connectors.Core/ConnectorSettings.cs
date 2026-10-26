using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PA.Connectors.Core.Secrets;

namespace PA.Connectors.Core;

/// <summary>
/// One configured store connection, as held in
/// <c>%ProgramData%\POSitiveAnywhereV2\connectors.json</c> (design §2.1: config
/// is not in a database). Credentials are NOT here — they live in the add-on's
/// DPAPI store under <c>ConnectorSecretNames.*(Id)</c>, including the
/// connector's own POSitive API key. Defaults are the approved slice-1 choices
/// (design §9).
/// </summary>
public sealed record ConnectorSettings
{
    /// <summary>Stable id: lowercase letters, digits and '-', 1–40 chars. Used in the webhook URL, secret names and every connectors.db row.</summary>
    public string Id { get; init; } = "";

    /// <summary>Platform adapter: <c>woocommerce</c> (the only one in slice 1).</summary>
    public string Platform { get; init; } = ConnectorPlatforms.WooCommerce;

    /// <summary>False = configured but not running (no polls, no pushes, webhooks still accepted and queued).</summary>
    public bool Enabled { get; init; }

    /// <summary>Store base URL, e.g. <c>https://shop.example.com</c>.</summary>
    public string? StoreUrl { get; init; }

    /// <summary>POSitive price level (1–6) pushed as the platform's regular price.</summary>
    public int PriceLevel { get; init; } = 1;

    /// <summary>Units held back from the web: pushed stock = available − buffer, never below 0.</summary>
    public decimal StockBuffer { get; init; }

    /// <summary>Status of products the connector creates on the platform: <c>draft</c> (default) or <c>publish</c>.</summary>
    public string PublishNewAs { get; init; } = PublishStatuses.Draft;

    /// <summary>True = send title/description on every push, not only when the connector creates the product.</summary>
    public bool OverwriteTitleDescription { get; init; }

    /// <summary>True = also import <c>on-hold</c> orders (bank transfer / cheque). Default: paid orders only.</summary>
    public bool ImportOnHold { get; init; }

    /// <summary>Incremental price/stock push cadence.</summary>
    public int PushIntervalSeconds { get; init; } = 60;

    /// <summary>Backstop order poll cadence (webhooks are not retried by Woo).</summary>
    public int OrderPollMinutes { get; init; } = 5;

    // There is deliberately no per-connector POSitive API address: the connector sends its API key
    // there, so it comes from the host's own settings (connectorhost.json Connectors:PosApiBaseUrl,
    // loopback only — PosApiAddress). A "posApiBaseUrl" left in an older connectors.json is ignored
    // on load and dropped on the next save.

    /// <summary>
    /// Informational: the APIDevAccess DeveloperID the connector's API key was
    /// minted under (e.g. <c>WOO-shop1</c>). The key itself — which is what
    /// decides the tenant — is in the secret store (ConnectorSecretNames.PosApiKey).
    /// </summary>
    public string? DeveloperId { get; init; }

    /// <summary>
    /// Public base URL the platform delivers webhooks to — the POSitive server's
    /// ngrok domain, e.g. <c>https://shop1.ngrok.app</c>. The connector registers
    /// <c>{webhookBaseUrl}/hooks/woocommerce/{id}</c> itself (S3, webhook.health).
    /// Empty = no webhook management: the backstop poll alone imports orders.
    /// </summary>
    public string? WebhookBaseUrl { get; init; }

    /// <summary>
    /// Woo <c>payment_method</c> ids (case-insensitive) whose orders are paid on delivery, not at
    /// checkout: Woo puts them in <c>processing</c> unpaid, so their payment is posted only once the
    /// order is <c>completed</c> (default: <c>cod</c>, cash on delivery). The order itself is still
    /// imported at <c>processing</c>.
    /// </summary>
    public List<string> PayOnCompletionMethods { get; init; } = ["cod"];

    /// <summary>
    /// Woo <c>payment_method</c> id (case-insensitive) → the POSitive tender its payment is posted as
    /// (<see cref="Core.PaymentTenders"/>: <c>card</c>, <c>check</c>, <c>onAccount</c>). A method not
    /// listed posts as <c>card</c> (tender R), the behaviour before this setting existed.
    /// </summary>
    public Dictionary<string, string> PaymentTenders { get; init; } = [];
}

/// <summary>
/// The POSitive tenders a Woo payment can be posted as, each a <c>paymentType</c> of
/// <c>POST /api/v2/transactions/payments</c>: <c>card</c> = 'R' (charge card), <c>check</c> = 'C',
/// <c>onAccount</c> = 'A' (also posts the closed deposit charge to the customer's AR ledger). The
/// public API has no cash payment type, so cash is not offered.
/// </summary>
public static class PaymentTenders
{
    public const string Card = "card";
    public const string Check = "check";
    public const string OnAccount = "onAccount";

    /// <summary>The allowed setting values, in the order the Settings page lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Card, Check, OnAccount];

    /// <summary>The value as stored (card / check / onAccount), or null when it is not one of them.</summary>
    public static string? Normalize(string? tender) =>
        All.FirstOrDefault(t => string.Equals(t, tender?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The tender for an order paid with <paramref name="paymentMethod"/> (unmapped or unknown → <see cref="Card"/>).</summary>
    public static string For(ConnectorSettings settings, string? paymentMethod)
    {
        if (string.IsNullOrWhiteSpace(paymentMethod) || settings.PaymentTenders is not { Count: > 0 } map)
            return Card;
        var method = paymentMethod.Trim();
        foreach (var (key, value) in map)
            if (string.Equals(key?.Trim(), method, StringComparison.OrdinalIgnoreCase))
                return Normalize(value) ?? Card;
        return Card;
    }

    /// <summary>The API's paymentType for a tender: R / C / A.</summary>
    public static string PosPaymentType(string tender) => Normalize(tender) switch
    {
        Check => "C",
        OnAccount => "A",
        _ => "R",
    };

    /// <summary>How the pages and log lines name a tender.</summary>
    public static string Describe(string tender) => Normalize(tender) switch
    {
        Check => "check (tender C)",
        OnAccount => "on account (tender A)",
        _ => "charge card (tender R)",
    };
}

/// <summary>
/// Where the connector calls POSitive Anywhere — and so where it sends its POSitive API key. A host
/// setting (connectorhost.json <c>Connectors:PosApiBaseUrl</c>), never a connector setting a portal
/// user can edit, and it must be this machine: a non-loopback address refuses to start.
/// </summary>
public sealed record PosApiEndpoint(Uri BaseUrl)
{
    public static PosApiEndpoint Default { get; } = new(new Uri(PosApiAddress.Default));
}

public static class PosApiAddress
{
    public const string Default = "http://localhost:5210";
    public const string SettingKey = "Connectors:PosApiBaseUrl";

    /// <summary>
    /// The configured address (blank = <see cref="Default"/>) when it is an http(s) URL on
    /// localhost / 127.0.0.1 / [::1] with no user info, query or fragment; anything else throws.
    /// </summary>
    public static PosApiEndpoint Require(string? configured)
    {
        var raw = string.IsNullOrWhiteSpace(configured) ? Default : configured.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            || !(url.IsLoopback || string.Equals(url.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            throw new InvalidOperationException(
                $"{SettingKey} must be the POSitive Anywhere server on this machine: an http(s) URL on localhost, 127.0.0.1 or [::1] " +
                $"(default {Default}). The connector sends its POSitive API key there, so no other address is accepted.");
        return new PosApiEndpoint(new Uri(url.GetLeftPart(UriPartial.Path).TrimEnd('/')));
    }
}

public static class ConnectorPlatforms
{
    public const string WooCommerce = "woocommerce";
}

public static class PublishStatuses
{
    public const string Draft = "draft";
    public const string Publish = "publish";
}

/// <summary>The whole connectors.json document.</summary>
public sealed record ConnectorsDocument
{
    public List<ConnectorSettings> Connectors { get; init; } = [];
}

/// <summary>
/// Reads and writes connectors.json. A missing file is "no connectors" (the
/// normal state of every install that never connected a store). Writes are
/// atomic (temp file + move), like InstanceConfig.
/// </summary>
public static class ConnectorConfigFile
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Parses the file. Missing or blank → empty document. Malformed JSON throws <see cref="JsonException"/>.</summary>
    /// <remarks>
    /// Opened sharing read, write and delete: the worker re-reads the file whenever it changes, and
    /// a read holding a plain read lock would make a concurrent <see cref="Save"/> (its move over
    /// the file) or an operator's edit fail with "being used by another process". A read that
    /// overlaps an in-place edit may see a half-written file; it throws <see cref="JsonException"/>
    /// and the worker reads again on the next change.
    /// </remarks>
    public static ConnectorsDocument Load(string path)
    {
        if (!File.Exists(path))
            return new ConnectorsDocument();
        string json;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return new ConnectorsDocument(); // removed between the check and the open
        }
        return Parse(json);
    }

    public static ConnectorsDocument Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ConnectorsDocument();
        var document = JsonSerializer.Deserialize<ConnectorsDocument>(json, JsonOptions) ?? new ConnectorsDocument();
        // "connectors": null in a hand-edited file is still "none".
        return document.Connectors is null ? new ConnectorsDocument() : document;
    }

    public static void Save(string path, ConnectorsDocument document)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Problems with one connector's settings; empty = usable.</summary>
    public static IReadOnlyList<string> Validate(ConnectorSettings settings)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.Id) || !IdPattern.IsMatch(settings.Id))
            errors.Add("id must be 1-40 lowercase letters, digits or '-', starting with a letter or digit.");
        if (!string.Equals(settings.Platform, ConnectorPlatforms.WooCommerce, StringComparison.Ordinal))
            errors.Add($"platform '{settings.Platform}' is not supported (expected '{ConnectorPlatforms.WooCommerce}').");
        if (!Uri.TryCreate(settings.StoreUrl, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            errors.Add("storeUrl must be an absolute http(s) URL.");
        else if (!string.IsNullOrEmpty(url.UserInfo))
            errors.Add("storeUrl must not contain a user name or password.");
        if (settings.PriceLevel is < 1 or > 6)
            errors.Add("priceLevel must be 1-6.");
        if (settings.StockBuffer < 0)
            errors.Add("stockBuffer must be 0 or more.");
        if (settings.PublishNewAs is not (PublishStatuses.Draft or PublishStatuses.Publish))
            errors.Add("publishNewAs must be 'draft' or 'publish'.");
        if (settings.PushIntervalSeconds is < 15 or > 86_400)
            errors.Add("pushIntervalSeconds must be 15-86400.");
        if (settings.OrderPollMinutes is < 1 or > 1_440)
            errors.Add("orderPollMinutes must be 1-1440.");
        foreach (var (method, tender) in settings.PaymentTenders ?? [])
            if (PaymentTenders.Normalize(tender) is null)
                errors.Add($"paymentTenders maps payment method '{method}' to '{tender}', which is not a tender (expected {string.Join(", ", PaymentTenders.All)}).");
        return errors;
    }

    /// <summary>
    /// The connectors that can run: enabled, valid, and with an id no other
    /// entry uses. Everything skipped is reported through <paramref name="skipped"/>.
    /// </summary>
    public static IReadOnlyList<ConnectorSettings> Runnable(
        ConnectorsDocument document, Action<string, IReadOnlyList<string>>? skipped = null)
    {
        var duplicateIds = document.Connectors
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Two connectors on one Developer ID (or one API-key secret) would share one APITableLink
        // space: each would see, and link over, the other's customers, orders and products.
        var duplicateDeveloperIds = document.Connectors
            .Where(c => !string.IsNullOrWhiteSpace(c.DeveloperId))
            .GroupBy(c => c.DeveloperId!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicateKeySecrets = document.Connectors
            .Where(c => !string.IsNullOrWhiteSpace(c.Id))
            .GroupBy(c => ConnectorSecretNames.PosApiKey(c.Id), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var runnable = new List<ConnectorSettings>();
        foreach (var connector in document.Connectors)
        {
            if (!connector.Enabled)
                continue;
            var errors = Validate(connector).ToList();
            if (duplicateIds.Contains(connector.Id))
                errors.Add("id is used by more than one connector.");
            if (!string.IsNullOrWhiteSpace(connector.DeveloperId) && duplicateDeveloperIds.Contains(connector.DeveloperId.Trim()))
                errors.Add($"developerId '{connector.DeveloperId.Trim()}' is used by more than one connector; each connector needs its own Developer ID and API key.");
            if (!string.IsNullOrWhiteSpace(connector.Id) && !duplicateIds.Contains(connector.Id)
                && duplicateKeySecrets.Contains(ConnectorSecretNames.PosApiKey(connector.Id)))
                errors.Add($"its API-key secret '{ConnectorSecretNames.PosApiKey(connector.Id)}' is shared with another connector.");
            if (errors.Count > 0)
            {
                skipped?.Invoke(connector.Id, errors);
                continue;
            }
            runnable.Add(connector);
        }
        return runnable;
    }
}
