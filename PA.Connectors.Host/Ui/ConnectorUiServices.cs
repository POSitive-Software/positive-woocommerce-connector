using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PA.Connectors.Core;
using PA.Connectors.Core.Pos;
using PA.Connectors.WooCommerce;

namespace PA.Connectors.Host.Ui;

/// <summary>
/// connectors.json edits from the UI: one at a time (in-process gate), read-modify-write, saved
/// atomically by <see cref="ConnectorConfigFile.Save"/> (temp file + move). The worker notices the
/// new file (write time + length) on its next tick, so nothing needs a restart.
/// </summary>
public sealed class ConnectorConfigEditor
{
    private readonly IConnectorConfigSource _source;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConnectorConfigEditor(IConnectorConfigSource source)
    {
        _source = source;
    }

    public string Path => _source.Path;

    public ConnectorsDocument Load() => _source.Load();

    /// <summary>
    /// Runs <paramref name="edit"/> on the current document under the gate. When it returns a
    /// document, that document is saved; the result is returned either way.
    /// </summary>
    public async Task<T> EditAsync<T>(Func<ConnectorsDocument, (ConnectorsDocument? Save, T Result)> edit,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (save, result) = edit(_source.Load());
            if (save is not null)
                ConnectorConfigFile.Save(_source.Path, save);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>The wizard's "Test connection" (a seam so tests need no store).</summary>
public interface IWooConnectionTester
{
    /// <summary>Throws <see cref="WooApiException"/> (or a transport exception) when the store or keys are wrong.</summary>
    Task<WooStoreInfo> TestAsync(Uri storeUrl, string consumerKey, string consumerSecret, CancellationToken cancellationToken);
}

/// <summary>
/// A one-off <see cref="WooClient"/> with the entered keys: no retries and a short timeout, so a
/// wrong URL answers in seconds instead of the job client's minutes of backoff.
/// </summary>
public sealed class WooConnectionTester : IWooConnectionTester
{
    private readonly IHttpClientFactory _httpClients;
    private readonly ILoggerFactory _loggerFactory;

    public WooConnectionTester(IHttpClientFactory httpClients, ILoggerFactory loggerFactory)
    {
        _httpClients = httpClients;
        _loggerFactory = loggerFactory;
    }

    public Task<WooStoreInfo> TestAsync(Uri storeUrl, string consumerKey, string consumerSecret, CancellationToken cancellationToken)
    {
        var client = new WooClient(
            _httpClients.CreateClient(WooConnectorFactory.HttpClientName),
            new WooClientOptions
            {
                StoreUrl = storeUrl,
                ConsumerKey = consumerKey,
                ConsumerSecret = consumerSecret,
                RequestTimeout = TimeSpan.FromSeconds(15),
                MaxRetries = 0,
            },
            _loggerFactory.CreateLogger<WooClient>());
        return WooStoreInfo.ReadAsync(client, cancellationToken);
    }
}

/// <summary>What GET /api/whoami said about a POSitive API key.</summary>
public sealed record PosKeyCheck(bool Ok, string? DeveloperId, int? SettingsId, string? Error);

/// <summary>
/// Checks a connector's POSitive API key with GET /api/whoami (design §5, step 0) — always at the
/// host's own POSitive address (<see cref="PosApiEndpoint"/>, loopback only), never one a page names.
/// </summary>
public interface IPosKeyVerifier
{
    Task<PosKeyCheck> VerifyAsync(string apiKey, CancellationToken cancellationToken);
}

public sealed class PosKeyVerifier : IPosKeyVerifier
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _httpClients;
    private readonly PosApiEndpoint _endpoint;
    private readonly ILogger _logger;

    public PosKeyVerifier(IHttpClientFactory httpClients, PosApiEndpoint endpoint, ILoggerFactory loggerFactory)
    {
        _httpClients = httpClients;
        _endpoint = endpoint;
        _logger = loggerFactory.CreateLogger<PosKeyVerifier>();
    }

    public async Task<PosKeyCheck> VerifyAsync(string apiKey, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var client = _httpClients.CreateClient(PosApiClientFactory.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint.BaseUrl, "/api/whoami"));
        request.Headers.TryAddWithoutValidation(PosApiClient.ApiKeyHeader, apiKey);
        try
        {
            using var response = await client.SendAsync(request, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new PosKeyCheck(false, null, null, "POSitive Anywhere rejected this API key (401). Check it was copied whole, or create a new one.");
            if (!response.IsSuccessStatusCode)
                return new PosKeyCheck(false, null, null, $"POSitive Anywhere answered {(int)response.StatusCode} to /api/whoami.");
            var who = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            var developerId = who.TryGetProperty("developerId", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            // /api/whoami sends settingsId as a string ("1"); accept a number too. TryGetInt32 throws on a string.
            int? settingsId = who.TryGetProperty("settingsId", out var s)
                              && ((s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var sid))
                                  || (s.ValueKind == JsonValueKind.String && int.TryParse(s.GetString(), System.Globalization.NumberStyles.None,
                                      System.Globalization.CultureInfo.InvariantCulture, out sid)))
                ? sid : null;
            return string.IsNullOrWhiteSpace(developerId)
                ? new PosKeyCheck(false, null, settingsId, "POSitive Anywhere did not report a Developer ID for this key.")
                : new PosKeyCheck(true, developerId.Trim(), settingsId, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning("GET /api/whoami at {BaseUrl} failed: {Error}", _endpoint.BaseUrl, ex.Message);
            return new PosKeyCheck(false, null, null,
                "POSitive Anywhere could not be reached on this machine. Check that the POSitive Anywhere service is running.");
        }
    }
}

/// <summary>
/// Per-user limit on the UI calls that reach out with a key or to a store (test connection, POSitive
/// key store/verify): <see cref="Limit"/> per <see cref="Window"/> per user and bucket, in memory.
/// Over the limit the endpoint answers 429 with Retry-After.
/// </summary>
public sealed class ConnectorUiRateLimiter
{
    public const int Limit = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time;
    private readonly Dictionary<(string User, string Bucket), Queue<DateTimeOffset>> _hits = new();
    private readonly object _lock = new();

    public ConnectorUiRateLimiter(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Counts one call; false (and how long to wait) when the user already made <see cref="Limit"/> in the window.</summary>
    public bool TryAcquire(string user, string bucket, out TimeSpan retryAfter)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            var key = (user.ToUpperInvariant(), bucket);
            if (!_hits.TryGetValue(key, out var hits))
                _hits[key] = hits = new Queue<DateTimeOffset>();
            while (hits.Count > 0 && now - hits.Peek() >= Window)
                hits.Dequeue();
            if (hits.Count >= Limit)
            {
                retryAfter = hits.Peek() + Window - now;
                return false;
            }
            hits.Enqueue(now);
            retryAfter = TimeSpan.Zero;
            // Keep the table small: drop users whose window has passed.
            if (_hits.Count > 1000)
            {
                foreach (var stale in _hits.Where(h => h.Value.Count == 0 || now - h.Value.Last() >= Window).Select(h => h.Key).ToList())
                    _hits.Remove(stale);
            }
            return true;
        }
    }
}

/// <summary>
/// A POSitive item the match review's picker offers. <see cref="WebProduct"/> null = unknown (the API
/// did not say): no "not a web item" tag. <see cref="LinkedTo"/> = the Woo id it is linked to now.
/// </summary>
public sealed record PosProductHit(
    int InvNo, string? Sku, string? Description, bool? WebProduct, decimal? AvailableLive, string? LinkedTo);

/// <summary>A lookup the picker could not finish (timed out or refused): shown as a row, the rest still listed.</summary>
public sealed record PosLookupProblem(int? InvNo, string Message);

public sealed record PosProductSearchResult(IReadOnlyList<PosProductHit> Items, IReadOnlyList<PosLookupProblem> Unavailable)
{
    /// <summary>Every lookup failed and nothing was found: the page says POSitive could not be searched.</summary>
    public bool AllFailed { get; init; }
}

/// <summary>
/// The match review's POSitive item picker, through the connector's own <see cref="IPosGateway"/>
/// (its key, its tenant). The public API has no text search, so: a number is tried as an invNo,
/// the text as a SKU/barcode (<c>?sku=</c>, any item), and the web items (read once and kept
/// for <see cref="CacheFor"/>) are matched on description and SKU.
/// <para>
/// Resilient (F3): the single-item reads (invNo, each SKU hit) run in parallel with the web-item
/// search, each with its own <see cref="LookupTimeout"/>; whatever succeeded is returned, and an item
/// that could not be read comes back in <see cref="PosProductSearchResult.Unavailable"/> ("item 900248
/// couldn't be loaded right now") instead of failing the whole search. A slow single read never holds
/// the cached web-item list (its own gate, its own task).
/// </para>
/// </summary>
public sealed class PosProductSearch
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultLookupTimeout = TimeSpan.FromSeconds(5);
    private const int MaxWebItems = 20_000;
    private const int MaxSkuHits = 10;

