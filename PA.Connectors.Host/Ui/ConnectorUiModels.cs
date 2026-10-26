using System.Globalization;
using System.Text.RegularExpressions;
using PA.Connectors.Core;

namespace PA.Connectors.Host.Ui;

/// <summary>
/// Create / edit body of a connector (POST /connectors/api/connectors, PUT …/{id}). Null = keep the
/// current value (defaults on create). The Woo keys are write-only: stored in the add-on's DPAPI
/// store and never returned; they go together (both or neither); both blank = keep the stored ones —
/// unless the store address moves to another scheme/host/port, which clears them unless both are
/// re-entered. There is no POSitive API address here: that is the host's (connectorhost.json).
/// </summary>
public sealed record ConnectorWrite
{
    public string? Id { get; init; }
    public string? StoreUrl { get; init; }
    public string? ConsumerKey { get; init; }
    public string? ConsumerSecret { get; init; }
    /// <summary>"" clears it (polling only).</summary>
    public string? WebhookBaseUrl { get; init; }
    public int? PriceLevel { get; init; }
    public decimal? StockBuffer { get; init; }
    public string? PublishNewAs { get; init; }
    public bool? OverwriteTitleDescription { get; init; }
    public bool? ImportOnHold { get; init; }
    public List<string>? PayOnCompletionMethods { get; init; }
    /// <summary>Woo payment method id → tender (card / check / onAccount). Replaces the whole map; card entries are not stored (card is the default).</summary>
    public Dictionary<string, string>? PaymentTenders { get; init; }
    public int? PushIntervalSeconds { get; init; }
    public int? OrderPollMinutes { get; init; }
    /// <summary>Create only: where the order poll starts (default: now — history is not imported).</summary>
    public OrderStart? OrderStart { get; init; }
    /// <summary>
    /// Edit only: the wizard step this save completes (<see cref="ConnectorSetup.Settings"/> or
    /// <see cref="ConnectorSetup.Orders"/>), so the rail ticks it. Null = a plain edit.
    /// </summary>
    public string? SetupStep { get; init; }
}

/// <summary>
/// The connect wizard's steps (the rail on Connect a store / Settings): 1 store connection, 2 settings,
/// 3 POSitive API key, 4 orders &amp; webhooks, 5 enable. Steps 2 and 4 have defaults, so a new connector
/// (saved at step 1) records them as pending until their own Next saves them (cursor
/// <see cref="PendingCursor"/>); a connector made before the rail has no such cursor = nothing pending.
/// </summary>
public static class ConnectorSetup
{
    public const string Store = "store";
    public const string Settings = "settings";
    public const string Key = "key";
    public const string Orders = "orders";
    public const string Enable = "enable";

    /// <summary>Steps a create leaves to confirm, comma separated ("" = none).</summary>
    public const string PendingCursor = "ui.setup.pending";

    public static readonly IReadOnlyList<string> Confirmable = [Settings, Orders];

    public const string Done = "done";
    public const string Todo = "todo";
    public const string Attention = "attention";
}

/// <summary>One step on the rail: <see cref="State"/> done / todo / attention, and why in a sentence.</summary>
public sealed record SetupStepView(int Number, string Key, string Title, string State, string Note);

/// <summary>The rail: every step's state; <see cref="Current"/> = the first not done (null = all done).</summary>
public sealed record SetupView(IReadOnlyList<SetupStepView> Steps, string? Current);

/// <summary><c>now</c> (default) or <c>since</c> + a local date: orders modified from then on are imported.</summary>
public sealed record OrderStart(string? Mode, DateOnly? Since);

public sealed record TestConnectionBody(string? StoreUrl, string? ConsumerKey, string? ConsumerSecret, string? ConnectorId);

/// <summary>The connector's POSitive API key, straight from the create response or pasted. Never echoed.</summary>
public sealed record PosKeyBody(string? ApiKey, bool ConfirmDeveloperId = false);

public sealed record MatchBody(bool CreateMissing = true);

public sealed record CreateInWooBody(List<int>? InvNos);

/// <summary>
/// <paramref name="Replace"/>: the owner confirmed "unlink the old one first" after a
/// <c>link_conflict</c> answer — the job unlinks whatever either side is linked to, then links.
/// </summary>
public sealed record LinkBody(long ExternalId, int InvNo, long? ExceptionId = null, bool Replace = false);

public sealed record IgnoreBody(string? ExternalRef, string? Kind, string? Note);

/// <summary>Bulk Ignore on the match review (a whole group at once): at most <see cref="ConnectorUiEndpoints.MaxBulk"/> items.</summary>
public sealed record IgnoreManyBody(List<IgnoreBody>? Items, string? Note);

/// <summary>Bulk Un-ignore: the refs to review again.</summary>
public sealed record UnignoreManyBody(List<string>? ExternalRefs);

