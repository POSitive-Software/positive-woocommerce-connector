using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PA.Connectors.Core.Secrets;

namespace PA.Connectors.Core.Pos;

/// <summary>Where and how a <see cref="PosApiClient"/> calls POSitive.</summary>
public sealed record PosApiClientOptions
{
    /// <summary>Server base URL (host setting <c>Connectors:PosApiBaseUrl</c>, loopback only); routes are appended as <c>/api/v2/...</c>.</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>Per-request timeout. A timeout is a transient failure (the job retries).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Connector id — only for messages.</summary>
    public string ConnectorId { get; init; } = "";
}

/// <summary>A transient POSitive API failure (5xx, 429, timeout): the job retries with backoff.</summary>
public sealed class PosApiException : Exception
{
    public PosApiException(string message, HttpStatusCode? status, string? errorCode, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
        ErrorCode = errorCode;
    }

    public HttpStatusCode? Status { get; }

    public string? ErrorCode { get; }
}

/// <summary>
/// <see cref="IPosGateway"/> over the public V2 API — the connector is a client
/// like any other (design §0). Every request carries the connector's OWN key
/// (<c>X-Api-Key</c>, from the add-on's secret store), so the tenant is decided
/// by the server exactly as for an outside developer.
/// <para>
/// Error mapping (same errorCodes the in-process gateway of S1 returned, because
/// they ARE the endpoints' codes): any 4xx problem+json → <see cref="PosResult{T}"/>
/// failure carrying its <c>errorCode</c>, <c>detail</c> and per-field <c>errors</c>
/// (422 <c>no_writable_lines</c> also keeps the per-line statuses). 401/403 →
/// non-retryable <see cref="ConnectorJobException"/> (the key is wrong — retrying
/// will not fix it). 5xx, 408, 429, timeouts and connection failures →
/// <see cref="PosApiException"/> / the transport exception, which the engine
/// treats as transient.
/// </para>
/// </summary>
public sealed partial class PosApiClient : IPosGateway
{
    public const string ApiKeyHeader = "X-Api-Key";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string ValidationDetail = "One or more fields failed validation — see the field-level messages.";

    private readonly HttpClient _http;
    private readonly PosApiClientOptions _options;
    private readonly Func<string?> _apiKey;
    private readonly ILogger _logger;

    /// <param name="apiKey">Read on every call, so a key rotated by the wizard takes effect without a restart.</param>
    public PosApiClient(HttpClient http, PosApiClientOptions options, Func<string?> apiKey, ILogger<PosApiClient>? logger = null)
    {
        _http = http;
        _options = options;
        _apiKey = apiKey;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    // --- customers -------------------------------------------------------

    public async Task<PosResult<PosCustomerRef>> FindCustomerAsync(
        string localCustomerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localCustomerId))
            return PosResult<PosCustomerRef>.Fail(PosErrorCodes.CustomerNotFound, "No localCustomerId given.");