    private readonly IPosGatewayFactory _gateways;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (DateTime At, IReadOnlyList<PosProduct> Items)> _web = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PosProductSearch(IPosGatewayFactory gateways, TimeProvider? time = null, TimeSpan? lookupTimeout = null)
    {
        _gateways = gateways;
        _time = time ?? TimeProvider.System;
        LookupTimeout = lookupTimeout ?? DefaultLookupTimeout;
    }

    /// <summary>Per single-item read (invNo / SKU resolve / each SKU hit).</summary>
    public TimeSpan LookupTimeout { get; }

    public async Task<PosProductSearchResult> SearchAsync(ConnectorSettings settings, string query, int max,
        CancellationToken cancellationToken)
    {
        var q = query.Trim();
        var pos = _gateways.Create(settings);
        var problems = new List<PosLookupProblem>();
        var attempts = 0;

        // Started first and awaited last: the description search runs while the single reads do.
        var webTask = WebMatchesAsync(settings, pos, q, max, cancellationToken);

        var direct = new List<Task<(int InvNo, PosProduct? Product, string? Error)>>();
        if (int.TryParse(q, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var invNo) && invNo > 0)
            direct.Add(ReadItemAsync(pos, invNo, cancellationToken));

        // SKU / barcode (BARCODES alternates included), any item — not only web items.
        attempts++;
        var (skuHits, skuError) = await WithTimeoutAsync(token => pos.ResolveSkuAsync(q, token), cancellationToken);
        if (skuError is not null)
            problems.Add(new PosLookupProblem(null, $"The SKU/barcode lookup for '{q}' couldn't be finished right now ({skuError})."));
        foreach (var match in (skuHits ?? []).Distinct().Where(m => m != invNo).Take(MaxSkuHits))
            direct.Add(ReadItemAsync(pos, match, cancellationToken));

        attempts += direct.Count;
        var hits = new Dictionary<int, PosProduct>();
        foreach (var (itemNo, product, error) in await Task.WhenAll(direct))
        {
            if (product is not null)
                hits.TryAdd(product.InvNo, product);
            else if (error is not null)
                problems.Add(new PosLookupProblem(itemNo, $"Item {itemNo} couldn't be loaded right now ({error})."));
        }

        attempts++;
        var (web, webError) = await webTask;
        foreach (var product in web)
        {
            // In the web-items list, so a web item even when the API leaves webProduct out.
            if (hits.TryGetValue(product.InvNo, out var found))
            {
                if (found.WebProduct is null)
                    hits[product.InvNo] = found with { WebProduct = true };
                continue;
            }
            if (hits.Count < max)
                hits[product.InvNo] = product with { WebProduct = product.WebProduct ?? true };
        }
        if (webError is not null)
            problems.Add(new PosLookupProblem(null, $"The description search over the web items couldn't be finished right now ({webError})."));
        // An item whose own read failed but that the web list found anyway is not "unavailable".
        problems.RemoveAll(p => p.InvNo is { } n && hits.ContainsKey(n));

        var items = hits.Values.Take(max)
            .Select(p => new PosProductHit(p.InvNo, p.Sku?.Trim(), p.Description?.Trim(), p.WebProduct, p.AvailableLive,
                string.IsNullOrWhiteSpace(p.ExternalId) ? null : p.ExternalId.Trim()))
            .ToList();
        return new PosProductSearchResult(items, problems) { AllFailed = items.Count == 0 && problems.Count >= attempts };
    }

