using System.Net;
using System.Text;
using System.Text.Json;
using PA.Connectors.Core;
using PA.Connectors.Core.Pos;
using Xunit;

namespace PA.Tests.Connectors;

/// <summary>
/// PosApiClient = the connector's IPosGateway over the public /api/v2 (design
/// §0). A fake handler stands in for PA.Server: routes, the key header, wire
/// names, and the errorCode → PosResult mapping the in-process gateway had.
/// </summary>
public class PosApiClientTests
{
    private sealed record Sent(HttpMethod Method, Uri Uri, string? ApiKey, string? ContentType, string? Body);

    private sealed class FakeApi : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
        public List<Sent> Requests { get; } = [];

        public FakeApi Then(HttpStatusCode status, object? body = null, string contentType = "application/json")
        {
            _responses.Enqueue(_ => Response(status, body, contentType));
            return this;
        }

        public FakeApi ThenProblem(HttpStatusCode status, string errorCode, string detail, object? extra = null)
        {
            var problem = new Dictionary<string, object?> { ["status"] = (int)status, ["title"] = "x", ["detail"] = detail, ["errorCode"] = errorCode };
            if (extra is not null)
                foreach (var p in JsonSerializer.SerializeToElement(extra).EnumerateObject())
                    problem[p.Name] = p.Value;
            return Then(status, problem, "application/problem+json");
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Sent(request.Method, request.RequestUri!,
                request.Headers.TryGetValues("X-Api-Key", out var key) ? key.Single() : null,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (_responses.Count == 0)
                throw new InvalidOperationException($"unexpected request {request.Method} {request.RequestUri}");
            return _responses.Dequeue()(request);
        }

        private static HttpResponseMessage Response(HttpStatusCode status, object? body, string contentType) =>
            new(status)
            {
                Content = body is null
                    ? new StringContent("")
                    : new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                        Encoding.UTF8, contentType),
            };
    }

    private readonly FakeApi _api = new();
    private string? _key = "pa-key-shop1";

    private PosApiClient Client(string baseUrl = "http://localhost:5210") =>
        new(new HttpClient(_api), new PosApiClientOptions { BaseUrl = new Uri(baseUrl), ConnectorId = "shop1" }, () => _key);

    private Sent Only() => Assert.Single(_api.Requests);

    // --- requests ----------------------------------------------------------

    [Fact]
    public async Task CreateCustomer_PostsCamelCaseWithoutNulls_WithTheConnectorsKey()
    {
        _api.Then(HttpStatusCode.Created, new { customerId = 1234, externalId = "woo-c3" });

        var result = await Client("http://pos.local:5210/").CreateCustomerAsync(
            new PosCustomer { LocalCustomerId = "woo-c3", Email = "a@b.c", FirstName = "Ann", TaxGroupId = 2 });

        Assert.True(result.Success);
        Assert.Equal(new PosCustomerRef(1234, "woo-c3"), result.Value);
        var sent = Only();
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("http://pos.local:5210/api/v2/customers", sent.Uri.ToString());
        Assert.Equal("pa-key-shop1", sent.ApiKey);
        Assert.Equal("application/json", sent.ContentType);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.Equal(["localCustomerId", "email", "firstName", "taxGroupId"],
            body.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task FindCustomer_ListsByExternalId_EmptyIsNotFound()
    {
        _api.Then(HttpStatusCode.OK, new { items = new[] { new { customerId = 77, externalId = "woo-c3" } }, page = 1, pageSize = 1, totalItems = 1 })
            .Then(HttpStatusCode.OK, new { items = Array.Empty<object>(), page = 1, pageSize = 1, totalItems = 0 });
        var client = Client();

        var found = await client.FindCustomerAsync("woo c3");
        var missing = await client.FindCustomerAsync("woo-c9");

        Assert.Equal(new PosCustomerRef(77, "woo c3"), found.Value);
        Assert.Equal("/api/v2/customers?externalId=woo%20c3&pageSize=1", _api.Requests[0].Uri.PathAndQuery);
        Assert.False(missing.Success);
        Assert.Equal("customer_not_found", missing.ErrorCode);
        Assert.Equal("customer_not_found", (await client.FindCustomerAsync(" ")).ErrorCode);
        Assert.Equal(2, _api.Requests.Count); // the blank id made no call
    }

    [Fact]
    public async Task PatchCustomer_SendsOnlyNonNullFields_AsMergePatch_AndSkipsAnEmptyPatch()
    {
        _api.Then(HttpStatusCode.OK, new { customerId = 77, externalId = "woo-c3" });
        var client = Client();

        var result = await client.PatchCustomerAsync(77, new PosCustomer { LocalCustomerId = "woo-c3", Email = "new@b.c" });
        var nothing = await client.PatchCustomerAsync(77, new PosCustomer { LocalCustomerId = "woo-c3" });
        var invalid = await client.PatchCustomerAsync(0, new PosCustomer { Email = "x" });

        Assert.True(result.Success);
        var sent = Only();
        Assert.Equal(HttpMethod.Patch, sent.Method);
        Assert.Equal("/api/v2/customers/77", sent.Uri.AbsolutePath);
        Assert.Equal("application/merge-patch+json", sent.ContentType);
        Assert.Equal("{\"email\":\"new@b.c\"}", sent.Body);
        Assert.Equal(new PosCustomerRef(77, null), nothing.Value);
        Assert.Equal("invalid_customer_id", invalid.ErrorCode);
    }

    [Fact]
    public async Task AddAddress_AddressesTheCustomerByExternalId()
    {
        _api.Then(HttpStatusCode.OK, new { customerId = 77, addressId = 3, localAddressId = "ship-1" });

        var result = await Client().AddAddressAsync(new PosAddress
        {
            LocalCustomerId = "woo-c3", LocalAddressId = "ship-1", Type = "Shipping", City = "Leeds",
        });

        Assert.Equal(new PosAddressRef(77, 3, "ship-1"), result.Value);
        Assert.Equal("/api/v2/customers/addresses?externalId=woo-c3", Only().Uri.PathAndQuery);
        Assert.Equal("validation_failed", (await Client().AddAddressAsync(new PosAddress { City = "x" })).ErrorCode);
    }

    [Fact]
    public async Task CreateTransaction_MapsCreated_AndTheLinesOfANoWritableLinesRejection()
    {
        _api.Then(HttpStatusCode.Created, new { transactionId = 50001556, externalId = "woo-58", lines = new[] { new { localLineId = "l1", status = "Success" } } })
            .ThenProblem((HttpStatusCode)422, "no_writable_lines", "All lines skipped",
                new { lines = new[] { new { localLineId = "l1", status = "product not found" } } })
            .ThenProblem(HttpStatusCode.Conflict, "transaction_exists", "Transaction already exists");
        var client = Client();
        var transaction = new PosTransaction
        {
            LocalTransactionId = "woo-58", LocalCustomerId = "woo-c3", Total = 10m, TaxAmount = 2m,
            ShippingAddress = new PosShipTo { LocalAddressId = "woo-o58-ship", Address1 = "1 Main St", City = "Leeds" },
            Lines = [new PosTransactionLine { LocalLineId = "l1", Sku = "CAP", Quantity = 1, Price = 10m, LineTotal = 10m }],
        };

        var created = await client.CreateTransactionAsync(transaction);
        var skipped = await client.CreateTransactionAsync(transaction);
        var duplicate = await client.CreateTransactionAsync(transaction);

        Assert.Equal(50001556, created.Value!.TransactionId);
        Assert.Equal([new PosLineResult("l1", "Success")], created.Value.Lines);
        Assert.Equal("/api/v2/transactions", _api.Requests[0].Uri.AbsolutePath);
        using (var body = JsonDocument.Parse(_api.Requests[0].Body!))
        {
            Assert.Equal("woo-58", body.RootElement.GetProperty("localTransactionId").GetString());
            Assert.Equal("CAP", body.RootElement.GetProperty("lines")[0].GetProperty("sku").GetString());
            Assert.Equal("woo-o58-ship", body.RootElement.GetProperty("shippingAddress").GetProperty("localAddressId").GetString());
            Assert.False(body.RootElement.GetProperty("shippingAddress").TryGetProperty("company", out _)); // nulls omitted
        }
        Assert.False(skipped.Success);
        Assert.Equal("no_writable_lines", skipped.ErrorCode);
        Assert.Equal([new PosLineResult("l1", "product not found")], skipped.Value!.Lines);
        Assert.Equal("woo-58", skipped.Value.LocalTransactionId);
        Assert.Equal("transaction_exists", duplicate.ErrorCode);
        Assert.Equal("Transaction already exists", duplicate.Detail);
    }

    [Fact]
    public async Task CreateTransaction_409_CarriesTheExistingTransactionId()
    {
        _api.ThenProblem(HttpStatusCode.Conflict, "transaction_exists", "Transaction already exists", new { transactionId = 50001921 });

        var duplicate = await Client().CreateTransactionAsync(new PosTransaction { LocalTransactionId = "woo-58", LocalCustomerId = "woo-c3" });

        Assert.False(duplicate.Success);
        Assert.Equal("transaction_exists", duplicate.ErrorCode);
        Assert.Equal(50001921, duplicate.Value!.TransactionId);
        Assert.Equal("woo-58", duplicate.Value.LocalTransactionId);
    }

    [Fact]
    public async Task FindTransaction_ByExternalId_Pending_ThenLines()
    {
        _api.Then(HttpStatusCode.OK, new { items = new[] { new { transactionNumber = 50001921, externalId = "woo-58", total = 10m, taxAmount = 2m } }, page = 1, pageSize = 1, totalItems = 1 })
            .Then(HttpStatusCode.OK, new { transaction = new { transactionNumber = 50001921 }, lines = new[] { new { productId = (int?)7 }, new { productId = (int?)null } } });

        var found = await Client().FindTransactionAsync(new PosTransactionLookup("woo-58", "woo-c3", "58", 10m, 2m));

        Assert.Equal(new PosTransactionFound(50001921, 10m, 2m, found.Value!.ProductIds, "pending: externalId"), found.Value);
        Assert.Equal([7, null], found.Value.ProductIds!);
        Assert.Equal("/api/v2/transactions/pending?externalId=woo-58&pageSize=1", _api.Requests[0].Uri.PathAndQuery);
        Assert.Equal("/api/v2/transactions/pending/50001921", _api.Requests[1].Uri.PathAndQuery);
    }

    [Fact]
    public async Task FindTransaction_Invoiced_ReadsTheInvoiceThe404Names()
    {
        _api.ThenProblem(HttpStatusCode.NotFound, "transaction_invoiced", "invoiced as 50001944",
                new { transactionId = 50001921, invoiceTransactionNumbers = new[] { 50001944 } })
            .Then(HttpStatusCode.OK, new
            {
                invoice = new { transactionNumber = 50001944, originalPendingLocalTransactionId = "woo-58", originalPendingTransactionNumber = 50001921, total = 9m, taxAmount = 1.5m },
                lines = Array.Empty<object>(),
            });

        var found = await Client().FindTransactionAsync(new PosTransactionLookup("woo-58", "woo-c3", "58", 10m, 2m));

        Assert.Equal(new PosTransactionFound(50001921, 9m, 1.5m, null, "invoice: originalPendingLocalTransactionId"), found.Value);
        Assert.Equal("/api/v2/invoices/50001944", _api.Requests[1].Uri.PathAndQuery);
    }

    [Fact]
    public async Task FindTransaction_NotFound_OrAServerWithoutTheFilter_IsTransactionNotFound()
    {
        _api.ThenProblem(HttpStatusCode.NotFound, "transaction_not_found", "No pending transaction is linked to localTransactionId 'woo-58' for your key.")
            // A server older than the externalId filter ignores it and answers the newest rows.
            .Then(HttpStatusCode.OK, new { items = new[] { new { transactionNumber = 1, total = 10m, taxAmount = 2m } }, page = 1, pageSize = 1, totalItems = 900 });
        var lookup = new PosTransactionLookup("woo-58", "woo-c3", "58", 10m, 2m);

        var missing = await Client().FindTransactionAsync(lookup);
        var oldServer = await Client().FindTransactionAsync(lookup);

        Assert.Equal("transaction_not_found", missing.ErrorCode);
        Assert.Contains("woo-58", missing.Detail);
        Assert.Equal("transaction_not_found", oldServer.ErrorCode);
        Assert.Equal(2, _api.Requests.Count); // no detail read for a row that is not ours
    }

    [Fact]
    public async Task ValidationFailed_CarriesTheFieldErrors()
    {
        _api.ThenProblem(HttpStatusCode.BadRequest, "validation_failed", "See errors",
            new { errors = new Dictionary<string, string[]> { ["email"] = ["Too long."] } });

        var result = await Client().CreateCustomerAsync(new PosCustomer { LocalCustomerId = "c", Email = new string('x', 300) });

        Assert.Equal("validation_failed", result.ErrorCode);
        Assert.Equal(["Too long."], result.ValidationErrors!["email"]);
        Assert.Contains("email: Too long.", result.Describe());
    }

    [Fact]
    public async Task PostPayment_UsesTheTransactionsExternalId()
    {
        _api.Then(HttpStatusCode.OK, new { transactionId = 50001556, paymentId = 9 })
            .ThenProblem(HttpStatusCode.NotFound, "transaction_not_found", "Transaction not found");
        var client = Client();

        var posted = await client.PostPaymentAsync(new PosPayment { LocalTransactionId = "woo-58", PaymentType = "R", Amount = 12m });
        var missing = await client.PostPaymentAsync(new PosPayment { LocalTransactionId = "woo-59", PaymentType = "R", Amount = 12m });

        Assert.Equal(new PosPaymentPosted(50001556, 9), posted.Value);
        Assert.Equal("/api/v2/transactions/payments?externalId=woo-58", _api.Requests[0].Uri.PathAndQuery);
        Assert.Equal("transaction_not_found", missing.ErrorCode);
    }

    [Fact]
    public async Task PostPayment_SendsLocalPaymentId_AndAReplayIsPaymentExists()
    {
        _api.ThenProblem(HttpStatusCode.Conflict, "payment_exists", "payment already exists",
            new { transactionId = 50001556, paymentId = 9, localPaymentId = "woo-58-pay" });

        var replay = await Client().PostPaymentAsync(new PosPayment
        {
            LocalTransactionId = "woo-58", PaymentType = "R", Amount = 12m, LocalPaymentId = "woo-58-pay",
        });

        Assert.False(replay.Success);
        Assert.Equal(PosErrorCodes.PaymentExists, replay.ErrorCode);
        using var body = JsonDocument.Parse(Only().Body!);
        Assert.Equal("woo-58-pay", body.RootElement.GetProperty("localPaymentId").GetString());
    }

    [Fact]
    public async Task ListProducts_BuildsTheQuery_AndMapsTheProductDto()
    {
        _api.Then(HttpStatusCode.OK, new
        {
            items = new[]
            {
                new
                {
                    productId = 3091, externalId = "27", sku = "WOO-HOODIE-RED", description = "Hoodie", longDescription = "Warm",
                    parentProductId = 3090, webProduct = true, available = 4.5m, availableLive = 2.5m, inStock = 6m, heldInStock = 1.5m, divisionId = 1,
                    prices = new[] { new { level = 1, price = 30m, salePrice = (decimal?)null } },
                },
            },
            page = 2, pageSize = 50, totalItems = 51, totalPages = 2,
        }).ThenProblem(HttpStatusCode.BadRequest, "modified_since_unsupported", "old procs");
        var client = Client();

        var page = await client.ListProductsAsync(new PosProductQuery
        {
            Page = 2, PageSize = 50, WebstoreOnly = true, ModifiedSince = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc),
        });
        var unsupported = await client.ListProductsAsync(new PosProductQuery { ModifiedSince = DateTime.UtcNow });

        Assert.Equal("/api/v2/products?page=2&pageSize=50&webstoreOnly=true&modifiedSince=2026-10-08T12%3A00%3A00Z",
            _api.Requests[0].Uri.PathAndQuery);
        var product = Assert.Single(page.Value!.Items);
        Assert.Equal(new PosProduct(3091, "27", "WOO-HOODIE-RED", "Hoodie", "Warm", 3090, true, 4.5m, 6m, 1.5m,
            product.Prices, 1, AvailableLive: 2.5m), product);
        Assert.Equal([new PosPrice(1, 30m, null)], product.Prices);
        Assert.Equal((2, 50, 51), (page.Value.Page, page.Value.PageSize, page.Value.TotalItems));
        Assert.Equal("modified_since_unsupported", unsupported.ErrorCode);
    }

    [Fact]
    public async Task ResolveSku_AndLinkReads_UseTheProductFilters()
    {
        _api.Then(HttpStatusCode.OK, new { items = new[] { new { productId = 5 }, new { productId = 5 }, new { productId = 8 } }, page = 1, pageSize = 100, totalItems = 3 })
            .Then(HttpStatusCode.OK, new { items = new[] { new { productId = 5 } }, page = 1, pageSize = 1, totalItems = 1 })
            .Then(HttpStatusCode.OK, new { items = new[] { new { productId = 5, externalId = "27" } }, page = 1, pageSize = 1, totalItems = 1 });
        var client = Client();

        Assert.Equal([5, 8], await client.ResolveSkuAsync(" 0123 "));
        Assert.Equal(5, await client.FindLinkedProductAsync("27"));
        Assert.Equal("27", await client.GetProductExternalIdAsync(5));
        Assert.Equal(
            ["/api/v2/products?sku=0123&pageSize=100", "/api/v2/products?externalId=27&pageSize=1", "/api/v2/products?invNo=5&pageSize=1"],
            _api.Requests.Select(r => r.Uri.PathAndQuery));
        Assert.Empty(await client.ResolveSkuAsync(""));
    }

    [Fact]
    public async Task LinkProduct_PutsTheLink_201IsNew_200IsUnchanged_409IsLinkExists()
    {
        var client = Client();
        _api.Then(HttpStatusCode.Created, new { productId = 5, localProductId = "27" })
            .Then(HttpStatusCode.OK, new { productId = 5, localProductId = "27" })
            .ThenProblem(HttpStatusCode.Conflict, "link_exists", "localProductId '27' is already linked to product 8", new { productId = 8, localProductId = "27" })
            .ThenProblem(HttpStatusCode.NotFound, "product_not_found", "Product does not exist");

        Assert.Equal(PosResult<bool>.Ok(true), await client.LinkProductAsync(5, "27 "));
        Assert.Equal(PosResult<bool>.Ok(false), await client.LinkProductAsync(5, "27"));
        Assert.Equal(PosErrorCodes.LinkExists, (await client.LinkProductAsync(5, "27")).ErrorCode);
        Assert.Equal(PosErrorCodes.ProductNotFound, (await client.LinkProductAsync(5, "27")).ErrorCode);

        var put = _api.Requests[0];
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal("/api/v2/products/5/link", put.Uri.AbsolutePath);
        Assert.Equal("{\"localProductId\":\"27\"}", put.Body);
        Assert.Equal(PosErrorCodes.ValidationFailed, (await client.LinkProductAsync(5, " ")).ErrorCode);
        Assert.Equal(4, _api.Requests.Count); // the blank id made no call
    }

    [Fact]
    public async Task UnlinkProduct_204IsRemoved_LinkNotFoundIsNothingToDo()
    {
        var client = Client();
        _api.Then(HttpStatusCode.NoContent)
            .ThenProblem(HttpStatusCode.NotFound, "link_not_found", "no link")
            .ThenProblem(HttpStatusCode.NotFound, "product_not_found", "Product does not exist");

        Assert.Equal(PosResult<bool>.Ok(true), await client.UnlinkProductAsync(5));
        Assert.Equal(PosResult<bool>.Ok(false), await client.UnlinkProductAsync(5));
        Assert.Equal(PosErrorCodes.ProductNotFound, (await client.UnlinkProductAsync(9)).ErrorCode);
        Assert.Equal(HttpMethod.Delete, _api.Requests[0].Method);
        Assert.Equal("/api/v2/products/5/link", _api.Requests[0].Uri.AbsolutePath);
    }

    // --- failures that are not data ----------------------------------------

    [Fact]
    public async Task RejectedKey_IsAPermanentJobFailure()
    {
        _api.ThenProblem(HttpStatusCode.Unauthorized, "invalid_api_key", "Unknown key");

        var error = await Assert.ThrowsAsync<ConnectorJobException>(() => Client().CreateCustomerAsync(new PosCustomer()));

        Assert.False(error.Retryable);
        Assert.Contains("invalid_api_key", error.Message);
    }

    [Fact]
    public async Task MissingKey_FailsPermanently_WithoutCallingTheServer()
    {
        _key = null;

        var error = await Assert.ThrowsAsync<ConnectorJobException>(() => Client().FindCustomerAsync("c"));

        Assert.False(error.Retryable);
        Assert.Contains("connector-shop1-pos-api-key", error.Message);
        Assert.Empty(_api.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "internal_error")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "http_503")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited")]
    public async Task ServerFaultsAndThrottling_AreTransient(HttpStatusCode status, string errorCode)
    {
        if (errorCode.StartsWith("http_", StringComparison.Ordinal))
            _api.Then(status);
        else
            _api.ThenProblem(status, errorCode, "later");

        var error = await Assert.ThrowsAsync<PosApiException>(() => Client().CreateTransactionAsync(
            new PosTransaction { LocalTransactionId = "woo-58" }));

        Assert.Equal(status, error.Status);
        Assert.Equal(errorCode, error.ErrorCode);
    }

    [Fact]
    public async Task DatabaseUpgradeRequired_IsTransient_AndKeepsItsErrorCode()
    {
        _api.ThenProblem(HttpStatusCode.ServiceUnavailable, "database_upgrade_required",
            "This store's database needs upgrading before orders can be taken.", new { requiredSchemaVersion = 2, schemaVersion = 1 });

        var error = await Assert.ThrowsAsync<PosApiException>(() => Client().CreateTransactionAsync(
            new PosTransaction { LocalTransactionId = "woo-58" }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.Status);
        Assert.Equal(PosErrorCodes.DatabaseUpgradeRequired, error.ErrorCode);
        Assert.Contains("needs upgrading", error.Message);
    }

    [Fact]
    public async Task Timeout_IsTransient()
    {
        var slow = new SlowHandler();
        var client = new PosApiClient(new HttpClient(slow),
            new PosApiClientOptions { BaseUrl = new Uri("http://localhost:5210"), RequestTimeout = TimeSpan.FromMilliseconds(50) },
            () => "k");

        var error = await Assert.ThrowsAsync<PosApiException>(() => client.FindCustomerAsync("c"));
        Assert.Contains("timed out", error.Message);
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Factory_BindsTheHostsBaseUrlAndTheConnectorsKeyName()
    {
        var settings = new ConnectorSettings { Id = "shop1" };
        var secrets = new DictionarySecrets { ["connector-shop1-pos-api-key"] = "k1" };
        var factory = new PosApiClientFactory(new SingleClientFactory(_api), secrets,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, PosApiAddress.Require("http://127.0.0.1:5299/"));

        _api.Then(HttpStatusCode.OK, new { items = Array.Empty<object>(), page = 1, pageSize = 1, totalItems = 0 });
        _ = await factory.Create(settings).FindCustomerAsync("c");

        var sent = Only();
        Assert.Equal("127.0.0.1:5299", sent.Uri.Authority);
        Assert.Equal("k1", sent.ApiKey);
    }

    [Theory]
    [InlineData(null, "http://localhost:5210/")]
    [InlineData("", "http://localhost:5210/")]
    [InlineData("http://localhost:5211", "http://localhost:5211/")]
    [InlineData("https://127.0.0.1:8443/", "https://127.0.0.1:8443/")]
    [InlineData("http://[::1]:5210", "http://[::1]:5210/")]
    public void H1_ThePosApiAddress_IsThisMachine(string? configured, string expected)
    {
        Assert.Equal(new Uri(expected), PosApiAddress.Require(configured).BaseUrl);
    }

    [Theory]
    [InlineData("https://attacker.example")]
    [InlineData("http://10.0.0.5:5210")]
    [InlineData("http://user:pw@localhost:5210")]
    [InlineData("http://localhost:5210/?x=1")]
    [InlineData("ftp://localhost")]
    [InlineData("localhost:5210")]
    public void H1_AnyOtherPosApiAddress_RefusesToStart(string configured)
    {
        Assert.Throws<InvalidOperationException>(() => PosApiAddress.Require(configured));
    }

    [Fact]
    public async Task H1_TheConnectorHost_RefusesToStartWithANonLoopbackPosApiAddress()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PA.Connectors.Host.ConnectorHostApp.BuildAsync(
            ["--Connectors:PosApiBaseUrl=https://attacker.example", "--Logging:Directory=" + Path.GetTempPath()]));

        Assert.Contains("Connectors:PosApiBaseUrl", error.Message);
    }

    [Fact]
    public async Task H1_TheWizardsKeyCheck_GoesToTheHostsAddress()
    {
        var verifier = new PA.Connectors.Host.Ui.PosKeyVerifier(new SingleClientFactory(_api), PosApiAddress.Require("http://127.0.0.1:5299"),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        _api.Then(HttpStatusCode.OK, new { developerId = "WOO-shop1", settingsId = 1 });

        var check = await verifier.VerifyAsync("k1", CancellationToken.None);

        var sent = Only();
        Assert.Equal("http://127.0.0.1:5299/api/whoami", sent.Uri.ToString());
        Assert.Equal("k1", sent.ApiKey);
        Assert.True(check.Ok);
    }

    [Fact]
    public void H1_ThePosClient_NeverFollowsARedirect()
    {
        using var handler = PosApiClientFactory.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DictionarySecrets : Dictionary<string, string>, PA.Connectors.Core.Secrets.IConnectorSecretStore
    {
        public string? Get(string name) => TryGetValue(name, out var value) ? value : null;
        public void Set(string name, string value) => this[name] = value;
        bool PA.Connectors.Core.Secrets.IConnectorSecretStore.Remove(string name) => Remove(name);
        public IReadOnlyList<string> List() => Keys.ToList();
    }
}