        var page = await GetAsync<PagedDto<CustomerListDto>>(
            $"/api/v2/customers?externalId={Q(localCustomerId)}&pageSize=1", cancellationToken);
        if (!page.Success)
            return Rewrap<PagedDto<CustomerListDto>, PosCustomerRef>(page);
        var customer = page.Value!.Items?.FirstOrDefault();
        return customer is null
            ? PosResult<PosCustomerRef>.Fail(PosErrorCodes.CustomerNotFound,
                $"No customer is linked to localCustomerId '{localCustomerId}' for this key.")
            : PosResult<PosCustomerRef>.Ok(new PosCustomerRef(customer.CustomerId, localCustomerId));
    }

    public async Task<PosResult<PosCustomerRef>> CreateCustomerAsync(
        PosCustomer customer, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<CustomerWriteResultDto>(HttpMethod.Post, "/api/v2/customers",
            JsonContent(customer), cancellationToken);
        return result.Success
            ? PosResult<PosCustomerRef>.Ok(new PosCustomerRef(result.Value!.CustomerId ?? 0, result.Value.ExternalId))
            : Rewrap<CustomerWriteResultDto, PosCustomerRef>(result);
    }

    public async Task<PosResult<PosCustomerRef>> PatchCustomerAsync(
        int customerId, PosCustomer changes, CancellationToken cancellationToken = default)
    {
        if (customerId <= 0)
            return PosResult<PosCustomerRef>.Fail(PosErrorCodes.InvalidCustomerId,
                $"customerId must be a positive POSitive customer id (got {customerId}).");

        var patch = ToPatch(changes);
        if (patch.Count == 0)
            return PosResult<PosCustomerRef>.Ok(new PosCustomerRef(customerId, null));

        var content = new StringContent(patch.ToJsonString(Json), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/merge-patch+json") { CharSet = "utf-8" };
        var result = await SendAsync<CustomerWriteResultDto>(HttpMethod.Patch,
            $"/api/v2/customers/{customerId.ToString(CultureInfo.InvariantCulture)}", content, cancellationToken);
        return result.Success
            ? PosResult<PosCustomerRef>.Ok(new PosCustomerRef(result.Value!.CustomerId ?? customerId, result.Value.ExternalId))
            : Rewrap<CustomerWriteResultDto, PosCustomerRef>(result);
    }

    public async Task<PosResult<PosAddressRef>> AddAddressAsync(
        PosAddress address, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address.LocalCustomerId))
            return Invalid<PosAddressRef>("localCustomerId", "Required: the customer the address belongs to.");

        var result = await SendAsync<AddressWriteResultDto>(HttpMethod.Post,
            $"/api/v2/customers/addresses?externalId={Q(address.LocalCustomerId)}", JsonContent(address), cancellationToken);
        return result.Success
            ? PosResult<PosAddressRef>.Ok(new PosAddressRef(result.Value!.CustomerId ?? 0, result.Value.AddressId, result.Value.LocalAddressId))
            : Rewrap<AddressWriteResultDto, PosAddressRef>(result);
    }

    // --- transactions ----------------------------------------------------

    public async Task<PosResult<PosTransactionCreated>> CreateTransactionAsync(
        PosTransaction transaction, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<TransactionCreatedDto>(HttpMethod.Post, "/api/v2/transactions",
            JsonContent(transaction), cancellationToken);
        if (result.Success)
            return PosResult<PosTransactionCreated>.Ok(new PosTransactionCreated(
                result.Value!.TransactionId ?? 0, result.Value.ExternalId, ToLines(result.Value.Lines)));

        // 422 no_writable_lines: nothing was written; keep each line's status (what the old gateway returned).
        if (result.ErrorCode == PosErrorCodes.NoWritableLines)
            return new PosResult<PosTransactionCreated>(false,
                new PosTransactionCreated(0, transaction.LocalTransactionId, ToLines(result.Problem?.Lines)),
                result.ErrorCode, result.Detail);
        // 409 transaction_exists: nothing was written; transactionId names the existing transaction (G13).
        if (result.ErrorCode == PosErrorCodes.TransactionExists && result.Problem?.TransactionId is > 0)
            return new PosResult<PosTransactionCreated>(false,
                new PosTransactionCreated(result.Problem.TransactionId.Value, transaction.LocalTransactionId, []),
                result.ErrorCode, result.Detail);
        return Rewrap<TransactionCreatedDto, PosTransactionCreated>(result);
    }

    public async Task<PosResult<PosPaymentPosted>> PostPaymentAsync(
        PosPayment payment, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PaymentCreatedDto>(HttpMethod.Post,
            $"/api/v2/transactions/payments?externalId={Q(payment.LocalTransactionId)}", JsonContent(payment), cancellationToken);
        return result.Success
            ? PosResult<PosPaymentPosted>.Ok(new PosPaymentPosted(result.Value!.TransactionId ?? 0, result.Value.PaymentId))
            : Rewrap<PaymentCreatedDto, PosPaymentPosted>(result);
    }

    // --- products --------------------------------------------------------

    public async Task<PosResult<PosProductPage>> ListProductsAsync(
        PosProductQuery query, CancellationToken cancellationToken = default)
    {
        var url = new StringBuilder("/api/v2/products?page=").Append(query.Page.ToString(CultureInfo.InvariantCulture))
            .Append("&pageSize=").Append(query.PageSize.ToString(CultureInfo.InvariantCulture));
        if (query.InvNo is { } invNo)
            url.Append("&invNo=").Append(invNo.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(query.Sku))
            url.Append("&sku=").Append(Q(query.Sku));
        if (query.WebstoreOnly)
            url.Append("&webstoreOnly=true");
        if (query.DivisionId != 0)
            url.Append("&divisionId=").Append(query.DivisionId.ToString(CultureInfo.InvariantCulture));
        if (query.ModifiedSince is { } since)
            url.Append("&modifiedSince=").Append(Q(DateTime.SpecifyKind(since.ToUniversalTime(), DateTimeKind.Utc)
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));

        var result = await GetAsync<PagedDto<ProductDto>>(url.ToString(), cancellationToken);
        if (!result.Success)
            return Rewrap<PagedDto<ProductDto>, PosProductPage>(result);
        var page = result.Value!;
        return PosResult<PosProductPage>.Ok(new PosProductPage(
            (page.Items ?? []).Select(ToPosProduct).ToList(), page.Page, page.PageSize, page.TotalItems));
    }

    public async Task<IReadOnlyList<int>> ResolveSkuAsync(string sku, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sku))
            return [];
        var result = await GetAsync<PagedDto<ProductDto>>($"/api/v2/products?sku={Q(sku.Trim())}&pageSize=100", cancellationToken);
        return OrThrow(result, "sku lookup").Items?.Select(p => p.ProductId).Distinct().ToList() ?? [];
    }

    public async Task<int?> FindLinkedProductAsync(string externalId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return null;
        var result = await GetAsync<PagedDto<ProductDto>>($"/api/v2/products?externalId={Q(externalId)}&pageSize=1", cancellationToken);
        return OrThrow(result, "product link lookup").Items?.FirstOrDefault()?.ProductId;
    }

    public async Task<string?> GetProductExternalIdAsync(int invNo, CancellationToken cancellationToken = default)
    {
        var product = await GetProductAsync(invNo, cancellationToken);
        return string.IsNullOrWhiteSpace(product?.ExternalId) ? null : product.ExternalId;
    }

    public async Task<PosResult<bool>> LinkProductAsync(int invNo, string externalId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return Invalid<bool>("localProductId", "Required.");

        // The endpoint checks both uniqueness rules and writes in one transaction:
        // 201 = linked now, 200 = already exactly this link, 409 link_exists = conflict.
        var result = await SendAsync<JsonElement>(HttpMethod.Put,
            $"/api/v2/products/{invNo.ToString(CultureInfo.InvariantCulture)}/link",
            JsonContent(new { localProductId = externalId.TrimEnd() }), cancellationToken);
        return result.Success
            ? PosResult<bool>.Ok(result.Status == HttpStatusCode.Created)
            : Rewrap<JsonElement, bool>(result);
    }

    public async Task<PosResult<bool>> UnlinkProductAsync(int invNo, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<JsonElement>(HttpMethod.Delete,
            $"/api/v2/products/{invNo.ToString(CultureInfo.InvariantCulture)}/link", null, cancellationToken);
        if (result.Success)
            return PosResult<bool>.Ok(true);
        return result.ErrorCode == PosErrorCodes.LinkNotFound ? PosResult<bool>.Ok(false) : Rewrap<JsonElement, bool>(result);
    }

    // --- HTTP ------------------------------------------------------------

    private async Task<ProductDto?> GetProductAsync(int invNo, CancellationToken cancellationToken)
    {
        var result = await GetAsync<PagedDto<ProductDto>>(
            $"/api/v2/products?invNo={invNo.ToString(CultureInfo.InvariantCulture)}&pageSize=1", cancellationToken);
        return OrThrow(result, "product lookup").Items?.FirstOrDefault(p => p.ProductId == invNo);
    }

    private Task<ApiResult<T>> GetAsync<T>(string pathAndQuery, CancellationToken cancellationToken) =>
        SendAsync<T>(HttpMethod.Get, pathAndQuery, null, cancellationToken);

    private async Task<ApiResult<T>> SendAsync<T>(
        HttpMethod method, string pathAndQuery, HttpContent? content, CancellationToken cancellationToken)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new ConnectorJobException(
                $"Connector '{_options.ConnectorId}' has no POSitive API key stored ({ConnectorSecretNames.PosApiKey(SafeId)}). Reconnect the store.",
                retryable: false);

        using var request = new HttpRequestMessage(method, BuildUri(pathAndQuery)) { Content = content };
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PosApiException(
                $"POSitive API {method} {pathAndQuery} timed out after {_options.RequestTimeout.TotalSeconds:0}s.", null, null, ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = (int)response.StatusCode;
            _logger.LogDebug("POSitive API {Method} {Path} -> {Status}", method, pathAndQuery, status);

            if (response.IsSuccessStatusCode)
            {
                var value = string.IsNullOrWhiteSpace(body) ? default : JsonSerializer.Deserialize<T>(body, Json);
                return new ApiResult<T>(true, value, null, null, null, response.StatusCode);
            }

            var problem = TryParseProblem(body);
            var errorCode = problem?.ErrorCode ?? $"http_{status}";
            var detail = problem?.Detail ?? problem?.Title ?? response.ReasonPhrase;

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ConnectorJobException(
                    $"POSitive API rejected connector '{_options.ConnectorId}' key ({status} {errorCode}): {detail} Reconnect the store to issue a new key.",
                    retryable: false);
            if (status >= 500 || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout)
                throw new PosApiException($"POSitive API {method} {pathAndQuery} answered {status} {errorCode}: {detail}",
                    response.StatusCode, errorCode);

            return new ApiResult<T>(false, default, errorCode, detail, problem);
        }
    }

    private Uri BuildUri(string pathAndQuery) =>
        new(_options.BaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + pathAndQuery, UriKind.Absolute);

    private string SafeId => string.IsNullOrWhiteSpace(_options.ConnectorId) ? "?" : _options.ConnectorId;

    private static string Q(string value) => Uri.EscapeDataString(value);

    private static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json");

    private static ProblemDto? TryParseProblem(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ProblemDto>(body, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads that have no PosResult shape: a refused read is a bug or a broken server, not data — throw.</summary>
    private static T OrThrow<T>(ApiResult<T> result, string what) =>
        result.Success && result.Value is not null
            ? result.Value
            : throw new PosApiException($"POSitive API {what} failed: {result.ErrorCode}: {result.Detail}", null, result.ErrorCode);

    private static PosResult<TOut> Rewrap<TIn, TOut>(ApiResult<TIn> result) =>
        PosResult<TOut>.Fail(result.ErrorCode ?? "error", result.Detail, result.Problem?.Errors);

    private static PosResult<T> Invalid<T>(string field, string message) =>
        PosResult<T>.Fail(PosErrorCodes.ValidationFailed, ValidationDetail,
            new Dictionary<string, string[]> { [field] = [message] });

    private static IReadOnlyList<PosLineResult> ToLines(IReadOnlyList<LineResultDto>? lines) =>
        lines?.Select(l => new PosLineResult(l.LocalLineId ?? l.LineId, l.Status ?? "")).ToList() ?? [];

    /// <summary>The JSON Merge Patch an API caller would send: every non-null field under its wire name; localCustomerId is the caller's reference, never patched.</summary>
    internal static JsonObject ToPatch(PosCustomer changes) =>
        JsonSerializer.SerializeToNode(changes with { LocalCustomerId = null }, Json)!.AsObject();

    private static PosProduct ToPosProduct(ProductDto p) => new(
        p.ProductId, p.ExternalId, p.Sku, p.Description, p.LongDescription, p.ParentProductId,
        p.WebProduct, p.Available, p.InStock, p.HeldInStock,
        (p.Prices ?? []).Select(x => new PosPrice(x.Level, x.Price, x.SalePrice)).ToList(),
        p.DivisionId, p.AvailableLive, p.ExtendedDescription, p.MatrixItems?.Count ?? 0);

    // --- wire shapes (docs/openapi/v2.json; only the fields the connector reads) ---

    private sealed record ApiResult<T>(
        bool Success, T? Value, string? ErrorCode, string? Detail, ProblemDto? Problem, HttpStatusCode? Status = null);

    /// <summary>
    /// <see cref="TransactionId"/>: on 409 transaction_exists and 404 transaction_invoiced, the existing
    /// transaction's number; <see cref="InvoiceTransactionNumbers"/>: on 404 transaction_invoiced, its invoices (newest first).
    /// </summary>
    internal sealed record ProblemDto(
        string? Title, string? Detail, string? ErrorCode, Dictionary<string, string[]>? Errors, IReadOnlyList<LineResultDto>? Lines,
        int? TransactionId = null, IReadOnlyList<int>? InvoiceTransactionNumbers = null);

    internal sealed record LineResultDto(string? LocalLineId, string? LineId, string? Status);

    internal sealed record PagedDto<T>(IReadOnlyList<T>? Items, int Page, int PageSize, int TotalItems);

    internal sealed record CustomerListDto(int CustomerId, string? ExternalId);

    internal sealed record CustomerWriteResultDto(int? CustomerId, string? ExternalId);

    internal sealed record AddressWriteResultDto(int? CustomerId, int? AddressId, string? LocalAddressId);

    internal sealed record TransactionCreatedDto(int? TransactionId, string? ExternalId, IReadOnlyList<LineResultDto>? Lines);

    internal sealed record PaymentCreatedDto(int? TransactionId, int? PaymentId);

    internal sealed record PriceDto(int Level, decimal? Price, decimal? SalePrice);

    internal sealed record ProductDto(
        int ProductId, string? ExternalId, string? Sku, string? Description, string? LongDescription,
        int? ParentProductId, bool? WebProduct, decimal? Available, decimal? InStock, decimal? HeldInStock,
        IReadOnlyList<PriceDto>? Prices, int? DivisionId, decimal? AvailableLive = null,
        string? ExtendedDescription = null, IReadOnlyList<JsonElement>? MatrixItems = null);
}

/// <summary>Builds the <see cref="IPosGateway"/> for one connector (its base URL, its key).</summary>
public interface IPosGatewayFactory
{
    IPosGateway Create(ConnectorSettings settings);
}

/// <summary>
/// <see cref="PosApiClient"/>s over a named HttpClient, with the key read from
/// the add-on's DPAPI store under <see cref="ConnectorSecretNames.PosApiKey"/>.
/// </summary>
public sealed class PosApiClientFactory : IPosGatewayFactory
{
    /// <summary>Named HttpClient registration.</summary>
    public const string HttpClientName = "positive-api";

    private readonly IHttpClientFactory _httpClients;
    private readonly IConnectorSecretStore _secrets;
    private readonly ILoggerFactory _loggerFactory;
    private readonly PosApiEndpoint _endpoint;

    /// <param name="endpoint">The host's POSitive address (loopback only, <see cref="PosApiAddress.Require"/>); default http://localhost:5210.</param>
    public PosApiClientFactory(IHttpClientFactory httpClients, IConnectorSecretStore secrets, ILoggerFactory loggerFactory,
        PosApiEndpoint? endpoint = null)
    {
        _httpClients = httpClients;
        _secrets = secrets;
        _loggerFactory = loggerFactory;
        _endpoint = endpoint ?? PosApiEndpoint.Default;
    }

    /// <summary>
    /// The primary handler for <see cref="HttpClientName"/>: the requests carry the connector's API
    /// key, so a redirect is never followed (it would re-send the key elsewhere), and no proxy or
    /// cookies are used for the call to this machine.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    public IPosGateway Create(ConnectorSettings settings)
    {
        var secretName = ConnectorSecretNames.PosApiKey(settings.Id);
        return new PosApiClient(
            _httpClients.CreateClient(HttpClientName),
            new PosApiClientOptions { BaseUrl = _endpoint.BaseUrl, ConnectorId = settings.Id },
            () => _secrets.Get(secretName),
            _loggerFactory.CreateLogger<PosApiClient>());
    }
}
