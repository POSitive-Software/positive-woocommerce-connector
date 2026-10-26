using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace PA.Tests.Connectors.Orders;

/// <summary>
/// A stand-in for a store's wc/v3 REST API: orders (by id, and the
/// modified_after listing the poll uses) and webhooks (list / create / update).
/// Orders are JSON objects — usually the recorded fixtures — so a test can
/// change status, refunds or modified time and re-run.
/// </summary>
internal sealed class FakeWooStore : HttpMessageHandler
{
    private const string Base = "/wp-json/wc/v3/";
    private long _nextWebhookId = 100;

    public Dictionary<long, JsonObject> Orders { get; } = [];
    public List<JsonObject> Webhooks { get; } = [];
    public List<(HttpMethod Method, string PathAndQuery, string? Body)> Requests { get; } = [];

    /// <summary>The modified_after values the poll sent.</summary>
    public List<string> ModifiedAfterQueries { get; } = [];

    public JsonObject AddOrder(string fixture, Action<JsonObject>? change = null)
    {
        var order = JsonNode.Parse(WooFixtures.Text(fixture))!.AsObject();
        if (order.ContainsKey("_fixture"))
            order = order["order"]!.DeepClone().AsObject();
        change?.Invoke(order);
        Orders[Id(order)] = order;
        return order;
    }

    public JsonObject Order(long id) => Orders[id];

    private static long Id(JsonObject o) => long.Parse(o["id"]!.ToJsonString(), CultureInfo.InvariantCulture);

    public JsonObject AddWebhook(string topic, string deliveryUrl, string status, string? secret)
    {
        var hook = new JsonObject
        {
            ["id"] = _nextWebhookId++, ["name"] = "existing", ["status"] = status, ["topic"] = topic,
            ["delivery_url"] = deliveryUrl, ["secret"] = secret,
        };
        Webhooks.Add(hook);
        return hook;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var uri = request.RequestUri!;
        Requests.Add((request.Method, uri.PathAndQuery, body));
        var path = uri.AbsolutePath.StartsWith(Base, StringComparison.Ordinal) ? uri.AbsolutePath[Base.Length..] : uri.AbsolutePath;
        var query = HttpUtility.ParseQueryString(uri.Query);

        if (request.Method == HttpMethod.Get && path.StartsWith("orders/", StringComparison.Ordinal)
            && long.TryParse(path["orders/".Length..], out var id))
            return Orders.TryGetValue(id, out var order)
                ? Json(HttpStatusCode.OK, order)
                : Json(HttpStatusCode.NotFound, new JsonObject { ["code"] = "woocommerce_rest_shop_order_invalid_id", ["message"] = "Invalid ID." });

        if (request.Method == HttpMethod.Get && path == "orders")
        {
            var after = query["modified_after"];
            ModifiedAfterQueries.Add(after ?? "");
            var since = after is null ? DateTime.MinValue : DateTime.Parse(after, CultureInfo.InvariantCulture);
            var list = new JsonArray(Orders.Values
                .Where(o => DateTime.Parse((string)o["date_modified_gmt"]!, CultureInfo.InvariantCulture) > since)
                .OrderBy(Id)
                .Select(o => (JsonNode)o.DeepClone()).ToArray());
            return Json(HttpStatusCode.OK, list, totalPages: 1);
        }

        if (path == "webhooks" && request.Method == HttpMethod.Get)
            return Json(HttpStatusCode.OK, new JsonArray(Webhooks.Select(h => (JsonNode)h.DeepClone()).ToArray()), totalPages: 1);
        if (path == "webhooks" && request.Method == HttpMethod.Post)
        {
            var hook = JsonNode.Parse(body!)!.AsObject();
            hook["id"] = _nextWebhookId++;
            Webhooks.Add(hook);
            return Json(HttpStatusCode.Created, hook);
        }
        if (path.StartsWith("webhooks/", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
        {
            var hookId = long.Parse(path["webhooks/".Length..], CultureInfo.InvariantCulture);
            var hook = Webhooks.Single(h => Id(h) == hookId);
            foreach (var (key, value) in JsonNode.Parse(body!)!.AsObject())
                hook[key] = value?.DeepClone();
            return Json(HttpStatusCode.OK, hook);
        }

        return Json(HttpStatusCode.NotFound, new JsonObject { ["code"] = "rest_no_route", ["message"] = $"No route {path}" });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body, int? totalPages = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (totalPages is not null)
        {
            response.Headers.Add("X-WP-TotalPages", totalPages.Value.ToString(CultureInfo.InvariantCulture));
            response.Headers.Add("X-WP-Total", (body is JsonArray a ? a.Count : 1).ToString(CultureInfo.InvariantCulture));
        }
        return response;
    }
}