    /// <summary>One item by invNo with its own timeout; never throws for a POSitive fault (the error comes back instead).</summary>
    private async Task<(int InvNo, PosProduct? Product, string? Error)> ReadItemAsync(IPosGateway pos, int invNo, CancellationToken cancellationToken)
    {
        var (product, error) = await WithTimeoutAsync(token => PA.Connectors.Core.Catalog.PosCatalog.GetAsync(pos, invNo, token), cancellationToken);
        return (invNo, product, error);
    }

    private async Task<(T? Value, string? Error)> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LookupTimeout);
        try
        {
            // WaitAsync: a gateway that ignores the token still cannot hold the search past the timeout.
            return (await read(timeout.Token).WaitAsync(timeout.Token), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (default, $"no answer within {LookupTimeout.TotalSeconds:0} s");
        }
        catch (Exception ex) when (ex is PosApiException or ConnectorJobException or HttpRequestException)
        {
            return (default, "POSitive refused or failed the lookup");
        }
    }

    private async Task<(IReadOnlyList<PosProduct> Items, string? Error)> WebMatchesAsync(ConnectorSettings settings, IPosGateway pos, string q,
        int max, CancellationToken cancellationToken)
    {
        await Task.Yield(); // the single reads start without waiting for the list
        try
        {
            var matches = (await WebItemsAsync(settings, pos, cancellationToken))
                .Where(p => (p.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                            || (p.Sku?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(max)
                .ToList();
            return (matches, null);
        }
        catch (Exception ex) when (ex is PosApiException or ConnectorJobException or HttpRequestException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return ([], "POSitive did not answer");
        }
    }

    private async Task<IReadOnlyList<PosProduct>> WebItemsAsync(ConnectorSettings settings, IPosGateway pos, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            if (_web.TryGetValue(settings.Id, out var cached) && now - cached.At < CacheFor)
                return cached.Items;
            var items = new List<PosProduct>();
            await foreach (var product in PA.Connectors.Core.Catalog.PosCatalog.ReadAllAsync(
                               pos, new PosProductQuery { WebstoreOnly = true }, cancellationToken))
            {
                items.Add(product);
                if (items.Count >= MaxWebItems)
                    break;
            }
            _web[settings.Id] = (now, items);
            return items;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// A Woo product or variation as the pages name it ("#58 (Draft, 'Widget Pro')"). <see cref="Option"/> =
/// a variation's attribute values ("Red", "Blue, Yes"); <see cref="VariationCount"/> = a variable product's.
/// </summary>
public sealed record WooItemSummary(long Id, string? Name, string? Status, string? Type, long ParentId,
    string? Sku = null, string? Option = null, int VariationCount = 0);

/// <summary>Reads Woo items for a message or the match review (a seam so tests need no store). Never throws: what cannot be read is left out.</summary>
public interface IWooItemLookup
{
    /// <summary>One item; null when it cannot be read — the message goes without it.</summary>
    Task<WooItemSummary?> GetAsync(ConnectorSettings settings, long wooId, CancellationToken cancellationToken);

    /// <summary>
    /// Many items in a few calls (the match review, once per page load): <paramref name="skus"/> by
    /// <c>products?sku=</c> (which finds variations too, with their parent), then <paramref name="ids"/>
    /// and the parents of the variations found by <c>products?include=</c>. Not found = left out.
    /// </summary>
    Task<IReadOnlyList<WooItemSummary>> FindAsync(ConnectorSettings settings, IReadOnlyCollection<long> ids,
        IReadOnlyCollection<string> skus, CancellationToken cancellationToken);

    /// <summary>The store's payment gateways (GET payment_gateways); null when they cannot be read.</summary>
    Task<IReadOnlyList<WooGatewaySummary>?> GetPaymentGatewaysAsync(ConnectorSettings settings, CancellationToken cancellationToken);
}

/// <summary>A Woo payment gateway: <see cref="Id"/> is an order's <c>payment_method</c>.</summary>
public sealed record WooGatewaySummary(string Id, string? Title, bool Enabled);

/// <summary>GET products/{id} with the connector's stored keys: short timeout, no retries, never throws.</summary>
public sealed class WooItemLookup : IWooItemLookup
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    private readonly IHttpClientFactory _httpClients;
    private readonly PA.Connectors.Core.Secrets.IConnectorSecretStore _secrets;
    private readonly ILoggerFactory _loggerFactory;

    public WooItemLookup(IHttpClientFactory httpClients, PA.Connectors.Core.Secrets.IConnectorSecretStore secrets, ILoggerFactory loggerFactory)
    {
        _httpClients = httpClients;
        _secrets = secrets;
        _loggerFactory = loggerFactory;
    }

    public async Task<WooItemSummary?> GetAsync(ConnectorSettings settings, long wooId, CancellationToken cancellationToken)
    {
        if (Client(settings) is not { } client)
            return null;
        try
        {
            var product = await client.GetProductAsync(wooId, cancellationToken);
            return product is null ? null : Summary(product);
        }
        catch (Exception ex) when (ex is WooApiException or HttpRequestException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _loggerFactory.CreateLogger<WooItemLookup>().LogInformation("Woo item {WooId} could not be read for a message: {Error}", wooId, ex.Message);
            return null;
        }
    }

    public async Task<IReadOnlyList<WooItemSummary>> FindAsync(ConnectorSettings settings, IReadOnlyCollection<long> ids,
        IReadOnlyCollection<string> skus, CancellationToken cancellationToken)
    {
        var found = new Dictionary<long, WooItemSummary>();
        if ((ids.Count == 0 && skus.Count == 0) || Client(settings) is not { } client)
            return [];
        try
        {
            if (skus.Count > 0)
            {
                foreach (var product in await client.FindBySkusAsync(skus, cancellationToken))
                    found[product.Id] = Summary(product);
            }
            var wanted = ids.Concat(found.Values.Where(f => f.ParentId > 0).Select(f => f.ParentId))
                .Where(id => id > 0 && !found.ContainsKey(id)).Distinct().ToList();
            foreach (var chunk in wanted.Chunk(100))
            {
                var page = await client.GetProductsPageAsync(1, new Dictionary<string, string>
                {
                    ["include"] = string.Join(",", chunk.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                    ["per_page"] = "100",
                    ["_fields"] = "id,name,type,status,sku,parent_id,variations,attributes",
                }, cancellationToken);
                foreach (var product in page.Items)
                    found[product.Id] = Summary(product);
            }
        }
        catch (Exception ex) when (ex is WooApiException or HttpRequestException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _loggerFactory.CreateLogger<WooItemLookup>().LogInformation(
                "Woo items could not be read for the match review ({Found} found before the error): {Error}", found.Count, ex.Message);
        }
        return found.Values.ToList();
    }

    public async Task<IReadOnlyList<WooGatewaySummary>?> GetPaymentGatewaysAsync(ConnectorSettings settings, CancellationToken cancellationToken)
    {
        if (Client(settings) is not { } client)
            return null;
        try
        {
            return (await client.GetPaymentGatewaysAsync(cancellationToken))
                .Where(g => !string.IsNullOrWhiteSpace(g.Id))
                .Select(g => new WooGatewaySummary(g.Id!.Trim(),
                    string.IsNullOrWhiteSpace(g.Title) ? g.MethodTitle?.Trim() : g.Title.Trim(), g.Enabled))
                .ToList();
        }
        catch (Exception ex) when (ex is WooApiException or HttpRequestException or JsonException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _loggerFactory.CreateLogger<WooItemLookup>().LogInformation("Woo payment gateways could not be read: {Error}", ex.Message);
            return null;
        }
    }

    private WooClient? Client(ConnectorSettings settings)
    {
        var key = _secrets.Get(PA.Connectors.Core.Secrets.ConnectorSecretNames.WooConsumerKey(settings.Id));
        var secret = _secrets.Get(PA.Connectors.Core.Secrets.ConnectorSecretNames.WooConsumerSecret(settings.Id));
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret) || !Uri.TryCreate(settings.StoreUrl, UriKind.Absolute, out var store))
            return null;
        return new WooClient(_httpClients.CreateClient(WooConnectorFactory.HttpClientName),
            new WooClientOptions { StoreUrl = store, ConsumerKey = key, ConsumerSecret = secret, RequestTimeout = Timeout, MaxRetries = 0 },
            _loggerFactory.CreateLogger<WooClient>());
    }

    private static WooItemSummary Summary(PA.Connectors.WooCommerce.Models.WooProduct p)
    {
        // A variation's own label is its attribute values ("Red", "Blue, Yes"); products?sku= names it "Parent - Red".
        var option = p.IsVariation
            ? string.Join(", ", p.Attributes.Select(a => a.Option?.Trim()).Where(o => !string.IsNullOrEmpty(o)))
            : null;
        return new WooItemSummary(p.Id, p.Name, p.Status, p.Type, p.ParentId, p.Sku,
            string.IsNullOrEmpty(option) ? null : option, p.Variations.Count);
    }
}

/// <summary>
/// What the match review read from Woo (names, SKUs, parents), per connector, kept for <see cref="Ttl"/> —
/// so a page load asks Woo at most once, and only for what it does not have yet. Misses are kept too
/// (an item Woo does not return is not asked for again until they expire).
/// </summary>
public sealed class WooReviewNames
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private const int MaxEntries = 20_000;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Connector, string Key), (DateTimeOffset At, WooItemSummary? Item)> _cache = new();

    /// <summary>Lookups made (tests).</summary>
    public int Lookups => _lookups;

    private int _lookups;

    /// <summary>
    /// The items for <paramref name="ids"/> and <paramref name="skus"/> (cached, else one
    /// <see cref="IWooItemLookup.FindAsync"/>, plus one more only for parents still unknown after it).
    /// </summary>
    public async Task<(IReadOnlyDictionary<long, WooItemSummary> ById, IReadOnlyDictionary<string, WooItemSummary> BySku)> GetAsync(
        IWooItemLookup lookup, ConnectorSettings settings, IReadOnlyCollection<long> ids, IReadOnlyCollection<string> skus,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var connector = settings.Id.ToLowerInvariant();
        var byId = new Dictionary<long, WooItemSummary>();
        var bySku = new Dictionary<string, WooItemSummary>(StringComparer.OrdinalIgnoreCase);
        static string IdKey(long id) => "id:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        static string SkuKey(string sku) => "sku:" + sku.Trim().ToLowerInvariant();

        bool Cached(string key, out WooItemSummary? item)
        {
            item = null;
            if (!_cache.TryGetValue((connector, key), out var hit) || now - hit.At > Ttl)
                return false;
            item = hit.Item;
            return true;
        }
        void Keep(WooItemSummary item)
        {
            byId[item.Id] = item;
            if (!string.IsNullOrWhiteSpace(item.Sku))
                bySku[item.Sku.Trim()] = item;
        }

        async Task FetchAsync(List<long> missingIds, List<string> missingSkus)
        {
            if (missingIds.Count == 0 && missingSkus.Count == 0)
                return;
            if (_cache.Count > MaxEntries)
                _cache.Clear();
            Interlocked.Increment(ref _lookups);
            var found = await lookup.FindAsync(settings, missingIds, missingSkus, cancellationToken);
            foreach (var item in found)
            {
                _cache[(connector, IdKey(item.Id))] = (now, item);
                if (!string.IsNullOrWhiteSpace(item.Sku))
                    _cache[(connector, SkuKey(item.Sku))] = (now, item);
                Keep(item);
            }
            foreach (var id in missingIds.Where(i => !byId.ContainsKey(i)))
                _cache[(connector, IdKey(id))] = (now, null);
            foreach (var sku in missingSkus.Where(s => !bySku.ContainsKey(s.Trim())))
                _cache[(connector, SkuKey(sku))] = (now, null);
        }

        var needIds = new List<long>();
        foreach (var id in ids.Where(i => i > 0).Distinct())
        {
            if (Cached(IdKey(id), out var item)) { if (item is not null) Keep(item); }
            else needIds.Add(id);
        }
        var needSkus = new List<string>();
        foreach (var sku in skus.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Cached(SkuKey(sku), out var item)) { if (item is not null) Keep(item); }
            else needSkus.Add(sku);
        }
        await FetchAsync(needIds, needSkus);

        // The parents of the variations found (FindAsync reads them with the rest; a cached variation's may have expired).
        var parents = new List<long>();
        foreach (var parent in byId.Values.Where(v => v.ParentId > 0).Select(v => v.ParentId).Distinct().Where(p => !byId.ContainsKey(p)).ToList())
        {
            if (Cached(IdKey(parent), out var item)) { if (item is not null) Keep(item); }
            else parents.Add(parent);
        }
        await FetchAsync(parents, []);
        return (byId, bySku);
    }
}

