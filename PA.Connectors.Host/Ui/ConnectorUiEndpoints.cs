using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PA.Connectors.Core;
using PA.Connectors.Core.Catalog;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Orders;
using PA.Connectors.Core.Pos;
using PA.Connectors.Core.Secrets;
using PA.Connectors.Core.Stores;
using PA.Connectors.WooCommerce;

namespace PA.Connectors.Host.Ui;

/// <summary>Everything the connector UI handlers use, as one injectable bundle.</summary>
public sealed class ConnectorUi
{
    public ConnectorUi(
        ConnectorConfigEditor config, IConnectorSecretStore secrets, ConnectorStores stores, SqliteConnectorUiStore ui,
        ConnectorWorker worker, IWooConnectionTester woo, IPosKeyVerifier keys, PosProductSearch search,
        ConnectorUiAudit audit, WooStoreAddress storeAddress, ConnectorUiRateLimiter limits, TimeProvider? time = null,
        IWooItemLookup? wooItems = null)
    {
        WooItems = wooItems;
        ReviewNames = new WooReviewNames();
        Config = config;
        Secrets = secrets;
        Stores = stores;
        Ui = ui;
        Worker = worker;
        Woo = woo;
        Keys = keys;
        Search = search;
        Audit = audit;
        StoreAddress = storeAddress;
        Limits = limits;
        Time = time ?? TimeProvider.System;
    }

    /// <summary>Which store addresses may be saved or tested (no private / loopback / link-local).</summary>
    public WooStoreAddress StoreAddress { get; }

    /// <summary>Per-user limit on test connection and the POSitive key checks.</summary>
    public ConnectorUiRateLimiter Limits { get; }

    public ConnectorConfigEditor Config { get; }
    public IConnectorSecretStore Secrets { get; }
    public ConnectorStores Stores { get; }
    public SqliteConnectorUiStore Ui { get; }
    public ConnectorWorker Worker { get; }
    public IWooConnectionTester Woo { get; }
    public IPosKeyVerifier Keys { get; }
    public PosProductSearch Search { get; }
    public ConnectorUiAudit Audit { get; }
    public TimeProvider Time { get; }

    /// <summary>Names a Woo item in a link refusal; null = the message goes without its name.</summary>
    public IWooItemLookup? WooItems { get; }

    /// <summary>The match review's Woo names and parents, cached per connector (one batched lookup per page load at most).</summary>
    public WooReviewNames ReviewNames { get; }
}

/// <summary>
/// The connector UI's JSON API (design §5, S5), under <c>/connectors/api</c>. Reached only through
/// PA.Server's <c>/connectors/**</c> forward, behind the portal login: viewing needs Operations
/// Dashboard, every write Operations Settings (106) — checked by PA.Server, and again here from the
/// trusted <see cref="PortalHeaders"/> (<see cref="ConnectorUiGate"/>). Every write is audited with
/// the portal user. Secrets go in and never come out. Catalog work is never run inline: the match,
/// "create in Woo", a hand link and every Retry are jobs for the worker.
/// </summary>
public static class ConnectorUiEndpoints
{
    /// <summary>
    /// Kinds the match review lists — and ONLY the match review: they are what the match left for the
    /// owner to look at (normal, not errors), never counted or listed as exceptions. Everything else
    /// (order problems, failed jobs/pushes/creates, refused hand links, webhooks) is an exception.
    /// </summary>
    public static readonly IReadOnlySet<string> ReviewKinds = CatalogExceptionKinds.Review;

    /// <summary>Most items one bulk Ignore / Un-ignore takes.</summary>
    public const int MaxBulk = 500;

    private static readonly string[] ReviewJobs = [ReviewJobKinds.CatalogMatch, ReviewJobKinds.CatalogCreate, ReviewJobKinds.CatalogLink];

    public static void MapConnectorUi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(ConnectorUiGate.ApiPrefix);

        api.MapGet("/me", (HttpContext http) =>
        {
            var caller = PortalCaller.From(http);
            return Results.Ok(new { user = caller.User, canWrite = caller.CanWrite });
        });

        api.MapGet("/connectors", ListAsync);
        api.MapPost("/connectors", CreateAsync);
        api.MapGet("/connectors/{id}", GetAsync);
        api.MapPut("/connectors/{id}", UpdateAsync);
        api.MapDelete("/connectors/{id}", RemoveAsync);
        api.MapPost("/connectors/{id}/enable", (string id, ConnectorUi ui, HttpContext http, CancellationToken ct) => SetEnabledAsync(id, true, ui, http, ct));
        api.MapPost("/connectors/{id}/disable", (string id, ConnectorUi ui, HttpContext http, CancellationToken ct) => SetEnabledAsync(id, false, ui, http, ct));
        api.MapPost("/test-connection", TestConnectionAsync);
        api.MapPost("/connectors/{id}/pos-key", SetPosKeyAsync);
        api.MapPost("/connectors/{id}/pos-key/verify", VerifyPosKeyAsync);
        api.MapPost("/connectors/{id}/order-start", SetOrderStartAsync);
        api.MapGet("/connectors/{id}/payment-gateways", PaymentGatewaysAsync);
        api.MapGet("/connectors/{id}/status", StatusAsync);

        api.MapGet("/connectors/{id}/review", ReviewAsync);
        api.MapPost("/connectors/{id}/review/match", RunMatchAsync);
        api.MapPost("/connectors/{id}/review/create", CreateInWooAsync);
        api.MapPost("/connectors/{id}/review/link", LinkAsync);
        api.MapPost("/connectors/{id}/review/ignore", IgnoreAsync);
        api.MapPost("/connectors/{id}/review/unignore", UnignoreAsync);
        api.MapPost("/connectors/{id}/review/ignore-many", IgnoreManyAsync);
        api.MapPost("/connectors/{id}/review/unignore-many", UnignoreManyAsync);
        api.MapGet("/connectors/{id}/pos-products", SearchProductsAsync);
        api.MapGet("/connectors/{id}/jobs/{jobId:long}", JobAsync);

