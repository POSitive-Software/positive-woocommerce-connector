using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PA.Connectors.WooCommerce.Models;

namespace PA.Connectors.WooCommerce;

/// <summary>Connection and retry settings for one store.</summary>
public sealed record WooClientOptions
{
    /// <summary>Store base URL (https://shop.example.com); the client appends /wp-json/wc/v3/.</summary>
    public required Uri StoreUrl { get; init; }

    public required string ConsumerKey { get; init; }

    public required string ConsumerSecret { get; init; }

    /// <summary>Per request, including reading the body. A timeout is retried like a 5xx.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Retries after the first try (429 / 5xx / timeout / connection failure).</summary>
    public int MaxRetries { get; init; } = 4;

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Items per page (Woo max 100).</summary>
    public int PageSize { get; init; } = 100;
}

/// <summary>A Woo REST failure. <see cref="Transient"/> = worth retrying later (429/5xx/timeout).</summary>
public sealed class WooApiException : Exception
{
    public WooApiException(string message, HttpStatusCode? status, string? code, bool transient, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
        Code = code;
        Transient = transient;
    }

    public HttpStatusCode? Status { get; }

    /// <summary>Woo's error code, e.g. woocommerce_rest_cannot_view, rest_no_route.</summary>
    public string? Code { get; }

    /// <summary>Woo's own short error message (cleaned, at most 200 characters), when its error body had one.</summary>
    public string? WooMessage { get; init; }

    public bool Transient { get; }
}

/// <summary>One page of a list call.</summary>
public sealed record WooPage<T>(IReadOnlyList<T> Items, int Page, int? TotalPages, int? TotalItems, bool HasNext);

/// <summary>Result of a connection test.</summary>
public sealed record WooConnectionInfo(int? ProductCount);

/// <summary>One failed item of a batch call.</summary>
public sealed record WooBatchItemError(string Operation, long Id, string? Sku, string? Code, string? Message);

/// <summary>Combined outcome of a (chunked) batch call: what succeeded and each item that did not.</summary>
public sealed record WooBatchResult(
    IReadOnlyList<WooBatchItem> Created,
    IReadOnlyList<WooBatchItem> Updated,
    IReadOnlyList<long> Deleted,
    IReadOnlyList<WooBatchItemError> Errors);

/// <summary>
/// WooCommerce REST client (wc/v3) over HttpClient: Basic auth (consumer key /
/// secret, over HTTPS), per-request timeout, paging via X-WP-TotalPages / Link,
/// chunked batch writes with per-item error collection, and retry with
/// exponential backoff on 429 / 5xx / timeouts. A request that may already have
/// taken effect (a create) is retried only on 429, which Woo answers before
/// doing anything. Never calls /system_status — it hung the dev store (S0).
/// </summary>
public sealed class WooClient
{
    /// <summary>Woo's per-request cap on create + update + delete in one batch.</summary>
    public const int MaxBatchSize = 100;

    /// <summary>SKUs per ?sku= lookup (comma list), keeping the URL short.</summary>
    internal const int SkuLookupChunk = 20;

    private const int MaxPages = 10_000;

    private readonly HttpClient _http;
    private readonly WooClientOptions _options;
    private readonly Uri _baseUri;
    private readonly AuthenticationHeaderValue _auth;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public WooClient(HttpClient http, WooClientOptions options, ILogger<WooClient>? logger = null)
        : this(http, options, logger, delay: null)
    {
    }

