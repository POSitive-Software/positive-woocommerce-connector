using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PA.Connectors.WooCommerce;
using PA.Connectors.WooCommerce.Models;
using Xunit;

namespace PA.Tests.Connectors;

public class WooClientTests
{
    /// <summary>Scripted handler: each request is recorded and answered by the next responder.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _responders = new();

        public List<(HttpMethod Method, Uri Uri, string? Body, string? Auth)> Requests { get; } = [];

        public FakeHandler Then(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responders.Enqueue(r => Task.FromResult(responder(r)));
            return this;
        }

        public FakeHandler ThenAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responders.Enqueue(r => responder(r, _currentToken));
            return this;
        }

        private CancellationToken _currentToken;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.Authorization?.ToString()));
            if (_responders.Count == 0)
                throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
            _currentToken = cancellationToken;
            return await _responders.Dequeue()(request);
        }
    }

    private readonly FakeHandler _handler = new();
    private readonly List<TimeSpan> _delays = [];

    private WooClient Client(int maxRetries = 3, TimeSpan? timeout = null, int pageSize = 2) => new(
        new HttpClient(_handler),
        new WooClientOptions
        {
            StoreUrl = new Uri("https://shop.example.com/"),
            ConsumerKey = "ck_test",
            ConsumerSecret = "cs_test",
            MaxRetries = maxRetries,
            RequestTimeout = timeout ?? TimeSpan.FromSeconds(30),
            RetryBaseDelay = TimeSpan.FromSeconds(1),
            MaxRetryDelay = TimeSpan.FromSeconds(30),
            PageSize = pageSize,
        },
        logger: null,
        delay: (d, _) => { _delays.Add(d); return Task.CompletedTask; });

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK,
        int? totalPages = null, int? total = null, string? link = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (totalPages is not null)
            response.Headers.Add("X-WP-TotalPages", totalPages.Value.ToString());
        if (total is not null)
            response.Headers.Add("X-WP-Total", total.Value.ToString());
        if (link is not null)
            response.Headers.Add("Link", link);
        return response;
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code = "some_error", string message = "boom") =>
        Json(new { code, message, data = new { status = (int)status } }, status);

    private static object[] Products(params long[] ids) => ids.Select(id => (object)new { id, sku = $"sku-{id}" }).ToArray();

    [Fact]
    public void BaseUri_IsWcV3UnderTheStore()
    {
        Assert.Equal("https://shop.example.com/wp-json/wc/v3/", WooClient.BaseUriFor(new Uri("https://shop.example.com")).ToString());
        Assert.Equal("https://x.test/shop/wp-json/wc/v3/", WooClient.BaseUriFor(new Uri("https://x.test/shop/")).ToString());
    }

    [Fact]
    public async Task Requests_CarryBasicAuth_AndJsonAccept()
    {
        _handler.Then(_ => Json(WooFixtures.Text("product-simple.json")));

        var product = await Client().GetProductAsync(16);

        Assert.Equal(16, product!.Id);
        var (method, uri, _, auth) = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal("https://shop.example.com/wp-json/wc/v3/products/16", uri.ToString());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ck_test:cs_test")), auth);
    }

    [Fact]
    public async Task GetProduct_404_IsNull()
    {
        _handler.Then(_ => Error(HttpStatusCode.NotFound, "woocommerce_rest_product_invalid_id", "Invalid ID."));
        Assert.Null(await Client().GetProductAsync(999));
    }

    [Fact]
    public async Task NoRoute_404_Throws_ItIsNotAWooStore()
    {
        _handler.Then(_ => Error(HttpStatusCode.NotFound, "rest_no_route", "No route was found"));
        var ex = await Assert.ThrowsAsync<WooApiException>(() => Client().GetProductAsync(1));
        Assert.Equal("rest_no_route", ex.Code);
    }

    [Fact]
    public async Task Paging_FollowsTotalPagesHeader()
    {
        _handler
            .Then(_ => Json(Products(1, 2), totalPages: 3, total: 5))
            .Then(_ => Json(Products(3, 4), totalPages: 3, total: 5))
            .Then(_ => Json(Products(5), totalPages: 3, total: 5));

        var ids = new List<long>();
        await foreach (var p in Client().GetAllProductsAsync())
            ids.Add(p.Id);

        Assert.Equal([1L, 2, 3, 4, 5], ids);
        Assert.Equal(3, _handler.Requests.Count);
        Assert.Contains("page=1", _handler.Requests[0].Uri.Query);
        Assert.Contains("per_page=2", _handler.Requests[0].Uri.Query);
        Assert.Contains("page=3", _handler.Requests[2].Uri.Query);
    }

    [Fact]
    public async Task Paging_FallsBackToLinkHeader_WhenTotalPagesIsMissing()
    {
        _handler
            .Then(_ => Json(Products(1), link: "<https://shop.example.com/wp-json/wc/v3/products?page=2>; rel=\"next\""))
            .Then(_ => Json(Products(2), link: "<https://shop.example.com/wp-json/wc/v3/products?page=1>; rel=\"prev\""));

        var ids = new List<long>();
        await foreach (var p in Client(pageSize: 1).GetAllProductsAsync())
            ids.Add(p.Id);

        // Page 2 is full, but its Link has no rel="next": that, not the page size, ends it.
        Assert.Equal([1L, 2], ids);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Paging_ShortPageWithoutHeaders_Stops()
    {
        _handler
            .Then(_ => Json(Products(1, 2)))
            .Then(_ => Json(Products(3)));

        var ids = new List<long>();
        await foreach (var p in Client(pageSize: 2).GetAllProductsAsync())
            ids.Add(p.Id);

        Assert.Equal([1L, 2, 3], ids);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task TestConnection_ReadsProductCount()
    {
        _handler.Then(_ => Json(Products(16), totalPages: 18, total: 18));

        var info = await Client().TestConnectionAsync();

        Assert.Equal(18, info.ProductCount);
        var uri = _handler.Requests.Single().Uri;
        Assert.Equal("/wp-json/wc/v3/products", uri.AbsolutePath);
        Assert.Contains("per_page=1", uri.Query);
        Assert.DoesNotContain(_handler.Requests, r => r.Uri.AbsolutePath.Contains("system_status"));
    }

    [Fact]
    public async Task TestConnection_BadKeys_ThrowsWithWooCode()
    {
        _handler.Then(_ => Error(HttpStatusCode.Unauthorized, "woocommerce_rest_cannot_view", "Sorry, you cannot list resources."));

        var ex = await Assert.ThrowsAsync<WooApiException>(() => Client().TestConnectionAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        Assert.Equal("woocommerce_rest_cannot_view", ex.Code);
        Assert.False(ex.Transient);
        Assert.Single(_handler.Requests); // 401 is not retried
    }

    [Fact]
    public async Task FindBySkus_CaseInsensitive_ChunksAndFiltersPartialMatches()
    {
        _handler.Then(_ => Json(new object[]
        {
            new { id = 14, sku = "woo-beanie", type = "simple" },
            new { id = 27, sku = "woo-hoodie-red", type = "variation", parent_id = 11 },
            new { id = 99, sku = "something-else" },
        }));

        var found = await Client(pageSize: 100).FindBySkusAsync(["WOO-BEANIE", "woo-Hoodie-Red", " ", "woo-beanie"]);

        Assert.Equal([14L, 27], found.Select(p => p.Id));
        Assert.True(found.Single(p => p.Id == 27).IsVariation);
        var query = Uri.UnescapeDataString(_handler.Requests.Single().Uri.Query);
        Assert.Contains("sku=WOO-BEANIE,woo-Hoodie-Red", query);
    }

    [Fact]
    public async Task Retry_On429_HonoursRetryAfter()
    {
        _handler
            .Then(_ =>
            {
                var r = Error(HttpStatusCode.TooManyRequests, "rate_limited");
                r.Headers.Add("Retry-After", "7");
                return r;
            })
            .Then(_ => Json(WooFixtures.Text("order-guest-paid.json")));

        var order = await Client().GetOrderAsync(58);

        Assert.Equal(58, order!.Id);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(7)], _delays);
    }

    [Fact]
    public async Task Retry_On5xx_ExponentialBackoff_ThenGivesUp()
    {
        for (var i = 0; i < 4; i++)
            _handler.Then(_ => Error(HttpStatusCode.BadGateway, "bad_gateway"));

        var ex = await Assert.ThrowsAsync<WooApiException>(() => Client(maxRetries: 3).GetOrderAsync(58));

        Assert.Equal(HttpStatusCode.BadGateway, ex.Status);
        Assert.True(ex.Transient);
        Assert.Equal(4, _handler.Requests.Count); // 1 try + 3 retries
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], _delays);
    }

    [Fact]
    public async Task Retry_OnTimeout_ThenSucceeds()
    {
        _handler
            .ThenAsync(async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token); // hangs until the per-request timeout fires
                throw new InvalidOperationException("unreachable");
            })
            .Then(_ => Json(WooFixtures.Text("order-guest-paid.json")));

        var order = await Client(timeout: TimeSpan.FromMilliseconds(100)).GetOrderAsync(58);

        Assert.Equal(58, order!.Id);
        Assert.Single(_delays);
    }

    [Fact]
    public async Task CallerCancellation_IsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        _handler.ThenAsync(async (_, token) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client().GetOrderAsync(58, cts.Token));
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Create_IsNotRetriedOn5xx_ButIsOn429()
    {
        // A 5xx after POST /webhooks may mean it WAS created: retrying would duplicate it.
        _handler.Then(_ => Error(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<WooApiException>(() => Client().CreateWebhookAsync(new WooWebhookWrite { Topic = "order.updated" }));
        Assert.Single(_handler.Requests);

        _handler
            .Then(_ => Error(HttpStatusCode.TooManyRequests))
            .Then(_ => Json(new { id = 5, status = "active", topic = "order.updated", delivery_url = "https://x/hooks" }));
        var hook = await Client().CreateWebhookAsync(new WooWebhookWrite { Topic = "order.updated" });
        Assert.Equal(5, hook.Id);
        Assert.Equal("https://x/hooks", hook.DeliveryUrl);
    }

    [Fact]
    public async Task Batch_ChunksAt100_AndCollectsPerItemErrors()
    {
        var updates = Enumerable.Range(1, 250).Select(i => new WooProductWrite
        {
            Id = i, RegularPrice = WooJson.FormatMoney(i + 0.5m), StockQuantity = i,
        }).ToList();

        foreach (var _ in Enumerable.Range(0, 3))
        {
            _handler.Then(request =>
            {
                var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!;
                var results = body["update"]!.AsArray().Select(u =>
                {
                    var id = u!["id"]!.GetValue<long>();
                    return id % 50 == 0
                        ? (object)new { id, error = new { code = "woocommerce_rest_product_invalid_id", message = "Invalid ID.", data = new { status = 400 } } }
                        : new { id, sku = $"sku-{id}" };
                }).ToArray();
                return Json(new { update = results });
            });
        }

        var result = await Client().BatchProductsAsync(new WooBatchRequest { Update = updates });

        Assert.Equal(3, _handler.Requests.Count);
        Assert.All(_handler.Requests, r => Assert.Equal("/wp-json/wc/v3/products/batch", r.Uri.AbsolutePath));
        var sizes = _handler.Requests.Select(r => JsonNode.Parse(r.Body!)!["update"]!.AsArray().Count).ToList();
        Assert.Equal([100, 100, 50], sizes);
        Assert.Equal(245, result.Updated.Count);
        Assert.Equal([50L, 100, 150, 200, 250], result.Errors.Select(e => e.Id));
        Assert.All(result.Errors, e =>
        {
            Assert.Equal("update", e.Operation);
            Assert.Equal("woocommerce_rest_product_invalid_id", e.Code);
        });

        // Money went over the wire as Woo strings.
        Assert.Equal("1.5", JsonNode.Parse(_handler.Requests[0].Body!)!["update"]![0]!["regular_price"]!.GetValue<string>());
    }

    [Fact]
    public async Task BatchVariations_UsesTheParentRoute()
    {
        _handler.Then(_ => Json(new { update = new[] { new { id = 27, sku = "woo-hoodie-red" } } }));

        var result = await Client().BatchVariationsAsync(11, new WooBatchRequest
        {
            Update = [new WooProductWrite { Id = 27, StockQuantity = 19, ManageStock = true }],
        });

        Assert.Equal("/wp-json/wc/v3/products/11/variations/batch", _handler.Requests.Single().Uri.AbsolutePath);
        Assert.Equal(27, Assert.Single(result.Updated).Id);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task OrdersModifiedAfter_SendsGmtCursorAndStatuses()
    {
        _handler.Then(_ => Json(new[] { JsonNode.Parse(WooFixtures.Text("order-guest-paid.json")) }, totalPages: 1));

        var orders = new List<WooOrder>();
        await foreach (var o in Client().GetOrdersModifiedAfterAsync(
                           new DateTime(2026, 10, 8, 18, 0, 0, DateTimeKind.Utc), ["processing", "completed"]))
            orders.Add(o);

        Assert.Equal(58, Assert.Single(orders).Id);
        var query = Uri.UnescapeDataString(_handler.Requests.Single().Uri.Query);
        Assert.Contains("modified_after=2026-10-08T18:00:00", query);
        Assert.Contains("dates_are_gmt=true", query);
        Assert.Contains("status=processing,completed", query);
    }

    [Fact]
    public async Task OrderRefunds_AreRead()
    {
        using var doc = JsonDocument.Parse(WooFixtures.Text("order-refund.json"));
        _handler.Then(_ => Json("[" + doc.RootElement.GetProperty("refund").GetRawText() + "]", totalPages: 1));

        var refunds = await Client().GetOrderRefundsAsync(58);

        Assert.Equal(17.42m, Assert.Single(refunds).Amount);
        Assert.Equal("/wp-json/wc/v3/orders/58/refunds", _handler.Requests.Single().Uri.AbsolutePath);
    }

    [Fact]
    public async Task Webhooks_ListAndUpdate()
    {
        _handler
            .Then(_ => Json(new[] { new { id = 2, status = "disabled", topic = "order.updated", delivery_url = "https://x/hooks/woocommerce/shop1" } }, totalPages: 1))
            .Then(_ => Json(new { id = 2, status = "active", topic = "order.updated" }));

        var client = Client();
        var hooks = await client.ListWebhooksAsync();
        Assert.Equal(WooWebhookStatuses.Disabled, Assert.Single(hooks).Status);

        var updated = await client.UpdateWebhookAsync(2, new WooWebhookWrite { Status = WooWebhookStatuses.Active });
        Assert.Equal(WooWebhookStatuses.Active, updated.Status);
        Assert.Equal(HttpMethod.Put, _handler.Requests[1].Method);
        Assert.Equal("""{"status":"active"}""", _handler.Requests[1].Body);
    }
}