        api.MapGet("/connectors/{id}/exceptions", ExceptionsAsync);
        api.MapGet("/connectors/{id}/exceptions/{exceptionId:long}", ExceptionAsync);
        api.MapPost("/connectors/{id}/exceptions/{exceptionId:long}/retry", RetryExceptionAsync);
        api.MapPost("/connectors/{id}/exceptions/{exceptionId:long}/ignore", IgnoreExceptionAsync);
    }

    // --- connectors ------------------------------------------------------------------

    internal static async Task<IResult> ListAsync(ConnectorUi ui, HttpContext http, CancellationToken cancellationToken)
    {
        var caller = PortalCaller.From(http);
        ConnectorsDocument document;
        try
        {
            document = ui.Config.Load();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // The path and the parser's message stay in the host log; viewers get a fixed sentence.
            ui.Audit.Failure(ex, $"connectors.json at {ui.Config.Path} could not be read");
            return Results.Ok(new
            {
                user = caller.User, canWrite = caller.CanWrite, worker = ui.Worker.State, version = HealthEndpoint.Version,
                configError = ConfigUnreadable,
                connectors = Array.Empty<ConnectorView>(),
            });
        }

        var views = new List<ConnectorView>();
        foreach (var connector in document.Connectors.Where(c => !string.IsNullOrWhiteSpace(c.Id)))
            views.Add(await ViewAsync(ui, document, connector, withStatus: true, cancellationToken));
        return Results.Ok(new
        {
            user = caller.User, canWrite = caller.CanWrite, worker = ui.Worker.State, version = HealthEndpoint.Version,
            configError = (string?)null, connectors = views,
        });
    }

    internal static async Task<IResult> GetAsync(string id, ConnectorUi ui, CancellationToken cancellationToken)
    {
        var document = ui.Config.Load();
        return Find(document, id) is { } connector
            ? Results.Ok(await ViewAsync(ui, document, connector, withStatus: true, cancellationToken))
            : NotFound(id);
    }

    /// <summary>
    /// The Settings page's payment-method rows: the store's enabled gateways, plus any method the
    /// setting maps that is not among them, each with the tender it posts as (unmapped = card).
    /// <c>read: false</c> when Woo could not be asked (no keys yet, store down): the page then shows
    /// the mapped methods only.
    /// </summary>
    internal static async Task<IResult> PaymentGatewaysAsync(string id, ConnectorUi ui, CancellationToken cancellationToken)
    {
        if (Find(ui.Config.Load(), id) is not { } connector)
            return NotFound(id);
        var gateways = ui.WooItems is null ? null : await ui.WooItems.GetPaymentGatewaysAsync(connector, cancellationToken);
        var rows = (gateways ?? []).Where(g => g.Enabled)
            .Select(g => new { id = g.Id, title = g.Title, enabled = true, tender = PaymentTenders.For(connector, g.Id) })
            .ToList();
        foreach (var (method, _) in connector.PaymentTenders ?? [])
        {
            if (string.IsNullOrWhiteSpace(method) || rows.Any(r => string.Equals(r.id, method.Trim(), StringComparison.OrdinalIgnoreCase)))
                continue;
            var known = gateways?.FirstOrDefault(g => string.Equals(g.Id, method.Trim(), StringComparison.OrdinalIgnoreCase));
            rows.Add(new { id = method.Trim(), title = known?.Title, enabled = false, tender = PaymentTenders.For(connector, method) });
        }
        return Results.Ok(new { read = gateways is not null, tenders = PaymentTenders.All, gateways = rows });
    }

    internal static async Task<IResult> CreateAsync(ConnectorWrite write, ConnectorUi ui, HttpContext http, CancellationToken cancellationToken)
    {
        var caller = PortalCaller.From(http);
        var id = write.Id?.Trim() ?? "";
        var cursor = ConnectorWriteRules.OrderCursor(write.OrderStart, ui.Time.GetUtcNow().UtcDateTime, ui.Time.LocalTimeZone, out var cursorError);
        if (await StoreAddressProblemAsync(ui, write.StoreUrl, cancellationToken) is { } refused)
            return Invalid(new() { ["storeUrl"] = [refused] });

        var outcome = await ui.Config.EditAsync<(ConnectorSettings? Saved, Dictionary<string, string[]>? Errors)>(document =>
        {
            var settings = ConnectorWriteRules.Apply(write, new ConnectorSettings
            {
                Id = id,
                Platform = ConnectorPlatforms.WooCommerce,
                Enabled = false, // enabled from the wizard's last step, once the keys are in
                DeveloperId = id.Length == 0 ? null : ConnectorWriteRules.DefaultDeveloperId(id),
            });
            var errors = ConnectorWriteRules.Validate(settings, write, document, creating: true);
            if (cursorError is not null)
                errors["orderStart"] = [cursorError];
            if (errors.Count > 0)
                return (null, (null, errors));
            return (document with { Connectors = [.. document.Connectors, settings] }, (settings, null));
        }, cancellationToken);
        if (outcome.Errors is not null)
            return Invalid(outcome.Errors);

        var saved = outcome.Saved!;
        var keysStored = StoreWooKeys(ui, saved.Id, write);
        // History is not imported unless asked: the poll starts now (or at the chosen date).
        if (await ui.Stores.Cursors.GetAsync(saved.Id, ConnectorCursorNames.OrdersModifiedAfter, cancellationToken) is null)
            await ui.Stores.Cursors.SetAsync(saved.Id, ConnectorCursorNames.OrdersModifiedAfter, cursor, cancellationToken);
        // The wizard saves the store first; its settings and orders steps keep their defaults until saved there.
        await ui.Stores.Cursors.SetAsync(saved.Id, ConnectorSetup.PendingCursor, string.Join(",", ConnectorSetup.Confirmable), cancellationToken);

        ui.Audit.Record(caller, "create", saved.Id,
            $"store {ConnectorWriteRules.ForLog(saved.StoreUrl)}, price level {saved.PriceLevel}, buffer {saved.StockBuffer}, new products {saved.PublishNewAs}, " +
            $"orders from {cursor}{(keysStored ? ", Woo keys stored" : "")}");
        var document = ui.Config.Load();
        return Results.Created($"{ConnectorUiGate.ApiPrefix}/connectors/{saved.Id}",
            await ViewAsync(ui, document, Find(document, saved.Id)!, withStatus: true, cancellationToken));
    }

    internal static async Task<IResult> UpdateAsync(string id, ConnectorWrite write, ConnectorUi ui, HttpContext http,
        CancellationToken cancellationToken)
    {
        var caller = PortalCaller.From(http);
        if (write.Id is not null && !string.Equals(write.Id.Trim(), id, StringComparison.OrdinalIgnoreCase))
            return Invalid(new() { ["id"] = ["A connector's id cannot be changed (it is in its webhook URL and every record). Remove it and connect again instead."] });
        if (write.OrderStart is not null)
            return Invalid(new() { ["orderStart"] = ["Use POST …/order-start to move where the order poll starts."] });
        var step = write.SetupStep?.Trim().ToLowerInvariant();
        if (step is not null && !ConnectorSetup.Confirmable.Contains(step))
            return Invalid(new() { ["setupStep"] = [$"setupStep must be one of: {string.Join(", ", ConnectorSetup.Confirmable)}."] });
        if (await StoreAddressProblemAsync(ui, write.StoreUrl, cancellationToken) is { } refused)
            return Invalid(new() { ["storeUrl"] = [refused] });

        var outcome = await ui.Config.EditAsync<(ConnectorSettings? Before, ConnectorSettings? After, Dictionary<string, string[]>? Errors)>(document =>
        {
            var index = document.Connectors.FindIndex(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return (null, (null, null, null));
            var before = document.Connectors[index];
            var after = ConnectorWriteRules.Apply(write, before);
            var errors = ConnectorWriteRules.Validate(after, write, document, creating: false);
            if (errors.Count > 0)
                return (null, (before, null, errors));
            var connectors = document.Connectors.ToList();
            connectors[index] = after;
            return (document with { Connectors = connectors }, (before, after, null));
        }, cancellationToken);
        if (outcome.Before is null)
            return NotFound(id);
        if (outcome.Errors is not null)
            return Invalid(outcome.Errors);

        // The stored Woo keys belong to the store they were entered for: moving the address to
        // another scheme/host/port without re-entering both clears them (they are never sent elsewhere).
        var keysStored = StoreWooKeys(ui, outcome.After!.Id, write);
        var keysCleared = false;
        if (!keysStored && !string.Equals(outcome.Before.StoreUrl, outcome.After.StoreUrl, StringComparison.Ordinal)
            && !ConnectorWriteRules.SameOrigin(outcome.Before.StoreUrl, outcome.After.StoreUrl))
        {
            keysCleared = ui.Secrets.Remove(ConnectorSecretNames.WooConsumerKey(outcome.After.Id))
                          | ui.Secrets.Remove(ConnectorSecretNames.WooConsumerSecret(outcome.After.Id));
        }
        if (step is not null)
            await ConfirmStepAsync(ui, outcome.After.Id, step, cancellationToken);
        ui.Audit.Record(caller, "update", outcome.After.Id,
            Describe(outcome.Before, outcome.After) + (keysStored ? "; Woo keys replaced" : "")
            + (keysCleared ? "; Woo keys cleared (new store address) — re-enter them" : "")
            + (step is null ? "" : $"; wizard step '{step}' saved"));
        var document = ui.Config.Load();
        return Results.Ok(await ViewAsync(ui, document, Find(document, id)!, withStatus: true, cancellationToken));
    }

    internal static async Task<IResult> SetEnabledAsync(string id, bool enable, ConnectorUi ui, HttpContext http, CancellationToken cancellationToken)
    {
        var caller = PortalCaller.From(http);
        var outcome = await ui.Config.EditAsync<(bool Found, IReadOnlyList<string>? Missing)>(document =>
        {
            var index = document.Connectors.FindIndex(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return (null, (false, null));
            var connector = document.Connectors[index] with { Enabled = enable };
            var connectors = document.Connectors.ToList();
            connectors[index] = connector;
            var updated = document with { Connectors = connectors };
            if (enable)
            {
                var missing = NotReady(ui, updated, connector);
                if (missing.Count > 0)
                    return (null, (true, missing));
            }
            return (updated, (true, null));
        }, cancellationToken);
        if (!outcome.Found)
            return NotFound(id);
        if (outcome.Missing is not null)
            return Problem(StatusCodes.Status409Conflict, "connector_not_ready",
                "The connector cannot be enabled yet: " + string.Join(" ", outcome.Missing),
                new() { ["problems"] = outcome.Missing });

        ui.Audit.Record(caller, enable ? "enable" : "disable", id, enable ? "connector enabled" : "connector disabled");
        var document = ui.Config.Load();
        return Results.Ok(await ViewAsync(ui, document, Find(document, id)!, withStatus: true, cancellationToken));
    }

    /// <summary>
    /// Removes the connector from connectors.json and its secrets from the add-on's store. Its data in
    /// connectors.db (item and order maps, exceptions, history) is kept, so connecting again under the
    /// same id carries on where it left off. The POSitive API key itself is revoked on the ops console.
    /// </summary>
    internal static async Task<IResult> RemoveAsync(string id, ConnectorUi ui, HttpContext http, CancellationToken cancellationToken)
    {
        var caller = PortalCaller.From(http);
        var removed = await ui.Config.EditAsync<ConnectorSettings?>(document =>
        {
            var connector = Find(document, id);
            return connector is null
                ? (null, null)
                : (document with { Connectors = document.Connectors.Where(c => !ReferenceEquals(c, connector)).ToList() }, connector);
        }, cancellationToken);
        if (removed is null)
            return NotFound(id);

        foreach (var name in ConnectorSecretNames.ForConnectorAll(removed.Id))
            ui.Secrets.Remove(name);
        ui.Audit.Record(caller, "remove", removed.Id,
            $"removed from {ui.Config.Path} and its secrets cleared; connectors.db data kept; Developer ID {removed.DeveloperId ?? "-"}");
        return Results.Ok(new
        {
            removed = removed.Id,
            developerId = removed.DeveloperId,
            note = "Its records in connectors.db are kept. Revoke its POSitive API key on the ops console (API keys card) " +
                   "and delete its webhooks in WooCommerce if the store is not coming back.",
        });
    }

    internal static async Task<IResult> TestConnectionAsync(TestConnectionBody body, ConnectorUi ui, HttpContext http,
        CancellationToken cancellationToken)
    {
        var caller = PortalCaller.From(http);
        if (RateLimited(ui, http, caller, "test-connection") is { } limited)
            return limited;
        var connector = string.IsNullOrWhiteSpace(body.ConnectorId) ? null : Find(ui.Config.Load(), body.ConnectorId);
        var storeUrl = string.IsNullOrWhiteSpace(body.StoreUrl) ? connector?.StoreUrl : body.StoreUrl.Trim();
        if (!Uri.TryCreate(storeUrl, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            return Invalid(new() { ["storeUrl"] = ["Enter the store's address, e.g. https://shop.example.com."] });
        if (await ui.StoreAddress.CheckAsync(url, cancellationToken) is { } refused)
            return Invalid(new() { ["storeUrl"] = [refused] });

        // The keys are a pair: both from the body, or both the stored ones — and the stored ones only
        // for the address they were saved with (scheme, host and port), so they never go elsewhere.
        var keyGiven = !string.IsNullOrWhiteSpace(body.ConsumerKey);
        var secretGiven = !string.IsNullOrWhiteSpace(body.ConsumerSecret);
        string? key, secret;
        var usingStored = false;
        if (keyGiven && secretGiven)
        {
            key = body.ConsumerKey!.Trim();
            secret = body.ConsumerSecret!.Trim();
        }
        else if (keyGiven || secretGiven)
        {
            return Invalid(new() { [keyGiven ? "consumerSecret" : "consumerKey"] =
                ["Enter the consumer key and the consumer secret together, or leave both empty to test with the stored ones."] });
        }
        else
        {
            if (connector is null)
                return Invalid(new() { ["consumerKey"] = ["Enter the consumer key and secret (WooCommerce → Settings → Advanced → REST API, Read/Write)."] });
            if (!ConnectorWriteRules.SameOrigin(url.ToString(), connector.StoreUrl))
                return Invalid(new() { ["consumerKey"] = ["The stored keys belong to the saved store address. Enter both keys to test a different address."] });
            key = ui.Secrets.Get(ConnectorSecretNames.WooConsumerKey(connector.Id));
            secret = ui.Secrets.Get(ConnectorSecretNames.WooConsumerSecret(connector.Id));
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
                return Invalid(new() { ["consumerKey"] = ["No keys are stored yet: enter the consumer key and secret (WooCommerce → Settings → Advanced → REST API, Read/Write)."] });
            usingStored = true;
        }

        ui.Audit.Record(caller, "test-connection", connector?.Id,
            $"store {ConnectorWriteRules.ForLog(url.ToString())}{(usingStored ? " with the stored keys" : "")}");
        try
        {
            var info = await ui.Woo.TestAsync(url, key, secret, cancellationToken);
            return Results.Ok(new { ok = true, storeUrl = ConnectorWriteRules.ForLog(url.ToString()).TrimEnd('/'), info, versions = "not read (only Woo's system status reports them, and it is too slow)" });
        }
        catch (WooApiException ex)
        {
            ui.Audit.Failure(ex, $"test connection to {ConnectorWriteRules.ForLog(url.ToString())} failed");
            return Results.Ok(new { ok = false, error = Explain(ex), status = (int?)ex.Status, code = ex.Code });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            ui.Audit.Failure(ex, $"test connection to {ConnectorWriteRules.ForLog(url.ToString())} failed");
            return Results.Ok(new { ok = false, error = IsBlocked(ex) ? BlockedAddress : Unreachable });
        }
    }

    private const string ConfigUnreadable = "The connector settings file could not be read. The details are in the connector host log (pa-connectors-*.log).";
    private const string Unreachable = "The store could not be reached. Check the address and that the site is up.";
    private const string BlockedAddress = "The store address points at this machine or a private network. Enter the store's public internet address.";

    private static bool IsBlocked(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is StoreAddressBlockedException)
                return true;
        }
        return false;
    }

    /// <summary>The <see cref="WooStoreAddress"/> verdict on an address a create / edit sets; null = fine (or left to the format check).</summary>
    private static async Task<string?> StoreAddressProblemAsync(ConnectorUi ui, string? storeUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeUrl) || !Uri.TryCreate(storeUrl.Trim(), UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            return null;
        return await ui.StoreAddress.CheckAsync(url, cancellationToken);
    }

    /// <summary>429 (with Retry-After) once the caller used up <paramref name="bucket"/>; null = go ahead.</summary>
    private static IResult? RateLimited(ConnectorUi ui, HttpContext http, PortalCaller caller, string bucket)
    {
        if (ui.Limits.TryAcquire(caller.User, bucket, out var wait))
            return null;
        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Problem(StatusCodes.Status429TooManyRequests, "rate_limited",
            $"Too many tries: at most {ConnectorUiRateLimiter.Limit} every {ConnectorUiRateLimiter.Window.TotalMinutes:0} minutes. Try again in {seconds} seconds.");
    }

    /// <summary>
    /// Stores the connector's POSitive API key after GET /api/whoami accepts it with the expected
    /// Developer ID. A different Developer ID is refused (409 developer_id_mismatch) unless the body
    /// confirms it, and one another connector records is always refused. The key is never echoed.
    /// </summary>
    internal static async Task<IResult> SetPosKeyAsync(string id, PosKeyBody body, ConnectorUi ui, HttpContext http,
        CancellationToken cancellationToken)
    {
        var caller = PortalCaller.From(http);
        var document = ui.Config.Load();
        if (Find(document, id) is not { } connector)
            return NotFound(id);
        var apiKey = body.ApiKey?.Trim();
        if (string.IsNullOrEmpty(apiKey) || apiKey.Length > 200 || apiKey.Any(char.IsWhiteSpace))
            return Invalid(new() { ["apiKey"] = ["Paste the whole API key, exactly as the ops console showed it."] });
        if (RateLimited(ui, http, caller, "pos-key") is { } limited)
            return limited;

        // Checked at the host's own POSitive address (connectorhost.json, loopback only).
        var check = await ui.Keys.VerifyAsync(apiKey, cancellationToken);
        if (!check.Ok)
        {
            ui.Audit.Record(caller, "pos-key refused", id, check.Error ?? "whoami failed");
            return Problem(StatusCodes.Status422UnprocessableEntity, "api_key_rejected", check.Error ?? "The key could not be checked.");
        }
        var expected = connector.DeveloperId?.Trim();
        if (!string.IsNullOrEmpty(expected) && !string.Equals(expected, check.DeveloperId, StringComparison.OrdinalIgnoreCase)
            && !body.ConfirmDeveloperId)
            return Problem(StatusCodes.Status409Conflict, "developer_id_mismatch",
                $"This key belongs to Developer ID '{check.DeveloperId}', but the connector expects '{expected}'. " +
                "A connector's links live under its Developer ID: use a key made for it, or confirm to switch.",
                new() { ["developerId"] = check.DeveloperId, ["expectedDeveloperId"] = expected });
        if (document.Connectors.FirstOrDefault(c => !ReferenceEquals(c, connector)
                && string.Equals(c.DeveloperId?.Trim(), check.DeveloperId, StringComparison.OrdinalIgnoreCase)) is { } other)
            return Problem(StatusCodes.Status409Conflict, "developer_id_in_use",
                $"Developer ID '{check.DeveloperId}' belongs to connector '{other.Id}'. Two stores must never share one: " +
                "they would link over each other's customers, orders and products.",
                new() { ["developerId"] = check.DeveloperId, ["connectorId"] = other.Id });

        await ui.Config.EditAsync<bool>(current =>
        {
            var index = current.Connectors.FindIndex(c => string.Equals(c.Id, connector.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return (null, false);
            var connectors = current.Connectors.ToList();
            connectors[index] = connectors[index] with { DeveloperId = check.DeveloperId };
            return (current with { Connectors = connectors }, true);
        }, cancellationToken);
        ui.Secrets.Set(ConnectorSecretNames.PosApiKey(connector.Id), apiKey);
        ui.Audit.Record(caller, "pos-key stored", connector.Id,
            $"key checked with /api/whoami: Developer ID {check.DeveloperId}, settings {check.SettingsId?.ToString(CultureInfo.InvariantCulture) ?? "-"}" +
            (string.Equals(expected, check.DeveloperId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(expected)
                ? "" : $" (was {expected}, confirmed)"));
        return Results.Ok(new { ok = true, developerId = check.DeveloperId, settingsId = check.SettingsId });
    }

    internal static async Task<IResult> VerifyPosKeyAsync(string id, ConnectorUi ui, HttpContext http, CancellationToken cancellationToken)
    {
        if (Find(ui.Config.Load(), id) is not { } connector)
            return NotFound(id);
        var key = ui.Secrets.Get(ConnectorSecretNames.PosApiKey(connector.Id));
        if (string.IsNullOrEmpty(key))
            return Results.Ok(new { ok = false, error = "No POSitive API key is stored for this connector yet." });
        if (RateLimited(ui, http, PortalCaller.From(http), "pos-key") is { } limited)
            return limited;
        var check = await ui.Keys.VerifyAsync(key, cancellationToken);
        var matches = check.Ok && string.Equals(check.DeveloperId, connector.DeveloperId?.Trim(), StringComparison.OrdinalIgnoreCase);
        return Results.Ok(new
        {
            ok = check.Ok && matches,
            developerId = check.DeveloperId,
            settingsId = check.SettingsId,
            error = check.Ok && !matches
                ? $"The stored key now answers as Developer ID '{check.DeveloperId}', not '{connector.DeveloperId}'."
                : check.Error,
        });
    }

    internal static async Task<IResult> SetOrderStartAsync(string id, OrderStart body, ConnectorUi ui, HttpContext http,
        CancellationToken cancellationToken)
    {
        var caller = PortalCaller.From(http);
        if (Find(ui.Config.Load(), id) is not { } connector)
            return NotFound(id);
        var cursor = ConnectorWriteRules.OrderCursor(body, ui.Time.GetUtcNow().UtcDateTime, ui.Time.LocalTimeZone, out var error);
        if (error is not null)
            return Invalid(new() { ["orderStart"] = [error] });
        var before = await ui.Stores.Cursors.GetAsync(connector.Id, ConnectorCursorNames.OrdersModifiedAfter, cancellationToken);
        await ui.Stores.Cursors.SetAsync(connector.Id, ConnectorCursorNames.OrdersModifiedAfter, cursor, cancellationToken);
        ui.Audit.Record(caller, "order-start", connector.Id, $"order poll cursor {before ?? "(none)"} -> {cursor}");
        return Results.Ok(new { ordersCursor = cursor });
    }

    internal static async Task<IResult> StatusAsync(string id, ConnectorUi ui, CancellationToken cancellationToken)
    {
        var document = ui.Config.Load();
        if (Find(document, id) is not { } connector)
            return NotFound(id);
        var view = await ViewAsync(ui, document, connector, withStatus: true, cancellationToken);
        var jobs = await ui.Ui.ListJobsAsync(connector.Id, null, 25, cancellationToken);
        // Exceptions only (things needing action); the match review's items are view.status.needsReview.
        var exceptions = await ui.Ui.CountExceptionsByStatusAsync(connector.Id, exceptionsOnly: true, cancellationToken);
        return Results.Ok(new
        {
            connector = view,
            worker = ui.Worker.State,
            exceptions,
            jobs = jobs.Select(JobView),
        });
    }

    // --- match review ------------------------------------------------------------------

    internal static async Task<IResult> ReviewAsync(string id, ConnectorUi ui, CancellationToken cancellationToken)
    {
        if (Find(ui.Config.Load(), id) is not { } connector)
            return NotFound(id);
        var open = await ui.Stores.Exceptions.ListAsync(connector.Id, ConnectorExceptionStatuses.Open, 1000, cancellationToken);
        var jobs = await ui.Ui.ListJobsAsync(connector.Id, ReviewJobs, 10, cancellationToken);
        var lastRun = await ui.Stores.Cursors.GetAsync(connector.Id, ReviewJobKinds.LastRunCursor, cancellationToken);
        var (exceptions, _) = await ui.Ui.CountOpenSplitAsync(connector.Id, cancellationToken);
        var items = open.Where(e => ReviewKinds.Contains(e.Kind)).Select(ReviewItem).ToList();
        var parents = new Dictionary<long, WooItemSummary>();
        if (ui.WooItems is not null && items.Count > 0)
            (items, parents) = await NameFromWooAsync(ui, connector, items, cancellationToken);
        return Results.Ok(new
        {
            linkedItems = await ui.Ui.CountLinkedItemsAsync(connector.Id, cancellationToken),
            active = jobs.Where(j => j.Status is ConnectorJobStatuses.Pending or ConnectorJobStatuses.Running).Select(JobView),
            recentJobs = jobs.Select(JobView),
            lastRun = ParseJson(lastRun),
            needsReview = items.Count,
            // Things needing action live on the Exceptions page; the review only points there.
            openExceptions = exceptions,
            items,
            groups = ReviewGroups(items, parents),
            ignored = await ui.Ui.ListIgnoredAsync(connector.Id, cancellationToken),
        });
    }

    /// <summary>
    /// Bulk Ignore (a whole group of the match review at once): each item is marked ignored and its open
    /// review rows closed. Only the review kinds can be ignored this way.
    /// </summary>
    internal static async Task<IResult> IgnoreManyAsync(string id, IgnoreManyBody body, ConnectorUi ui, HttpContext http,
        CancellationToken cancellationToken)
    {
        var caller = PortalCaller.From(http);
        if (Find(ui.Config.Load(), id) is not { } connector)
            return NotFound(id);
        var items = (body.Items ?? []).Where(i => !string.IsNullOrWhiteSpace(i.ExternalRef))
            .Select(i => (Ref: i.ExternalRef!.Trim(), Kind: string.IsNullOrWhiteSpace(i.Kind) ? CatalogExceptionKinds.UnmatchedPlatform : i.Kind.Trim()))
            .DistinctBy(i => i.Ref, StringComparer.Ordinal).ToList();
        if (items.Count is 0 or > MaxBulk || items.Any(i => i.Ref.Length > 200))
            return Invalid(new() { ["items"] = [$"Choose 1 to {MaxBulk} items (each a Woo id, or inv:<invNo> for a POSitive item)."] });
        if (items.FirstOrDefault(i => !SqliteConnectorUiStore.IgnorableKinds.Contains(i.Kind)) is { Ref: not null } bad)
            return Invalid(new() { ["items"] = [$"'{bad.Ref}' ({bad.Kind}) is not a match review item; only those can be ignored here."] });

        var note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
        var closed = 0;
        var refs = items.Select(i => i.Ref).ToHashSet(StringComparer.Ordinal);
        foreach (var (externalRef, kind) in items)
            await ui.Ui.IgnoreAsync(connector.Id, externalRef, kind, caller.User, note, cancellationToken);
        foreach (var row in (await ui.Stores.Exceptions.ListAsync(connector.Id, ConnectorExceptionStatuses.Open, 1000, cancellationToken))
                 .Where(e => SqliteConnectorUiStore.IgnorableKinds.Contains(e.Kind) && refs.Contains(e.ExternalRef)))
        {
            if (await ui.Stores.Exceptions.CloseAsync(row.ExceptionId, ConnectorExceptionStatuses.Ignored, caller.User, cancellationToken))
                closed++;
        }
        ui.Audit.Record(caller, "ignore", connector.Id,
            $"{items.Count} item(s) ignored at once ({string.Join(", ", items.Take(20).Select(i => i.Ref))}{(items.Count > 20 ? ", …" : "")}); {closed} open review item(s) closed");
        return Results.Ok(new { ignored = items.Count, closed });
    }

    internal static async Task<IResult> UnignoreManyAsync(string id, UnignoreManyBody body, ConnectorUi ui, HttpContext http,
        CancellationToken cancellationToken)
    {
        var caller = PortalCaller.From(http);
        if (Find(ui.Config.Load(), id) is not { } connector)
            return NotFound(id);
        var refs = (body.ExternalRefs ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (refs.Count is 0 or > MaxBulk)
            return Invalid(new() { ["externalRefs"] = [$"Choose 1 to {MaxBulk} ignored items."] });
        var done = 0;
        foreach (var externalRef in refs)
        {
            if (await ui.Ui.UnignoreAsync(connector.Id, externalRef, cancellationToken))
                done++;
        }
        ui.Audit.Record(caller, "unignore", connector.Id,
            $"{done} item(s) no longer ignored ({string.Join(", ", refs.Take(20))}{(refs.Count > 20 ? ", …" : "")}); the next match reviews them again");
        return Results.Ok(new { unignored = done });
    }

    internal static async Task<IResult> RunMatchAsync(string id, MatchBody body, ConnectorUi ui, HttpContext http,
        CancellationToken cancellationToken)
    {
        var caller = PortalCaller.From(http);
        if (Find(ui.Config.Load(), id) is not { } connector)
            return NotFound(id);
        var queued = await ui.Stores.Jobs.EnqueueAsync(new JobRequest(connector.Id, ReviewJobKinds.CatalogMatch, "ui",
            ReviewJobKinds.Serialize(new CatalogMatchPayload(body.CreateMissing))), cancellationToken);
        ui.Audit.Record(caller, "match", connector.Id, $"queued job {queued.JobId} (create missing: {body.CreateMissing}){Coalesced(queued)}");
        return Queued(queued, connector);
    }

    internal static async Task<IResult> CreateInWooAsync(string id, CreateInWooBody body, ConnectorUi ui, HttpContext http,
        CancellationToken cancellationToken)
    {
        var caller = PortalCaller.From(http);
        if (Find(ui.Config.Load(), id) is not { } connector)
            return NotFound(id);
        var invNos = (body.InvNos ?? []).Where(i => i > 0).Distinct().OrderBy(i => i).ToList();
        if (invNos.Count is 0 or > 100)
            return Invalid(new() { ["invNos"] = ["Choose 1 to 100 POSitive items."] });
        var queued = await ui.Stores.Jobs.EnqueueAsync(new JobRequest(connector.Id, ReviewJobKinds.CatalogCreate,
            ResourceKey("inv:", invNos), ReviewJobKinds.Serialize(new CatalogCreatePayload(invNos))), cancellationToken);
        ui.Audit.Record(caller, "create-in-woo", connector.Id, $"queued job {queued.JobId} for items {string.Join(", ", invNos)}{Coalesced(queued)}");
        return Queued(queued, connector);
    }

    /// <summary>
    /// Links a Woo item to a POSitive item (a <c>catalog.link</c> job: PUT /products/{invNo}/link, the
    /// item map, then a push). From an <c>order_unmatched_product</c> exception it also re-imports
    /// that order once the link is made ("Link item → Retry").
    /// </summary>
    internal static async Task<IResult> LinkAsync(string id, LinkBody body, ConnectorUi ui, HttpContext http,
        CancellationToken cancellationToken)
    {
        var caller = PortalCaller.From(http);
        if (Find(ui.Config.Load(), id) is not { } connector)
            return NotFound(id);
        if (body.ExternalId <= 0 || body.InvNo <= 0)
            return Invalid(new() { ["externalId"] = ["Give the Woo product or variation id and the POSitive item (invNo)."] });

        long? retryOrder = null;
        if (body.ExceptionId is { } exceptionId)
        {
            var row = await ui.Stores.Exceptions.GetAsync(exceptionId, cancellationToken);
            if (row is null || !string.Equals(row.ConnectorId, connector.Id, StringComparison.OrdinalIgnoreCase))
                return Problem(StatusCodes.Status404NotFound, "exception_not_found", $"Exception {exceptionId} does not belong to connector '{connector.Id}'.");
            if (row.Kind == OrderExceptionKinds.UnmatchedProduct && row.Status == ConnectorExceptionStatuses.Open
                && long.TryParse(row.ExternalRef, NumberStyles.None, CultureInfo.InvariantCulture, out var orderId))
                retryOrder = orderId;
        }

        // Refused here, at once and in words, when the item map already knows a clash — the page offers
        // "unlink the old one first" (Replace). Clashes only POSitive knows still surface from the job
        // (catalog_link_refused), and the page reads that back when the job is done.
        if (!body.Replace && await LinkConflictAsync(ui, connector, body.ExternalId, body.InvNo, cancellationToken) is { } conflict)
        {
            ui.Audit.Record(caller, "link refused", connector.Id, $"Woo item {body.ExternalId} -> POSitive item {body.InvNo}: {conflict.Message}");
            return Problem(StatusCodes.Status409Conflict, "link_conflict", conflict.Message, new() { ["conflict"] = conflict });
        }

        var queued = await ui.Stores.Jobs.EnqueueAsync(new JobRequest(connector.Id, ReviewJobKinds.CatalogLink,
            "woo:" + body.ExternalId.ToString(CultureInfo.InvariantCulture),
            ReviewJobKinds.Serialize(new CatalogLinkPayload(body.InvNo, body.ExternalId, retryOrder, body.Replace))), cancellationToken);
        ui.Audit.Record(caller, "link", connector.Id,
            $"queued job {queued.JobId}: Woo item {body.ExternalId} -> POSitive item {body.InvNo}" +
            (body.Replace ? " (unlinking what either is linked to now, as confirmed)" : "") +
            (retryOrder is null ? "" : $", then re-import Woo order {retryOrder}") + Coalesced(queued));
        return Queued(queued, connector);
    }

    /// <summary>A link the item map already shows would be refused, in the owner's words, and what is in the way.</summary>
    public sealed record LinkConflictView(
        string Side, string Message, int InvNo, string? Sku, long ExternalId,
        long? LinkedWooId, string? LinkedWooType, long? LinkedWooParentId, string? WooName, string? WooStatus,
        int? LinkedInvNo, string? LinkedSku);

    internal static async Task<LinkConflictView?> LinkConflictAsync(ConnectorUi ui, ConnectorSettings connector, long externalId, int invNo,
        CancellationToken cancellationToken)
    {
        var mine = await ui.Stores.Items.GetByInvNoAsync(connector.Id, invNo, cancellationToken);
        if (mine is not null && mine.ExternalId != externalId && mine.ExternalType != CatalogItemTypes.Missing)
        {
            var woo = ui.WooItems is null ? null : await ui.WooItems.GetAsync(connector, mine.ExternalId, cancellationToken);
            var label = string.IsNullOrWhiteSpace(mine.Sku) ? $"POSitive item {invNo}" : mine.Sku.Trim();
            var what = mine.ExternalType == CatalogItemTypes.Variation
                ? $"Woo variation #{mine.ExternalId} of #{mine.ExternalParentId}"
                : $"Woo product #{mine.ExternalId}";
            var details = new[] { WooStatusText(woo?.Status), string.IsNullOrWhiteSpace(woo?.Name) ? null : $"'{woo.Name.Trim()}'" }
                .Where(s => s is not null).ToList();
            var message = $"{label} is already linked to {what}{(details.Count > 0 ? " (" + string.Join(", ", details) + ")" : "")}. " +
                          $"Link a different POSitive item, or unlink #{mine.ExternalId} first.";
            return new LinkConflictView("pos", message, invNo, mine.Sku, externalId, mine.ExternalId, mine.ExternalType,
                mine.ExternalParentId == 0 ? null : mine.ExternalParentId, woo?.Name, woo?.Status, null, null);
        }

        var other = await ui.Stores.Items.GetByExternalIdAsync(connector.Id, externalId, cancellationToken);
        if (other is not null && other.InvNo != invNo)
        {
            var message = $"Woo item #{externalId} is already linked to POSitive item {other.InvNo}" +
                          (string.IsNullOrWhiteSpace(other.Sku) ? "" : $" ({other.Sku.Trim()})") +
                          $". Leave it, or unlink it from {other.InvNo} first and link it to {invNo}.";
            return new LinkConflictView("woo", message, invNo, null, externalId, null, null, null, null, null, other.InvNo, other.Sku);
        }
        return null;
    }

    /// <summary>Woo's post status as the owner sees it in WordPress.</summary>
    internal static string? WooStatusText(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "publish" => "Published",
        "draft" => "Draft",
        "pending" => "Pending review",
        "private" => "Private",
        "trash" => "In the trash",
        var s => char.ToUpperInvariant(s[0]) + s[1..],
    };

    internal static async Task<IResult> IgnoreAsync(string id, IgnoreBody body, ConnectorUi ui, HttpContext http,
        CancellationToken cancellationToken)
    {
        var caller = PortalCaller.From(http);
        if (Find(ui.Config.Load(), id) is not { } connector)
            return NotFound(id);
        var externalRef = body.ExternalRef?.Trim();
        if (string.IsNullOrEmpty(externalRef) || externalRef.Length > 200)
            return Invalid(new() { ["externalRef"] = ["Which item: the Woo id, or inv:<invNo> for a POSitive item."] });
        var kind = string.IsNullOrWhiteSpace(body.Kind) ? CatalogExceptionKinds.UnmatchedPlatform : body.Kind.Trim();
        if (!SqliteConnectorUiStore.IgnorableKinds.Contains(kind))
            return Invalid(new() { ["kind"] = [$"Only unmatched and ambiguous items can be ignored ({string.Join(", ", SqliteConnectorUiStore.IgnorableKinds)})."] });

        await ui.Ui.IgnoreAsync(connector.Id, externalRef, kind, caller.User, body.Note?.Trim(), cancellationToken);
        var closed = await CloseOpenAsync(ui, connector.Id, externalRef, SqliteConnectorUiStore.IgnorableKinds,
            ConnectorExceptionStatuses.Ignored, caller.User, cancellationToken);
        ui.Audit.Record(caller, "ignore", connector.Id, $"{kind} {externalRef} ignored ({closed} open exception(s) closed)");
        return Results.Ok(new { ignored = externalRef, closed });
    }

    internal static async Task<IResult> UnignoreAsync(string id, IgnoreBody body, ConnectorUi ui, HttpContext http,
        CancellationToken cancellationToken)
    {
        var caller = PortalCaller.From(http);
        if (Find(ui.Config.Load(), id) is not { } connector)
            return NotFound(id);
        var externalRef = body.ExternalRef?.Trim() ?? "";
        if (!await ui.Ui.UnignoreAsync(connector.Id, externalRef, cancellationToken))
            return Problem(StatusCodes.Status404NotFound, "not_ignored", $"'{externalRef}' is not ignored.");
        ui.Audit.Record(caller, "unignore", connector.Id, $"{externalRef} no longer ignored (the next match reviews it again)");
        return Results.Ok(new { unignored = externalRef });
    }

    internal static async Task<IResult> SearchProductsAsync(string id, string? q, ConnectorUi ui, CancellationToken cancellationToken)
    {
        if (Find(ui.Config.Load(), id) is not { } connector)
            return NotFound(id);
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length > 100)
            return Invalid(new() { ["q"] = ["Type an item number, SKU/barcode or part of the description."] });
        if (string.IsNullOrEmpty(ui.Secrets.Get(ConnectorSecretNames.PosApiKey(connector.Id))))
            return Problem(StatusCodes.Status409Conflict, "api_key_missing", "Store the connector's POSitive API key first (connect wizard, step 3).");
        PosProductSearchResult result;
        try
        {
            result = await ui.Search.SearchAsync(connector, q, 25, cancellationToken);
        }
        catch (Exception ex) when (ex is PosApiException or ConnectorJobException or HttpRequestException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            ui.Audit.Failure(ex, $"item search for connector {connector.Id} failed");
            return Problem(StatusCodes.Status502BadGateway, "pos_unavailable",
                "POSitive Anywhere could not be searched just now. Try again; the details are in the connector host log.");
        }
        if (result.AllFailed)
        {
            ui.Audit.Failure(new TimeoutException(string.Join(" ", result.Unavailable.Select(u => u.Message))),
                $"item search '{q.Trim()}' for connector {connector.Id} found nothing it could read");
            return Problem(StatusCodes.Status502BadGateway, "pos_unavailable",
                "POSitive Anywhere could not be searched just now. Try again; the details are in the connector host log.");
        }

        // What each hit is linked to for THIS connector (the item map), so the picker can say so and
        // Choose can ask first instead of failing; the API's own link is the fallback.
        var items = new List<object>();
        foreach (var hit in result.Items)
        {
            var linked = await ui.Stores.Items.GetByInvNoAsync(connector.Id, hit.InvNo, cancellationToken);
            var current = linked is not null && linked.ExternalType != CatalogItemTypes.Missing ? linked : null;
            items.Add(new
            {
                hit.InvNo, hit.Sku, hit.Description, hit.WebProduct, hit.AvailableLive,
                linkedTo = current?.ExternalId.ToString(CultureInfo.InvariantCulture) ?? hit.LinkedTo,
                linkedWooId = current?.ExternalId ?? (long.TryParse(hit.LinkedTo, NumberStyles.None, CultureInfo.InvariantCulture, out var w) ? w : null),
                linkedWooType = current?.ExternalType,
            });
        }
        if (result.Unavailable.Count > 0)
            ui.Audit.Failure(new TimeoutException(string.Join(" ", result.Unavailable.Select(u => u.Message))),
                $"item search '{q.Trim()}' for connector {connector.Id} returned without some lookups");
        return Results.Ok(new { items, unavailable = result.Unavailable });
    }

    internal static async Task<IResult> JobAsync(string id, long jobId, ConnectorUi ui, CancellationToken cancellationToken)
    {
        var job = await ui.Ui.GetJobAsync(jobId, cancellationToken);
        return job is null || !string.Equals(job.ConnectorId, id, StringComparison.OrdinalIgnoreCase)
            ? Problem(StatusCodes.Status404NotFound, "job_not_found", $"Job {jobId} does not belong to connector '{id}' (or was purged).")
            : Results.Ok(JobView(job));
    }

    // --- exceptions -------------------------------------------------------------------

    internal static async Task<IResult> ExceptionsAsync(string id, string? status, string? kind, ConnectorUi ui,
        CancellationToken cancellationToken)
    {
        if (Find(ui.Config.Load(), id) is not { } connector)
            return NotFound(id);
        var filter = (status ?? "open").Trim().ToLowerInvariant();
        if (filter is not ("open" or "closed" or "resolved" or "all"))
            return Invalid(new() { ["status"] = ["status must be open, closed or all."] });
        IEnumerable<ConnectorExceptionRow> rows = filter switch
        {
            "open" => await ui.Stores.Exceptions.ListAsync(connector.Id, ConnectorExceptionStatuses.Open, 500, cancellationToken),
            "closed" or "resolved" => (await ui.Stores.Exceptions.ListAsync(connector.Id, null, 1000, cancellationToken))
                .Where(e => e.Status != ConnectorExceptionStatuses.Open && !ReviewKinds.Contains(e.Kind)).Take(500),
            "all" => await ui.Stores.Exceptions.ListAsync(connector.Id, null, 500, cancellationToken),
            _ => await ui.Stores.Exceptions.ListAsync(connector.Id, null, 500, cancellationToken),
        };
        // The match review's items are not exceptions: they are listed (and counted) on Match review only.
        rows = rows.Where(e => !ReviewKinds.Contains(e.Kind));
        if (!string.IsNullOrWhiteSpace(kind))
            rows = rows.Where(e => string.Equals(e.Kind, kind.Trim(), StringComparison.Ordinal));
        var (_, needsReview) = await ui.Ui.CountOpenSplitAsync(connector.Id, cancellationToken);
        return Results.Ok(new
        {
            counts = await ui.Ui.CountExceptionsByStatusAsync(connector.Id, exceptionsOnly: true, cancellationToken),
            needsReview,
            items = rows.Select(e => ExceptionView(e, includeSnapshot: false)),
        });
    }

    internal static async Task<IResult> ExceptionAsync(string id, long exceptionId, ConnectorUi ui, CancellationToken cancellationToken)
    {
        var row = await ui.Stores.Exceptions.GetAsync(exceptionId, cancellationToken);
        if (row is null || !string.Equals(row.ConnectorId, id, StringComparison.OrdinalIgnoreCase))
            return Problem(StatusCodes.Status404NotFound, "exception_not_found", $"Exception {exceptionId} does not belong to connector '{id}'.");
        ConnectorJobView? job = row.JobId is { } jobId ? await ui.Ui.GetJobAsync(jobId, cancellationToken) : null;
        return Results.Ok(new { exception = ExceptionView(row, includeSnapshot: true), job = job is null ? null : JobView(job) });
    }

    /// <summary>Re-queues the job behind an open exception and closes it as <c>retried</c>; a new failure raises it again.</summary>
    internal static async Task<IResult> RetryExceptionAsync(string id, long exceptionId, ConnectorUi ui, HttpContext http,
        CancellationToken cancellationToken)
    {
        var caller = PortalCaller.From(http);
        var row = await ui.Stores.Exceptions.GetAsync(exceptionId, cancellationToken);
        if (row is null || !string.Equals(row.ConnectorId, id, StringComparison.OrdinalIgnoreCase))
            return Problem(StatusCodes.Status404NotFound, "exception_not_found", $"Exception {exceptionId} does not belong to connector '{id}'.");
        if (row.Status != ConnectorExceptionStatuses.Open)
            return Problem(StatusCodes.Status409Conflict, "exception_not_open", $"Exception {exceptionId} is already {row.Status}.");

        var request = await RetryRequestAsync(ui, row, cancellationToken);
        if (request is null)
            return Problem(StatusCodes.Status400BadRequest, "not_retryable",
                $"There is nothing to re-run for a '{row.Kind}' exception. Fix it in POSitive or WooCommerce, then Ignore it here.");

        var queued = await ui.Stores.Jobs.EnqueueAsync(request, cancellationToken);
        await ui.Stores.Exceptions.CloseAsync(row.ExceptionId, ConnectorExceptionStatuses.Retried, caller.User, cancellationToken);
        ui.Audit.Record(caller, "retry", row.ConnectorId,
            $"exception {row.ExceptionId} ({row.Kind} {row.ExternalRef}): queued {request.Kind} '{request.ResourceKey}' as job {queued.JobId}{Coalesced(queued)}");
        ui.Audit.OrderException(caller, row, $"closed for a retry (job {queued.JobId.ToString(CultureInfo.InvariantCulture)})");
        return Results.Ok(new { retried = row.ExceptionId, jobId = queued.JobId, kind = request.Kind, coalesced = queued.Coalesced });
    }

    internal static async Task<IResult> IgnoreExceptionAsync(string id, long exceptionId, ConnectorUi ui, HttpContext http,
        CancellationToken cancellationToken)
    {
        var caller = PortalCaller.From(http);
        var row = await ui.Stores.Exceptions.GetAsync(exceptionId, cancellationToken);
        if (row is null || !string.Equals(row.ConnectorId, id, StringComparison.OrdinalIgnoreCase))
            return Problem(StatusCodes.Status404NotFound, "exception_not_found", $"Exception {exceptionId} does not belong to connector '{id}'.");
        if (!await ui.Stores.Exceptions.CloseAsync(row.ExceptionId, ConnectorExceptionStatuses.Ignored, caller.User, cancellationToken))
            return Problem(StatusCodes.Status409Conflict, "exception_not_open", $"Exception {exceptionId} is already {row.Status}.");
        // A review item stays quiet from now on; anything else may come back if it happens again.
        var marked = SqliteConnectorUiStore.IgnorableKinds.Contains(row.Kind);
        if (marked)
            await ui.Ui.IgnoreAsync(row.ConnectorId, row.ExternalRef, row.Kind, caller.User, null, cancellationToken);
        // An order changed after import is fixed in POSitive by hand; closing it says "handled", not "ignored".
        var handled = row.Kind == OrderExceptionKinds.ChangedAfterImport;
        var outcome = handled ? "marked as handled" : "ignored";
        ui.Audit.Record(caller, handled ? "handled" : "ignore", row.ConnectorId,
            $"exception {row.ExceptionId} ({row.Kind} {row.ExternalRef}) {outcome}{(marked ? "; item marked ignored for the match review" : "")}");
        ui.Audit.OrderException(caller, row, outcome);
        return Results.Ok(new { ignored = row.ExceptionId, itemIgnored = marked });
    }

    /// <summary>The job that re-does what an exception reports, or null when re-running cannot help.</summary>
    internal static async Task<JobRequest?> RetryRequestAsync(ConnectorUi ui, ConnectorExceptionRow row, CancellationToken cancellationToken)
    {
        var connectorId = row.ConnectorId;
        if (row.Kind.StartsWith("order_", StringComparison.Ordinal))
            return long.TryParse(row.ExternalRef, NumberStyles.None, CultureInfo.InvariantCulture, out var orderId) && orderId > 0
                ? new JobRequest(connectorId, ConnectorJobKinds.OrderImport, row.ExternalRef,
                    ReviewJobKinds.Serialize(new { source = "ui-retry", exceptionId = row.ExceptionId }))
                : null;

        switch (row.Kind)
        {
            case ConnectorExceptionKinds.JobFailed:
                if (row.JobId is not { } jobId || await ui.Ui.GetJobAsync(jobId, cancellationToken) is not { } job)
                    return null;
                return new JobRequest(connectorId, job.Kind, job.ResourceKey, job.Payload);
            case ConnectorExceptionKinds.WebhookUnhealthy:
                return new JobRequest(connectorId, ConnectorJobKinds.WebhookHealth);
            case ConnectorExceptionKinds.PushFailed:
                if (!long.TryParse(row.ExternalRef, NumberStyles.None, CultureInfo.InvariantCulture, out var wooId)
                    || await ui.Stores.Items.GetByExternalIdAsync(connectorId, wooId, cancellationToken) is not { } item)
                    return null;
                return new JobRequest(connectorId, ConnectorJobKinds.CatalogPush, "ui:" + item.InvNo.ToString(CultureInfo.InvariantCulture),
                    ReviewJobKinds.Serialize(new CatalogPushPayload([item.InvNo])));
            case CatalogExceptionKinds.ManualLinkRefused:
                // The same hand link again (it may work now that whatever was in the way is unlinked).
                return long.TryParse(row.ExternalRef, NumberStyles.None, CultureInfo.InvariantCulture, out var refusedWoo) && refusedWoo > 0
                       && int.TryParse(row.PosRef, NumberStyles.None, CultureInfo.InvariantCulture, out var refusedInv) && refusedInv > 0
                    ? new JobRequest(connectorId, ReviewJobKinds.CatalogLink, "woo:" + row.ExternalRef,
                        ReviewJobKinds.Serialize(new CatalogLinkPayload(refusedInv, refusedWoo)))
                    : null;
            case CatalogExceptionKinds.CreateFailed:
                return InvNoOf(row.ExternalRef) is { } invNo
                    ? new JobRequest(connectorId, ReviewJobKinds.CatalogCreate, ResourceKey("inv:", [invNo]),
                        ReviewJobKinds.Serialize(new CatalogCreatePayload([invNo])))
                    : null;
            case CatalogExceptionKinds.UnmatchedPlatform:
            case CatalogExceptionKinds.UnmatchedPos:
            case CatalogExceptionKinds.Ambiguous:
            case CatalogExceptionKinds.LinkConflict:
            case CatalogExceptionKinds.PlatformMissing:
                // Re-check the whole match (links what now matches; nothing new is created).
                return new JobRequest(connectorId, ReviewJobKinds.CatalogMatch, "ui",
                    ReviewJobKinds.Serialize(new CatalogMatchPayload(CreateMissing: false)));
            default:
                return null;
        }
    }

    // --- views and helpers ----------------------------------------------------------------

    internal static async Task<ConnectorView> ViewAsync(ConnectorUi ui, ConnectorsDocument document, ConnectorSettings connector,
        bool withStatus, CancellationToken cancellationToken)
    {
        var problems = connector.Enabled ? NotReady(ui, document, connector) : [];
        var secrets = ui.Secrets.List().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var status = withStatus ? await StatusViewAsync(ui, connector, cancellationToken) : null;
        var pending = await ui.Stores.Cursors.GetAsync(connector.Id, ConnectorSetup.PendingCursor, cancellationToken);
        return new ConnectorView(
            connector.Id,
            connector.Platform,
            connector.Enabled,
            ui.Worker.RunningConnectorIds.Contains(connector.Id, StringComparer.OrdinalIgnoreCase),
            problems,
            connector.StoreUrl,
            connector.DeveloperId,
            connector.WebhookBaseUrl,
            connector.PriceLevel,
            connector.StockBuffer,
            connector.PublishNewAs,
            connector.OverwriteTitleDescription,
            connector.ImportOnHold,
            connector.PayOnCompletionMethods,
            connector.PaymentTenders ?? [],
            connector.PushIntervalSeconds,
            connector.OrderPollMinutes,
            new SecretPresence(
                secrets.Contains(ConnectorSecretNames.WooConsumerKey(connector.Id)),
                secrets.Contains(ConnectorSecretNames.WooConsumerSecret(connector.Id)),
                secrets.Contains(ConnectorSecretNames.WooWebhookSecret(connector.Id)),
                secrets.Contains(ConnectorSecretNames.PosApiKey(connector.Id))),
            status,
            Setup(connector, secrets, problems, PendingSteps(pending), status?.Webhooks.UnhealthyOpen ?? 0));
    }

    internal static IReadOnlySet<string> PendingSteps(string? cursor) =>
        (cursor ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The connect wizard's rail for <paramref name="connector"/>: each step done, to do, or needing
    /// attention (a step that was done but no longer is on an enabled connector, or invalid settings).
    /// </summary>
    internal static SetupView Setup(ConnectorSettings connector, IReadOnlySet<string> secrets, IReadOnlyList<string> problems,
        IReadOnlySet<string> pending, int unhealthyWebhooks)
    {
        // Missing on an enabled connector = something broke; on one being set up = not done yet.
        var missing = connector.Enabled ? ConnectorSetup.Attention : ConnectorSetup.Todo;
        var invalid = ConnectorConfigFile.Validate(connector);
        string[] Invalid(params string[] fields) => invalid.Where(m => fields.Contains(m.Split(' ', 2)[0], StringComparer.Ordinal))
            .Select(m => char.ToUpperInvariant(m[0]) + m[1..]).ToArray();

        SetupStepView Store()
        {
            var bad = Invalid("storeUrl");
            if (string.IsNullOrWhiteSpace(connector.StoreUrl) || bad.Length > 0)
                return new(1, ConnectorSetup.Store, "Store connection", ConnectorSetup.Attention,
                    bad.Length > 0 ? string.Join(" ", bad) : "No store address.");
            var keys = secrets.Contains(ConnectorSecretNames.WooConsumerKey(connector.Id)) && secrets.Contains(ConnectorSecretNames.WooConsumerSecret(connector.Id));
            return keys
                ? new(1, ConnectorSetup.Store, "Store connection", ConnectorSetup.Done, $"{ConnectorWriteRules.ForLog(connector.StoreUrl).TrimEnd('/')}, WooCommerce keys stored.")
                : new(1, ConnectorSetup.Store, "Store connection", missing, "The WooCommerce consumer key and secret are not stored.");
        }

        SetupStepView Settings()
        {
            var bad = Invalid("priceLevel", "stockBuffer", "publishNewAs", "pushIntervalSeconds", "orderPollMinutes");
            if (bad.Length > 0)
                return new(2, ConnectorSetup.Settings, "Settings", ConnectorSetup.Attention, string.Join(" ", bad));
            return pending.Contains(ConnectorSetup.Settings)
                ? new(2, ConnectorSetup.Settings, "Settings", ConnectorSetup.Todo, "Check the defaults and save them.")
                : new(2, ConnectorSetup.Settings, "Settings", ConnectorSetup.Done,
                    $"Price level {connector.PriceLevel}, buffer {connector.StockBuffer.ToString("0.##", CultureInfo.InvariantCulture)}, new products as {connector.PublishNewAs}.");
        }

        SetupStepView Key() => secrets.Contains(ConnectorSecretNames.PosApiKey(connector.Id))
            ? new(3, ConnectorSetup.Key, "POSitive API key", ConnectorSetup.Done, $"Stored for Developer ID {connector.DeveloperId ?? ConnectorWriteRules.DefaultDeveloperId(connector.Id)}.")
            : new(3, ConnectorSetup.Key, "POSitive API key", missing, "No POSitive API key is stored for this connector yet.");

        SetupStepView Orders()
        {
            if (pending.Contains(ConnectorSetup.Orders))
                return new(4, ConnectorSetup.Orders, "Orders & webhooks", ConnectorSetup.Todo, "Choose how orders arrive and where the import starts.");
            if (unhealthyWebhooks > 0)
                return new(4, ConnectorSetup.Orders, "Orders & webhooks", ConnectorSetup.Attention, "The webhooks need attention (see Exceptions).");
            return new(4, ConnectorSetup.Orders, "Orders & webhooks", ConnectorSetup.Done, string.IsNullOrWhiteSpace(connector.WebhookBaseUrl)
                ? $"Polling only, every {connector.OrderPollMinutes} min."
                : $"Webhooks at {connector.WebhookBaseUrl}, with a {connector.OrderPollMinutes}-minute backstop poll.");
        }

        SetupStepView Enable() => !connector.Enabled
            ? new(5, ConnectorSetup.Enable, "Enable", ConnectorSetup.Todo, "Not enabled yet.")
            : problems.Count > 0
                ? new(5, ConnectorSetup.Enable, "Enable", ConnectorSetup.Attention, "Enabled but it cannot run: " + string.Join(" ", problems))
                : new(5, ConnectorSetup.Enable, "Enable", ConnectorSetup.Done, "Enabled.");

        var steps = new[] { Store(), Settings(), Key(), Orders(), Enable() };
        return new SetupView(steps, steps.FirstOrDefault(s => s.State != ConnectorSetup.Done)?.Key);
    }

    private static async Task ConfirmStepAsync(ConnectorUi ui, string connectorId, string step, CancellationToken cancellationToken)
    {
        var pending = PendingSteps(await ui.Stores.Cursors.GetAsync(connectorId, ConnectorSetup.PendingCursor, cancellationToken));
        if (pending.Contains(step))
            await ui.Stores.Cursors.SetAsync(connectorId, ConnectorSetup.PendingCursor,
                string.Join(",", pending.Where(p => p != step).Order(StringComparer.Ordinal)), cancellationToken);
    }

    private static async Task<ConnectorStatusView> StatusViewAsync(ConnectorUi ui, ConnectorSettings connector, CancellationToken cancellationToken)
    {
        var id = connector.Id;
        var jobs = ui.Stores.Jobs;
        var (holdOrders, holdItems) = await ui.Ui.CountStockHoldsAsync(id, cancellationToken);
        var unhealthy = (await ui.Stores.Exceptions.ListAsync(id, ConnectorExceptionStatuses.Open, 1000, cancellationToken))
            .Count(e => e.Kind == ConnectorExceptionKinds.WebhookUnhealthy);
        var managed = !string.IsNullOrWhiteSpace(connector.WebhookBaseUrl);
        var (exceptions, needsReview) = await ui.Ui.CountOpenSplitAsync(id, cancellationToken);
        var catalogCursor = await ui.Stores.Cursors.GetAsync(id, ConnectorCursorNames.PosModifiedSince, cancellationToken);
        return new ConnectorStatusView(
            await jobs.CountOpenAsync(id, cancellationToken),
            exceptions,
            await jobs.LastCompletedAtAsync(id, ConnectorJobKinds.CatalogPush, cancellationToken),
            await jobs.LastCompletedAtAsync(id, ConnectorJobKinds.OrderImport, cancellationToken),
            await jobs.LastCompletedAtAsync(id, ConnectorJobKinds.OrderPoll, cancellationToken),
            await jobs.LastCompletedAtAsync(id, ConnectorJobKinds.WebhookHealth, cancellationToken),
            holdOrders,
            holdItems,
            await ui.Ui.CountLinkedItemsAsync(id, cancellationToken),
            new WebhookStatusView(managed ? "webhooks" : "polling",
                managed ? $"{connector.WebhookBaseUrl!.TrimEnd('/')}/hooks/woocommerce/{id}" : null, unhealthy),
            await ui.Stores.Cursors.GetAsync(id, ConnectorCursorNames.OrdersModifiedAfter, cancellationToken),
            catalogCursor,
            needsReview,
            await MatchProgressAsync(ui, id, catalogCursor, cancellationToken));
    }

    /// <summary>
    /// Has a match run (Run the match, or the first push after Enable — which matches everything), is one
    /// queued or running now, and what the last one found.
    /// </summary>
    private static async Task<MatchProgressView> MatchProgressAsync(ConnectorUi ui, string id, string? catalogCursor,
        CancellationToken cancellationToken)
    {
        var lastRun = ParseJson(await ui.Stores.Cursors.GetAsync(id, ReviewJobKinds.LastRunCursor, cancellationToken));
        DateTime? lastRunAt = null;
        JsonElement? summary = null;
        if (lastRun is { ValueKind: JsonValueKind.Object } run)
        {
            if (run.TryGetProperty("completedAt", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTime(out var when))
                lastRunAt = DateTime.SpecifyKind(when.ToUniversalTime(), DateTimeKind.Utc);
            if (run.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.Object)
                summary = s.Clone();
        }

        // Before the first push has matched (no change-feed cursor yet), a waiting catalog.push IS the first match.
        var kinds = catalogCursor is null ? new[] { ReviewJobKinds.CatalogMatch, ConnectorJobKinds.CatalogPush } : [ReviewJobKinds.CatalogMatch];
        var active = (await ui.Ui.ListJobsAsync(id, kinds, 10, cancellationToken))
            .Where(j => j.Status is ConnectorJobStatuses.Pending or ConnectorJobStatuses.Running)
            .OrderBy(j => j.Status == ConnectorJobStatuses.Running ? 0 : 1).ThenBy(j => j.JobId)
            .FirstOrDefault();
        return new MatchProgressView(catalogCursor is not null || lastRunAt is not null, lastRunAt, active?.JobId, active?.Status, summary);
    }

    /// <summary>Why an (enabled) connector would not run: invalid settings, shared ids, missing secrets.</summary>
    private static List<string> NotReady(ConnectorUi ui, ConnectorsDocument document, ConnectorSettings connector)
    {
        var problems = new List<string>();
        ConnectorConfigFile.Runnable(document with { Connectors = document.Connectors.Select(c =>
                ReferenceEquals(c, connector) || string.Equals(c.Id, connector.Id, StringComparison.OrdinalIgnoreCase)
                    ? c with { Enabled = true } : c).ToList() },
            (id, errors) =>
            {
                if (string.Equals(id, connector.Id, StringComparison.OrdinalIgnoreCase))
                    problems.AddRange(errors);
            });
        if (string.IsNullOrEmpty(ui.Secrets.Get(ConnectorSecretNames.WooConsumerKey(connector.Id)))
            || string.IsNullOrEmpty(ui.Secrets.Get(ConnectorSecretNames.WooConsumerSecret(connector.Id))))
            problems.Add("The WooCommerce consumer key and secret are not stored.");
        if (string.IsNullOrEmpty(ui.Secrets.Get(ConnectorSecretNames.PosApiKey(connector.Id))))
            problems.Add("The connector's POSitive API key is not stored.");
        return problems;
    }

    private static bool StoreWooKeys(ConnectorUi ui, string connectorId, ConnectorWrite write)
    {
        var stored = false;
        if (!string.IsNullOrWhiteSpace(write.ConsumerKey))
        {
            ui.Secrets.Set(ConnectorSecretNames.WooConsumerKey(connectorId), write.ConsumerKey.Trim());
            stored = true;
        }
        if (!string.IsNullOrWhiteSpace(write.ConsumerSecret))
        {
            ui.Secrets.Set(ConnectorSecretNames.WooConsumerSecret(connectorId), write.ConsumerSecret.Trim());
            stored = true;
        }
        return stored;
    }

    private static async Task<int> CloseOpenAsync(ConnectorUi ui, string connectorId, string externalRef, IReadOnlySet<string> kinds,
        string status, string by, CancellationToken cancellationToken)
    {
        var closed = 0;
        foreach (var row in (await ui.Stores.Exceptions.ListAsync(connectorId, ConnectorExceptionStatuses.Open, 1000, cancellationToken))
                 .Where(e => kinds.Contains(e.Kind) && e.ExternalRef == externalRef))
        {
            if (await ui.Stores.Exceptions.CloseAsync(row.ExceptionId, status, by, cancellationToken))
                closed++;
        }
        return closed;
    }

    private static ConnectorSettings? Find(ConnectorsDocument document, string id) =>
        document.Connectors.FirstOrDefault(c => string.Equals(c.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static object ExceptionView(ConnectorExceptionRow e, bool includeSnapshot)
    {
        var wooRef = long.TryParse(e.ExternalRef, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (long?)null;
        var invNo = InvNoOf(e.ExternalRef) ?? (int.TryParse(e.PosRef, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null);
        var catalog = e.Kind.StartsWith("catalog_", StringComparison.Ordinal);
        return new
        {
            exceptionId = e.ExceptionId,
            e.Kind,
            e.ExternalRef,
            e.PosRef,
            e.Reason,
            e.Status,
            e.Occurrences,
            e.JobId,
            e.CreatedAt,
            e.UpdatedAt,
            e.ResolvedBy,
            e.ResolvedAt,
            hasSnapshot = e.Snapshot is not null,
            snapshot = includeSnapshot ? e.Snapshot : null,
            // What the page offers for it (the server checks again on each action).
            wooId = catalog || e.Kind == ConnectorExceptionKinds.PushFailed ? wooRef : null,
            invNo = catalog ? invNo : null,
            canLink = e.Status == ConnectorExceptionStatuses.Open
                      && ((wooRef is not null && e.Kind is CatalogExceptionKinds.UnmatchedPlatform or CatalogExceptionKinds.Ambiguous
                              or CatalogExceptionKinds.LinkConflict or CatalogExceptionKinds.ManualLinkRefused)
                          || e.Kind == OrderExceptionKinds.UnmatchedProduct),
            canCreate = e.Status == ConnectorExceptionStatuses.Open && InvNoOf(e.ExternalRef) is not null
                        && e.Kind is CatalogExceptionKinds.UnmatchedPos or CatalogExceptionKinds.CreateFailed or CatalogExceptionKinds.Ambiguous,
            canIgnoreItem = SqliteConnectorUiStore.IgnorableKinds.Contains(e.Kind),
            // What fixed it (stored when the connector resolved it); the page leads with this once closed.
            resolution = e.Resolution,
            review = ReviewKinds.Contains(e.Kind),
        };
    }

    // --- match review grouping ------------------------------------------------------------

    /// <summary>
    /// A match review row: the exception plus what the review groups and names it by. <see cref="Label"/> =
    /// what the row shows first (a variation's attribute values, e.g. "Red"; a product's name).
    /// </summary>
    public sealed record ReviewItemView(
        long ExceptionId, string Kind, string ExternalRef, string? PosRef, string Reason, int Occurrences, DateTime UpdatedAt,
        long? WooId, int? InvNo, long? ParentId, string? Name, string? Sku, string? WooStatus, string Group, bool CanLink, bool CanCreate,
        string? Label = null, string? WooType = null);

    /// <summary>
    /// One block of the match review: its items share one explanation (shown once). A variable product's
    /// block also names the product (<see cref="ParentName"/>) and how many variations it has.
    /// </summary>
    public sealed record ReviewGroupView(string Key, string Kind, string Title, string Explanation, int Count, long? ParentId,
        string? ParentName = null, int? VariationCount = null);

    private static readonly System.Text.RegularExpressions.Regex VariationOf =
        new(@"variation #(?<id>\d+) of #(?<parent>\d+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>"… has SKU 'woo-vneck-tee-red' …" / "… SKU or barcode 'X' …" in a match reason.</summary>
    private static readonly System.Text.RegularExpressions.Regex SkuInReason =
        new(@"SKU(?: or barcode)? '(?<sku>[^']{1,100})'", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>"POSitive item 4019 (MX16) …" in a match reason.</summary>
    private static readonly System.Text.RegularExpressions.Regex PosSkuInReason =
        new(@"^POSitive item \d+ \((?<sku>[^)]{1,100})\)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static ReviewItemView ReviewItem(ConnectorExceptionRow e)
    {
        var wooId = long.TryParse(e.ExternalRef, NumberStyles.None, CultureInfo.InvariantCulture, out var w) ? w : (long?)null;
        var invNo = InvNoOf(e.ExternalRef) ?? (int.TryParse(e.PosRef, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null);
        long? parentId = null;
        string? name = null, sku = null, status = null, group = null, type = null;
        if (ParseJson(e.Snapshot) is { ValueKind: JsonValueKind.Object } snap)
        {
            parentId = snap.TryGetProperty("parentId", out var pid) && pid.TryGetInt64(out var pv) && pv > 0 ? pv : null;
            name = Text(snap, "name");
            sku = Text(snap, "sku");
            status = Text(snap, "status");
            group = Text(snap, "group");
            type = Text(snap, "type");
        }
        // Rows raised before the snapshot carried this: the reason says "variation #X of #Y" / "a matrix item" / the SKU.
        if (parentId is null && wooId is not null && VariationOf.Match(e.Reason) is { Success: true } m
            && m.Groups["id"].Value == e.ExternalRef)
        {
            parentId = long.Parse(m.Groups["parent"].Value, CultureInfo.InvariantCulture);
            type ??= CatalogItemTypes.Variation;
        }
        sku ??= (wooId is not null ? SkuInReason.Match(e.Reason) : PosSkuInReason.Match(e.Reason)) is { Success: true } s
            ? s.Groups["sku"].Value.Trim() : null;
        if (group is null && e.Kind == CatalogExceptionKinds.UnmatchedPos)
            group = e.Reason.Contains("is a matrix item", StringComparison.Ordinal) ? "matrix"
                : e.Reason.Contains("has no SKU", StringComparison.Ordinal) ? "no_sku" : null;

        var open = e.Status == ConnectorExceptionStatuses.Open;
        return new ReviewItemView(e.ExceptionId, e.Kind, e.ExternalRef, e.PosRef, e.Reason, e.Occurrences, e.UpdatedAt,
            wooId, invNo, parentId, name, sku, WooStatusText(status), GroupKey(e.Kind, parentId, group),
            CanLink: open && wooId is not null && e.Kind is CatalogExceptionKinds.UnmatchedPlatform or CatalogExceptionKinds.Ambiguous or CatalogExceptionKinds.LinkConflict,
            CanCreate: open && InvNoOf(e.ExternalRef) is not null && e.Kind is CatalogExceptionKinds.UnmatchedPos or CatalogExceptionKinds.Ambiguous
                       && group != "matrix",
            Label: name, WooType: type);
    }

    private static string GroupKey(string kind, long? parentId, string? group) => kind switch
    {
        CatalogExceptionKinds.UnmatchedPlatform when parentId is { } parent => "parent:" + parent.ToString(CultureInfo.InvariantCulture),
        CatalogExceptionKinds.UnmatchedPlatform => "woo",
        CatalogExceptionKinds.UnmatchedPos => group is "matrix" or "no_sku" ? group : "pos",
        CatalogExceptionKinds.Ambiguous => "ambiguous",
        _ => "conflict",
    };

    /// <summary>
    /// Names the Woo rows from Woo itself where the row does not carry it (rows raised before the
    /// snapshot did: no parent, no name — "Woo #24"), and reads the variable products' own names:
    /// one batched lookup per page load at most (<see cref="WooReviewNames"/>, cached), never per row.
    /// Whatever Woo does not answer keeps what the row had.
    /// </summary>
    internal static async Task<(List<ReviewItemView> Items, Dictionary<long, WooItemSummary> Parents)> NameFromWooAsync(
        ConnectorUi ui, ConnectorSettings connector, List<ReviewItemView> items, CancellationToken cancellationToken)
    {
        var woo = items.Where(i => i.WooId is not null).ToList();
        var unnamed = woo.Where(i => i.Name is null || (i.ParentId is null && i.WooType is null)).ToList();
        var ids = unnamed.Select(i => i.WooId!.Value).Concat(woo.Where(i => i.ParentId is not null).Select(i => i.ParentId!.Value)).ToList();
        // A variation is not answered by products?include=; its SKU finds it (and its parent).
        var skus = unnamed.Where(i => i.ParentId is null && i.Sku is not null).Select(i => i.Sku!).ToList();
        if (ids.Count == 0 && skus.Count == 0)
            return (items, []);

        var (byId, _) = await ui.ReviewNames.GetAsync(ui.WooItems!, connector, ids, skus, ui.Time, cancellationToken);
        var named = items.Select(i =>
        {
            if (i.WooId is not { } id || !byId.TryGetValue(id, out var found))
                return i;
            var parentId = i.ParentId ?? (found.ParentId > 0 ? found.ParentId : null);
            var variation = parentId is not null;
            return i with
            {
                ParentId = parentId,
                Name = i.Name ?? found.Name,
                Label = i.Label ?? (variation ? found.Option ?? found.Name : found.Name),
                Sku = i.Sku ?? found.Sku,
                WooStatus = i.WooStatus ?? WooStatusText(found.Status),
                WooType = i.WooType ?? (variation ? CatalogItemTypes.Variation : found.Type),
                Group = i.Kind == CatalogExceptionKinds.UnmatchedPlatform ? GroupKey(i.Kind, parentId, null) : i.Group,
            };
        }).ToList();
        var parents = named.Where(i => i.ParentId is not null).Select(i => i.ParentId!.Value).Distinct()
            .Where(byId.ContainsKey).ToDictionary(p => p, p => byId[p]);
        return (named, parents);
    }

    /// <summary>The review's blocks in page order, each explained once (matrix items, a product's variations, …).</summary>
    internal static List<ReviewGroupView> ReviewGroups(IReadOnlyList<ReviewItemView> items,
        IReadOnlyDictionary<long, WooItemSummary>? parents = null)
    {
        var order = new[] { "woo", "parent:", "ambiguous", "conflict", "pos", "matrix", "no_sku" };
        return items.GroupBy(i => i.Group)
            .Select(g =>
            {
                var first = g.First();
                var count = g.Count();
                var parent = first.ParentId is { } pid && parents is not null ? parents.GetValueOrDefault(pid) : null;
                var variations = parent is { VariationCount: > 0 } ? parent.VariationCount : (int?)null;
                var (title, explain) = g.Key switch
                {
                    "woo" => ("Woo products with no POSitive match",
                        "Normal, not an error: these stay in Woo as they are, and the connector does not change their price or stock. " +
                        "Link one to a POSitive item to manage it, or ignore the ones you only sell online."),
                    "ambiguous" => ("Same SKU on more than one item",
                        "One SKU matches more than one item, so nothing on it was linked. Link the right one by hand, or fix the duplicate SKU."),
                    "conflict" => ("Links POSitive refused during the match",
                        "The POSitive item or the Woo product is already linked to something else. Link it by hand, or ignore it."),
                    "pos" => ("POSitive web items not created in Woo",
                        "Their SKU is already used in Woo by a product the connector does not link (for example a variable product). Fix the SKU on one side, or create it in Woo."),
                    "matrix" => ("POSitive matrix items",
                        "The connector links existing Woo variations by SKU but does not create variable products. Create the product in Woo with the same SKUs, then run the match again."),
                    "no_sku" => ("POSitive web items without a SKU",
                        "Without a SKU (or barcode) nothing ties them to a Woo product. Give them one in POSitive, or create and link them by hand."),
                    _ => (ParentTitle(first.ParentId, parent?.Name, variations ?? count, count),
                        "Each variation links to its own POSitive item by SKU. Link them one by one, or ignore the whole product."),
                };
                return new ReviewGroupView(g.Key, first.Kind, title, explain, count, first.ParentId,
                    string.IsNullOrWhiteSpace(parent?.Name) ? null : parent.Name.Trim(), variations);
            })
            .OrderBy(g => Array.FindIndex(order, o => g.Key == o || (o.EndsWith(':') && g.Key.StartsWith(o, StringComparison.Ordinal))))
            .ThenBy(g => g.ParentId)
            .ToList();
    }

    /// <summary>"V-Neck T-Shirt (Woo #11, variable, 3 variations, none matched)"; without a name, "Variations of Woo product #11".</summary>
    internal static string ParentTitle(long? parentId, string? parentName, int variations, int toReview)
    {
        if (string.IsNullOrWhiteSpace(parentName))
            return $"Variations of Woo product #{parentId}";
        var state = toReview >= variations ? "none matched" : $"{toReview} to review";
        return $"{parentName.Trim()} (Woo #{parentId}, variable, {variations} variation{(variations == 1 ? "" : "s")}, {state})";
    }

    private static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    private static object JobView(ConnectorJobView j) => new
    {
        j.JobId, j.Kind, j.ResourceKey, j.Status, j.Attempts, j.NextAttemptAt, j.LastError, j.FailedPermanently,
        j.CreatedAt, j.UpdatedAt, j.CompletedAt,
    };

    private static int? InvNoOf(string externalRef) =>
        externalRef.StartsWith("inv:", StringComparison.Ordinal)
        && int.TryParse(externalRef.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var invNo) ? invNo : null;

    private static string ResourceKey(string prefix, IReadOnlyList<int> invNos)
    {
        var key = prefix + string.Join(",", invNos.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        return key.Length <= 200 ? key : prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..40];
    }

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the page says about a failed test: fixed sentences, plus at most the HTTP status and
    /// Woo's own error code / short message — never a response body or a transport exception text.
    /// </summary>
    private static string Explain(WooApiException ex) => ex.Status switch
    {
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
            "WooCommerce refused the consumer key/secret. Create a Read/Write key under WooCommerce → Settings → Advanced → REST API and paste both parts.",
        System.Net.HttpStatusCode.NotFound =>
            "That address answered, but not as a WooCommerce store (no wc/v3 REST API). Check the address and that WooCommerce is active.",
        null when IsBlocked(ex) => BlockedAddress,
        null when ex.Code == "timeout" => "The store did not answer in time. Check the address and that the site is up.",
        null when ex.Code == "invalid_json" => "That address answered, but not with WooCommerce's JSON. Check the address.",
        null => Unreachable,
        { } status when ex.Code == "invalid_json" =>
            $"That address answered HTTP {(int)status}, but not with WooCommerce's JSON. Check the address.",
        { } status => $"WooCommerce answered HTTP {(int)status}"
                      + (ex.Code is null ? "" : $" ({ex.Code})")
                      + (ex.WooMessage is null ? "." : $": {ex.WooMessage}"),
    };

    private static string Describe(ConnectorSettings before, ConnectorSettings after)
    {
        var changes = new List<string>();
        void Diff<T>(string name, T a, T b)
        {
            if (!EqualityComparer<T>.Default.Equals(a, b))
                changes.Add($"{name} {a} -> {b}");
        }
        Diff("storeUrl", ConnectorWriteRules.ForLog(before.StoreUrl), ConnectorWriteRules.ForLog(after.StoreUrl));
        Diff("webhookBaseUrl", before.WebhookBaseUrl ?? "(none)", after.WebhookBaseUrl ?? "(none)");
        Diff("priceLevel", before.PriceLevel, after.PriceLevel);
        Diff("stockBuffer", before.StockBuffer, after.StockBuffer);
        Diff("publishNewAs", before.PublishNewAs, after.PublishNewAs);
        Diff("overwriteTitleDescription", before.OverwriteTitleDescription, after.OverwriteTitleDescription);
        Diff("importOnHold", before.ImportOnHold, after.ImportOnHold);
        Diff("payOnCompletionMethods", string.Join(",", before.PayOnCompletionMethods), string.Join(",", after.PayOnCompletionMethods));
        static string Tenders(ConnectorSettings s) => (s.PaymentTenders ?? []).Count == 0
            ? "(all card)"
            : string.Join(",", (s.PaymentTenders ?? []).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value));
        Diff("paymentTenders", Tenders(before), Tenders(after));
        Diff("pushIntervalSeconds", before.PushIntervalSeconds, after.PushIntervalSeconds);
        Diff("orderPollMinutes", before.OrderPollMinutes, after.OrderPollMinutes);
        return changes.Count == 0 ? "no setting changed" : string.Join("; ", changes);
    }

    private static string Coalesced(EnqueueResult queued) => queued.Coalesced ? " (joined a job already waiting)" : "";

    private static IResult Queued(EnqueueResult queued, ConnectorSettings connector) => Results.Accepted(
        $"{ConnectorUiGate.ApiPrefix}/connectors/{connector.Id}/jobs/{queued.JobId}",
        new
        {
            jobId = queued.JobId,
            coalesced = queued.Coalesced,
            note = connector.Enabled ? null : "The connector is disabled: the job runs once it is enabled.",
        });

    private static IResult NotFound(string id) =>
        Problem(StatusCodes.Status404NotFound, "connector_not_found", $"No connector '{id}' is configured.");

    private static IResult Invalid(Dictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, title: "Connector settings invalid",
            detail: "One or more fields failed validation — see the field-level messages.",
            extensions: new Dictionary<string, object?> { ["errorCode"] = "validation_failed" });

    private static IResult Problem(int status, string errorCode, string detail, Dictionary<string, object?>? extra = null)
    {
        var extensions = new Dictionary<string, object?>(extra ?? []) { ["errorCode"] = errorCode };
        return Results.Problem(statusCode: status, detail: detail, extensions: extensions);
    }
}