/// <summary>Settings as the pages show them: everything in connectors.json, plus which secrets are stored (never their values).</summary>
public sealed record ConnectorView(
    string Id,
    string Platform,
    bool Enabled,
    bool Running,
    IReadOnlyList<string> Problems,
    string? StoreUrl,
    string? DeveloperId,
    string? WebhookBaseUrl,
    int PriceLevel,
    decimal StockBuffer,
    string PublishNewAs,
    bool OverwriteTitleDescription,
    bool ImportOnHold,
    IReadOnlyList<string> PayOnCompletionMethods,
    IReadOnlyDictionary<string, string> PaymentTenders,
    int PushIntervalSeconds,
    int OrderPollMinutes,
    SecretPresence Secrets,
    ConnectorStatusView? Status,
    SetupView Setup);

public sealed record SecretPresence(bool WooConsumerKey, bool WooConsumerSecret, bool WooWebhookSecret, bool PosApiKey);

/// <summary>
/// <see cref="OpenExceptions"/> = things needing action (order problems, failed jobs, refused links, …);
/// <see cref="NeedsReview"/> = what the match left for the owner to look at on Match review (normal, not errors).
/// </summary>
public sealed record ConnectorStatusView(
    int QueueDepth,
    int OpenExceptions,
    DateTime? LastPushAt,
    DateTime? LastImportAt,
    DateTime? LastPollAt,
    DateTime? LastWebhookCheckAt,
    int StockHoldOrders,
    int StockHoldItems,
    int LinkedItems,
    WebhookStatusView Webhooks,
    string? OrdersCursor,
    string? CatalogCursor,
    int NeedsReview,
    MatchProgressView Match);

/// <summary>
/// Where the catalog match stands, for the "next steps" panel: <see cref="Done"/> once a match ran
/// (the first push after Enable, or Run the match); <see cref="RunningJobId"/> while one is queued
/// or running; <see cref="Summary"/> = the last run's counts (CatalogRunSummary JSON).
/// </summary>
public sealed record MatchProgressView(bool Done, DateTime? LastRunAt, long? RunningJobId, string? RunningStatus,
    System.Text.Json.JsonElement? Summary);

/// <summary><c>webhooks</c> when webhookBaseUrl is set (the connector registers and checks them), else <c>polling</c>.</summary>
public sealed record WebhookStatusView(string Mode, string? DeliveryUrl, int UnhealthyOpen);