    /// <param name="delay">Test seam for the backoff wait; default Task.Delay.</param>
    internal WooClient(HttpClient http, WooClientOptions options, ILogger? logger,
        Func<TimeSpan, CancellationToken, Task>? delay)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConsumerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConsumerSecret);
        _http = http;
        _options = options;
        _baseUri = BaseUriFor(options.StoreUrl);
        _auth = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.ConsumerKey}:{options.ConsumerSecret}")));
        _logger = logger ?? NullLogger.Instance;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>https://shop.example.com/sub → https://shop.example.com/sub/wp-json/wc/v3/</summary>
    public static Uri BaseUriFor(Uri storeUrl)
    {
        var root = storeUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri(root + "/wp-json/wc/v3/", UriKind.Absolute);
    }

    // --- connection ------------------------------------------------------

    /// <summary>
    /// Proves the URL is a Woo store and the keys can read products:
    /// GET products?per_page=1 (authenticated, cheap; X-WP-Total = product count).
    /// Throws <see cref="WooApiException"/> with Woo's code (401 bad keys, 404 rest_no_route = not WooCommerce).
    /// </summary>
    public async Task<WooConnectionInfo> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var page = await GetPageAsync<WooProduct>("products", new() { ["per_page"] = "1", ["_fields"] = "id" }, 1, cancellationToken);
        return new WooConnectionInfo(page.TotalItems);
    }

    // --- products ----------------------------------------------------------

    public Task<WooProduct?> GetProductAsync(long id, CancellationToken cancellationToken = default) =>
        GetOrNullAsync<WooProduct>($"products/{id}", cancellationToken);

    /// <summary>One page of products. <paramref name="query"/> takes Woo's list parameters (sku, status, type, modified_after, ...).</summary>
    public Task<WooPage<WooProduct>> GetProductsPageAsync(
        int page, IReadOnlyDictionary<string, string>? query = null, CancellationToken cancellationToken = default) =>
        GetPageAsync<WooProduct>("products", WithPageSize(query), page, cancellationToken);

    public IAsyncEnumerable<WooProduct> GetAllProductsAsync(
        IReadOnlyDictionary<string, string>? query = null, CancellationToken cancellationToken = default) =>
        GetAllAsync<WooProduct>("products", WithPageSize(query), cancellationToken);

    /// <summary>
    /// Products AND variations whose SKU equals one of <paramref name="skus"/>,
    /// ignoring case (Woo's ?sku= matches exactly but case-insensitively, and
    /// the connector must agree — S0bc). Comma lists are chunked; a SKU that
    /// itself contains a comma is looked up alone.
    /// </summary>
    public async Task<IReadOnlyList<WooProduct>> FindBySkusAsync(
        IEnumerable<string> skus, CancellationToken cancellationToken = default)
    {
        var wanted = skus.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var wantedSet = wanted.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var batches = wanted.Where(s => !s.Contains(',')).Chunk(SkuLookupChunk).Select(c => string.Join(",", c))
            .Concat(wanted.Where(s => s.Contains(',')));

        var found = new Dictionary<long, WooProduct>();
        foreach (var batch in batches)
        {
            await foreach (var product in GetAllAsync<WooProduct>("products",
                               WithPageSize(new Dictionary<string, string> { ["sku"] = batch }), cancellationToken))
            {
                if (product.Sku is not null && wantedSet.Contains(product.Sku.Trim()))
                    found[product.Id] = product;
            }
        }
        return found.Values.OrderBy(p => p.Id).ToList();
    }

    public async Task<IReadOnlyList<WooVariation>> GetVariationsAsync(
        long productId, CancellationToken cancellationToken = default)
    {
        var all = new List<WooVariation>();
        await foreach (var variation in GetAllAsync<WooVariation>(
                           $"products/{productId}/variations", WithPageSize(null), cancellationToken))
            all.Add(variation);
        return all;
    }

    public Task<WooVariation?> GetVariationAsync(long productId, long variationId, CancellationToken cancellationToken = default) =>
        GetOrNullAsync<WooVariation>($"products/{productId}/variations/{variationId}", cancellationToken);

    /// <summary>POST products/batch, chunked to ≤100 operations per request; per-item errors collected, not thrown.</summary>
    public Task<WooBatchResult> BatchProductsAsync(WooBatchRequest request, CancellationToken cancellationToken = default) =>
        BatchAsync("products/batch", request, cancellationToken);

    /// <summary>POST products/{parentId}/variations/batch, chunked like <see cref="BatchProductsAsync"/>.</summary>
    public Task<WooBatchResult> BatchVariationsAsync(
        long parentId, WooBatchRequest request, CancellationToken cancellationToken = default) =>
        BatchAsync($"products/{parentId}/variations/batch", request, cancellationToken);

    /// <summary>
    /// The store's price decimals (GET settings/general/woocommerce_price_num_decimals), so money
    /// is sent the way the store shows it. Null when the setting cannot be read or is not a number.
    /// </summary>
    public async Task<int?> GetPriceDecimalsAsync(CancellationToken cancellationToken = default)
    {
        var setting = await GetOrNullAsync<WooSettingValue>("settings/general/woocommerce_price_num_decimals", cancellationToken);
        return int.TryParse(setting?.Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimals)
               && decimals is >= 0 and <= 8
            ? decimals
            : null;
    }

    /// <summary>
    /// One store setting as text (GET settings/{group}/{id}, e.g. general/woocommerce_currency) — the
    /// connect wizard's light store check (system_status hung the dev store). Null when the setting
    /// does not exist (404) or has no value.
    /// </summary>
    public async Task<string?> GetSettingAsync(string group, string id, CancellationToken cancellationToken = default)
    {
        var setting = await GetOrNullAsync<WooSettingValue>(
            $"settings/{Uri.EscapeDataString(group)}/{Uri.EscapeDataString(id)}", cancellationToken);
        return setting?.Value is { } value
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;
    }

    private sealed class WooSettingValue
    {
        public JsonElement? Value { get; set; }
    }

    /// <summary>
    /// GET payment_gateways (every installed gateway, enabled or not; one unpaged list) — the Settings
    /// page's payment-method → tender rows. Only id, title, method title and enabled are read.
    /// </summary>
    public async Task<IReadOnlyList<WooPaymentGateway>> GetPaymentGatewaysAsync(CancellationToken cancellationToken = default) =>
        await GetOrNullAsync<List<WooPaymentGateway>>("payment_gateways", cancellationToken,
            new Dictionary<string, string> { ["_fields"] = "id,title,method_title,enabled" }) ?? [];

    // --- orders ------------------------------------------------------------

    /// <summary>
    /// Decimal places asked for on orders (Woo's <c>dp</c> parameter on the orders endpoints, default 2).
    /// Woo keeps line, shipping, fee and tax amounts unrounded, so 6 places let the import add the
    /// lines up exactly instead of summing amounts that were each rounded to the cent.
    /// </summary>
    public const int OrderDecimalPlaces = 6;

    private static readonly Dictionary<string, string> OrderPrecision = new()
    {
        ["dp"] = OrderDecimalPlaces.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>GET orders/{id}?dp=6 (null = no such order). A trashed order is still returned, with status "trash".</summary>
    public Task<WooOrder?> GetOrderAsync(long id, CancellationToken cancellationToken = default) =>
        GetOrNullAsync<WooOrder>($"orders/{id}", cancellationToken, OrderPrecision);

    /// <summary>
    /// Orders modified after <paramref name="modifiedAfterUtc"/> (UTC; sent with
    /// dates_are_gmt=true), oldest id first, amounts to 6 places (dp=6). Optional status filter.
    /// </summary>
    public IAsyncEnumerable<WooOrder> GetOrdersModifiedAfterAsync(
        DateTime modifiedAfterUtc, IEnumerable<string>? statuses = null, CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string>
        {
            ["modified_after"] = DateTime.SpecifyKind(modifiedAfterUtc.ToUniversalTime(), DateTimeKind.Unspecified)
                .ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            ["dates_are_gmt"] = "true",
            ["orderby"] = "id",
            ["order"] = "asc",
            ["dp"] = OrderDecimalPlaces.ToString(CultureInfo.InvariantCulture),
        };
        var statusList = statuses?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (statusList is { Count: > 0 })
            query["status"] = string.Join(",", statusList);
        return GetAllAsync<WooOrder>("orders", WithPageSize(query), cancellationToken);
    }

    public async Task<IReadOnlyList<WooRefund>> GetOrderRefundsAsync(long orderId, CancellationToken cancellationToken = default)
    {
        var all = new List<WooRefund>();
        await foreach (var refund in GetAllAsync<WooRefund>($"orders/{orderId}/refunds", WithPageSize(null), cancellationToken))
            all.Add(refund);
        return all;
    }

    // --- webhooks ----------------------------------------------------------

    public async Task<IReadOnlyList<WooWebhook>> ListWebhooksAsync(CancellationToken cancellationToken = default)
    {
        var all = new List<WooWebhook>();
        await foreach (var hook in GetAllAsync<WooWebhook>("webhooks", WithPageSize(null), cancellationToken))
            all.Add(hook);
        return all;
    }

    /// <summary>POST webhooks. Not idempotent: retried only on 429. Woo pings the delivery URL synchronously on create.</summary>
    public Task<WooWebhook> CreateWebhookAsync(WooWebhookWrite webhook, CancellationToken cancellationToken = default) =>
        SendJsonAsync<WooWebhook>(HttpMethod.Post, "webhooks", webhook, idempotent: false, cancellationToken);

    /// <summary>PUT webhooks/{id} (e.g. status "active" to re-enable one Woo disabled).</summary>
    public Task<WooWebhook> UpdateWebhookAsync(long id, WooWebhookWrite webhook, CancellationToken cancellationToken = default) =>
        SendJsonAsync<WooWebhook>(HttpMethod.Put, $"webhooks/{id}", webhook, idempotent: true, cancellationToken);

    // --- plumbing ----------------------------------------------------------

    private Dictionary<string, string> WithPageSize(IReadOnlyDictionary<string, string>? query)
    {
        var merged = query is null ? new Dictionary<string, string>() : new Dictionary<string, string>(query);
        merged.TryAdd("per_page", Math.Clamp(_options.PageSize, 1, 100).ToString(CultureInfo.InvariantCulture));
        return merged;
    }

    private async Task<WooBatchResult> BatchAsync(string path, WooBatchRequest request, CancellationToken cancellationToken)
    {
        // Creates first, then updates, then deletes — each chunk ≤ MaxBatchSize ops.
        var operations = new List<(string Op, object Item)>();
        operations.AddRange((request.Create ?? []).Select(c => ("create", (object)c)));
        operations.AddRange((request.Update ?? []).Select(u => ("update", (object)u)));
        operations.AddRange((request.Delete ?? []).Select(d => ("delete", (object)d)));

        var created = new List<WooBatchItem>();
        var updated = new List<WooBatchItem>();
        var deleted = new List<long>();
        var errors = new List<WooBatchItemError>();

        foreach (var chunk in operations.Chunk(MaxBatchSize))
        {
            var body = new WooBatchRequest
            {
                Create = Collect<WooProductWrite>(chunk, "create"),
                Update = Collect<WooProductWrite>(chunk, "update"),
                Delete = Collect<long>(chunk, "delete"),
            };
            // A chunk with creates may have taken effect when a 5xx/timeout comes
            // back; retrying it could duplicate products — so only 429 is retried.
            var idempotent = body.Create is null;
            var response = await SendJsonAsync<WooBatchResponse>(HttpMethod.Post, path, body, idempotent, cancellationToken);

            Split(response.Create, "create", created);
            Split(response.Update, "update", updated);
            foreach (var item in response.Delete)
            {
                if (item.Error is null)
                    deleted.Add(item.Id);
                else
                    errors.Add(new WooBatchItemError("delete", item.Id, item.Sku, item.Error.Code, item.Error.Message));
            }
        }

        return new WooBatchResult(created, updated, deleted, errors);

        void Split(List<WooBatchItem> items, string op, List<WooBatchItem> ok)
        {
            foreach (var item in items)
            {
                if (item.Error is null)
                    ok.Add(item);
                else
                    errors.Add(new WooBatchItemError(op, item.Id, item.Sku, item.Error.Code, item.Error.Message));
            }
        }

        static List<T>? Collect<T>((string Op, object Item)[] chunk, string op)
        {
            var items = chunk.Where(c => c.Op == op).Select(c => (T)c.Item).ToList();
            return items.Count == 0 ? null : items;
        }
    }

    private async IAsyncEnumerable<T> GetAllAsync<T>(
        string path, Dictionary<string, string> query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var page = 1; page <= MaxPages; page++)
        {
            var result = await GetPageAsync<T>(path, query, page, cancellationToken);
            foreach (var item in result.Items)
                yield return item;
            if (!result.HasNext)
                yield break;
        }
        throw new WooApiException($"GET {path} returned more than {MaxPages} pages; refusing to continue.", null, null, false);
    }

    private async Task<WooPage<T>> GetPageAsync<T>(
        string path, Dictionary<string, string> query, int page, CancellationToken cancellationToken)
    {
        var withPage = new Dictionary<string, string>(query) { ["page"] = page.ToString(CultureInfo.InvariantCulture) };
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Url(path, withPage)),
            idempotent: true, cancellationToken);
        var items = await ReadAsync<List<T>>(response, path, cancellationToken) ?? [];

        var totalPages = HeaderInt(response, "X-WP-TotalPages");
        var totalItems = HeaderInt(response, "X-WP-Total");
        var perPage = int.TryParse(query.GetValueOrDefault("per_page"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pp) ? pp : 10;
        bool hasNext;
        if (totalPages is not null)
            hasNext = page < totalPages.Value;
        else if (response.Headers.Contains("Link"))
            hasNext = HasNextLink(response);
        else
            hasNext = items.Count >= perPage && items.Count > 0;
        return new WooPage<T>(items, page, totalPages, totalItems, hasNext);
    }

    private async Task<T?> GetOrNullAsync<T>(string path, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? query = null) where T : class
    {
        try
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Url(path, query)),
                idempotent: true, cancellationToken);
            return await ReadAsync<T>(response, path, cancellationToken);
        }
        catch (WooApiException ex) when (ex.Status == HttpStatusCode.NotFound && ex.Code != "rest_no_route")
        {
            return null;
        }
    }

    private async Task<T> SendJsonAsync<T>(
        HttpMethod method, string path, object body, bool idempotent, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(body, body.GetType(), WooJson.Options);
        using var response = await SendAsync(() => new HttpRequestMessage(method, Url(path, null))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }, idempotent, cancellationToken);
        return await ReadAsync<T>(response, path, cancellationToken)
               ?? throw new WooApiException($"{method} {path} returned an empty body.", response.StatusCode, null, false);
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(text, WooJson.Options);
        }
        catch (JsonException ex)
        {
            throw new WooApiException($"{path}: the store returned a response that is not the expected JSON ({ex.Message}).",
                response.StatusCode, "invalid_json", false, ex);
        }
    }

    /// <summary>
    /// Sends with retry. Success = 2xx (the caller disposes the response).
    /// 429 always retries (honouring Retry-After); 5xx, timeouts and connection
    /// failures retry only when <paramref name="idempotent"/>.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> buildRequest, bool idempotent, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = buildRequest();
            request.Headers.Authorization = _auth;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var what = $"{request.Method} {request.RequestUri?.AbsolutePath}";
            var canRetry = attempt < _options.MaxRetries;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (idempotent && canRetry)
                {
                    await BackoffAsync(attempt, null, what, "timeout", cancellationToken);
                    continue;
                }
                throw new WooApiException($"{what} timed out after {_options.RequestTimeout.TotalSeconds:0}s.",
                    null, "timeout", transient: true, ex);
            }
            catch (HttpRequestException ex)
            {
                if (idempotent && canRetry)
                {
                    await BackoffAsync(attempt, null, what, ex.Message, cancellationToken);
                    continue;
                }
                throw new WooApiException($"{what} failed: {ex.Message}", null, "connection_failed", transient: true, ex);
            }

            if (response.IsSuccessStatusCode)
                return response;

            var status = response.StatusCode;
            var transient = status == HttpStatusCode.TooManyRequests || (int)status >= 500;
            var retry = canRetry && (status == HttpStatusCode.TooManyRequests || (idempotent && (int)status >= 500));
            if (retry)
            {
                var retryAfter = RetryAfter(response);
                response.Dispose();
                await BackoffAsync(attempt, retryAfter, what, ((int)status).ToString(CultureInfo.InvariantCulture), cancellationToken);
                continue;
            }

            using (response)
            {
                var (code, message) = await ReadErrorAsync(response, cancellationToken);
                throw new WooApiException(
                    $"{what} → {(int)status} {code}: {message}".TrimEnd(' ', ':'), status, code, transient)
                {
                    WooMessage = message,
                };
            }
        }
    }

    private async Task BackoffAsync(int attempt, TimeSpan? retryAfter, string what, string reason, CancellationToken cancellationToken)
    {
        var exponential = TimeSpan.FromTicks((long)(_options.RetryBaseDelay.Ticks * Math.Pow(2, Math.Min(attempt, 20))));
        var delay = retryAfter ?? exponential;
        if (delay > _options.MaxRetryDelay)
            delay = _options.MaxRetryDelay;
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;
        _logger.LogInformation("Woo {Request} failed ({Reason}); retry {Attempt} in {Delay}ms",
            what, reason, attempt + 1, (int)delay.TotalMilliseconds);
        await _delay(delay, cancellationToken);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta;
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>Longest Woo error message kept (they are one sentence); the rest is cut.</summary>
    internal const int MaxErrorMessageLength = 200;

    /// <summary>
    /// Woo's error <c>code</c> and short <c>message</c> from its JSON error body — never the raw body:
    /// whatever answered at the store address (a proxy page, another server) must not end up in the
    /// connector's pages, exceptions or logs. Anything that is not a Woo error is "no code, no message".
    /// </summary>
    private static async Task<(string? Code, string? Message)> ReadErrorAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var error = JsonSerializer.Deserialize<WooError>(text, WooJson.Options);
            return (CleanCode(error?.Code), CleanMessage(error?.Message));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    /// <summary>A Woo error code is an identifier (woocommerce_rest_cannot_view); anything else is dropped.</summary>
    internal static string? CleanCode(string? code) =>
        string.IsNullOrWhiteSpace(code) || code.Length > 100
        || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
            ? null
            : code;

    /// <summary>One line, no control characters, at most <see cref="MaxErrorMessageLength"/> characters.</summary>
    internal static string? CleanMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;
        var cleaned = new string(message.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (cleaned.Length == 0)
            return null;
        return cleaned.Length <= MaxErrorMessageLength ? cleaned : cleaned[..MaxErrorMessageLength] + "…";
    }

    private static int? HeaderInt(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
        && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>RFC 5988: <c>&lt;https://…?page=2&gt;; rel="next"</c>.</summary>
    internal static bool HasNextLink(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Link", out var values)
        && values.SelectMany(v => v.Split(',')).Any(part =>
            part.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)
            || part.Contains("rel=next", StringComparison.OrdinalIgnoreCase));

    private Uri Url(string path, IReadOnlyDictionary<string, string>? query)
    {
        var builder = new StringBuilder(path);
        if (query is { Count: > 0 })
        {
            builder.Append('?');
            builder.AppendJoin('&', query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
        }
        return new Uri(_baseUri, builder.ToString());
    }
}