/// <summary>AUDIT lines for every change made through the connector UI (host log, pa-connectors-*.log).</summary>
public sealed class ConnectorUiAudit
{
    private readonly ILogger _logger;

    public ConnectorUiAudit(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger("PA.Connectors.Host.Audit");
    }

    public void Record(PortalCaller caller, string action, string? connectorId, string detail) =>
        _logger.LogInformation("AUDIT connector {Action} {ConnectorId} by {User}: {Detail}",
            action, connectorId ?? "-", caller.User, detail);

    /// <summary>
    /// An order exception closed from the pages, in the connector's own "Order exception &lt;kind&gt;
    /// &lt;order id&gt;: …" form (beside the AUDIT line). Kind, Woo order id and who — never the reason.
    /// </summary>
    public void OrderException(PortalCaller caller, PA.Connectors.Core.Stores.ConnectorExceptionRow row, string outcome)
    {
        if (row.Kind.StartsWith("order_", StringComparison.Ordinal) || row.Kind == PA.Connectors.Core.Orders.OrderExceptionKinds.PosUpgradeRequired)
            _logger.LogInformation("Order exception {Kind} {Ref}: {Outcome} by {User} (exception {ExceptionId})",
                row.Kind, row.ExternalRef, outcome, caller.User, row.ExceptionId);
    }

    /// <summary>The details behind a fixed message a page showed (paths, exception text stay in the log).</summary>
    public void Failure(Exception ex, string what) =>
        _logger.LogWarning("Connector UI: {What}: {Error}", what, ex.Message);
}