/// <summary>Field rules for <see cref="ConnectorWrite"/> beyond <see cref="ConnectorConfigFile.Validate"/>.</summary>
public static partial class ConnectorWriteRules
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PaymentMethodId();

    public const int MaxPaymentMethods = 20;
    public const int MaxCredentialLength = 200;

    /// <summary>The Developer ID a connector's key is minted under: one per store, so its links stay its own.</summary>
    public static string DefaultDeveloperId(string connectorId) => "WOO-" + connectorId;

    /// <summary>The settings after the edit (or the new connector's).</summary>
    public static ConnectorSettings Apply(ConnectorWrite write, ConnectorSettings current) => current with
    {
        StoreUrl = write.StoreUrl is null ? current.StoreUrl : write.StoreUrl.Trim().TrimEnd('/'),
        WebhookBaseUrl = write.WebhookBaseUrl is null
            ? current.WebhookBaseUrl
            : string.IsNullOrWhiteSpace(write.WebhookBaseUrl) ? null : write.WebhookBaseUrl.Trim().TrimEnd('/'),
        PriceLevel = write.PriceLevel ?? current.PriceLevel,
        StockBuffer = write.StockBuffer ?? current.StockBuffer,
        PublishNewAs = write.PublishNewAs?.Trim().ToLowerInvariant() ?? current.PublishNewAs,
        OverwriteTitleDescription = write.OverwriteTitleDescription ?? current.OverwriteTitleDescription,
        ImportOnHold = write.ImportOnHold ?? current.ImportOnHold,
        PayOnCompletionMethods = write.PayOnCompletionMethods is null
            ? current.PayOnCompletionMethods
            : write.PayOnCompletionMethods.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        PaymentTenders = write.PaymentTenders is null ? current.PaymentTenders : NormalizeTenders(write.PaymentTenders),
        PushIntervalSeconds = write.PushIntervalSeconds ?? current.PushIntervalSeconds,
        OrderPollMinutes = write.OrderPollMinutes ?? current.OrderPollMinutes,
    };

    /// <summary>
    /// The map as stored: method ids trimmed (first spelling wins on a case-insensitive repeat), known
    /// tenders in their stored spelling, card entries dropped (card is what an unmapped method posts as).
    /// An unknown tender is kept as typed, so validation names it.
    /// </summary>
    public static Dictionary<string, string> NormalizeTenders(IReadOnlyDictionary<string, string> map)
    {
        var result = new Dictionary<string, string>();
        foreach (var (rawMethod, rawTender) in map)
        {
            var method = rawMethod?.Trim() ?? "";
            if (method.Length == 0 || result.Keys.Any(k => string.Equals(k, method, StringComparison.OrdinalIgnoreCase)))
                continue;
            var tender = PaymentTenders.Normalize(rawTender);
            if (tender == PaymentTenders.Card)
                continue;
            result[method] = tender ?? rawTender?.Trim() ?? "";
        }
        return result;
    }

    /// <summary>
    /// Per-field problems of <paramref name="settings"/> as it would be saved into <paramref name="document"/>
    /// (which still holds the other connectors). Empty = fine.
    /// </summary>
    public static Dictionary<string, string[]> Validate(ConnectorSettings settings, ConnectorWrite write, ConnectorsDocument document,
        bool creating)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
                errors[field] = list = [];
            list.Add(message);
        }

        // The engine's own rules; each message starts with the field it is about.
        foreach (var message in ConnectorConfigFile.Validate(settings))
            Add(message.Split(' ', 2)[0], char.ToUpperInvariant(message[0]) + message[1..]);

        var others = document.Connectors.Where(c => !string.Equals(c.Id, settings.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (creating && document.Connectors.Any(c => string.Equals(c.Id, settings.Id, StringComparison.OrdinalIgnoreCase)))
            Add("id", $"A connector called '{settings.Id}' already exists.");
        if (!string.IsNullOrWhiteSpace(settings.DeveloperId)
            && others.FirstOrDefault(c => string.Equals(c.DeveloperId?.Trim(), settings.DeveloperId.Trim(), StringComparison.OrdinalIgnoreCase)) is { } clash)
            Add("developerId", $"Developer ID '{settings.DeveloperId.Trim()}' is already used by connector '{clash.Id}'. Each store needs its own.");

        if (settings.WebhookBaseUrl is { } hooks)
        {
            if (!Uri.TryCreate(hooks, UriKind.Absolute, out var url)
                || !(url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && (url.IsLoopback || IsDockerHost(url))))
                || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
                Add("webhookBaseUrl", "Must be the server's public https address, e.g. https://shop1.ngrok.app (no path or query) — or leave it empty for polling only.");
        }

        if (settings.PayOnCompletionMethods.Count > MaxPaymentMethods)
            Add("payOnCompletionMethods", $"At most {MaxPaymentMethods} payment methods.");
        foreach (var method in settings.PayOnCompletionMethods.Where(m => !PaymentMethodId().IsMatch(m)))
            Add("payOnCompletionMethods", $"'{method}' is not a Woo payment method id (letters, digits, '-' and '_', e.g. cod, cheque).");
        if (settings.PaymentTenders.Count > MaxPaymentMethods)
            Add("paymentTenders", $"At most {MaxPaymentMethods} payment methods.");
        foreach (var method in settings.PaymentTenders.Keys.Where(m => !PaymentMethodId().IsMatch(m)))
            Add("paymentTenders", $"'{method}' is not a Woo payment method id (letters, digits, '-' and '_', e.g. stripe, bacs).");

        foreach (var (field, value) in new[] { ("consumerKey", write.ConsumerKey), ("consumerSecret", write.ConsumerSecret) })
        {
            if (string.IsNullOrEmpty(value))
                continue;
            if (value.Length > MaxCredentialLength || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
                Add(field, "Paste the key exactly as WooCommerce shows it (no spaces), at most 200 characters.");
        }
        if (string.IsNullOrWhiteSpace(write.ConsumerKey) != string.IsNullOrWhiteSpace(write.ConsumerSecret))
            Add(string.IsNullOrWhiteSpace(write.ConsumerKey) ? "consumerKey" : "consumerSecret",
                "Enter the consumer key and the consumer secret together (or leave both empty to keep the stored ones).");

        return errors.ToDictionary(e => e.Key, e => e.Value.ToArray());
    }

    /// <summary>
    /// True when both are absolute URLs with the same scheme, host and port — the Woo keys stored for
    /// one may be used with the other. Anything unparsable is "not the same".
    /// </summary>
    public static bool SameOrigin(string? a, string? b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
        && string.Equals(x.Scheme, y.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(x.IdnHost, y.IdnHost, StringComparison.OrdinalIgnoreCase)
        && x.Port == y.Port
        && string.IsNullOrEmpty(x.UserInfo) && string.IsNullOrEmpty(y.UserInfo);

    /// <summary>A URL for logs and audit lines: scheme, host, port and path — never user info or query.</summary>
    public static string ForLog(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? u.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : "(invalid address)";

    /// <summary>The dev store calls the server through Docker's host alias over http.</summary>
    private static bool IsDockerHost(Uri url) => string.Equals(url.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase);

    /// <summary>The order poll's starting cursor (UTC, ISO 8601) for <paramref name="start"/>.</summary>
    public static string? OrderCursor(OrderStart? start, DateTime utcNow, TimeZoneInfo localZone, out string? error)
    {
        error = null;
        var mode = start?.Mode?.Trim().ToLowerInvariant() ?? "now";
        if (mode == "now")
            return FormatUtc(utcNow);
        if (mode != "since")
        {
            error = "orderStart.mode must be 'now' or 'since'.";
            return null;
        }
        if (start!.Since is not { } since)
        {
            error = "orderStart.since is required with mode 'since' (a date, e.g. 2026-10-01).";
            return null;
        }
        var local = since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, localZone);
        if (utc > utcNow)
        {
            error = "orderStart.since cannot be in the future.";
            return null;
        }
        if (utc < utcNow.AddDays(-366))
        {
            error = "orderStart.since can go back one year at most.";
            return null;
        }
        return FormatUtc(utc);
    }

    public static string FormatUtc(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
}
