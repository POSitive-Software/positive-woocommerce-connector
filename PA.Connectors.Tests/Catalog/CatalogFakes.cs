using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PA.Connectors.Core.Pos;
using Xunit;

namespace PA.Tests.Connectors.Catalog;

/// <summary>A product or variation held by <see cref="FakeWooStore"/>.</summary>
internal sealed class FakeWooProduct
{
    public long Id { get; set; }
    public long ParentId { get; set; }
    public string Type { get; set; } = "simple";
    public string Status { get; set; } = "publish";
    public string? Sku { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string RegularPrice { get; set; } = "";
    public string SalePrice { get; set; } = "";
    public bool ManageStock { get; set; }
    public int? StockQuantity { get; set; }

    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["parent_id"] = ParentId,
        ["type"] = Type,
        ["status"] = Status,
        ["sku"] = Sku ?? "",
        ["name"] = Name,
        ["description"] = Description ?? "",
        ["regular_price"] = RegularPrice,
        ["sale_price"] = SalePrice,
        ["manage_stock"] = Type == "variation" && !ManageStock ? JsonValue.Create("parent") : JsonValue.Create(ManageStock),
        ["stock_quantity"] = ManageStock ? StockQuantity : null,
    };
}

/// <summary>
/// An in-memory WooCommerce store behind an HttpMessageHandler: the wc/v3 routes the
/// catalog push uses (products, variations, batch, ?sku=, the decimals setting), with
/// X-WP-TotalPages paging, per-item batch errors and injectable request failures.
/// Every request is recorded.
/// </summary>
internal sealed class FakeWooStore : HttpMessageHandler
{
    private long _nextId = 5000;

    public List<FakeWooProduct> Products { get; } = [];

    public List<(HttpMethod Method, string Path, string Query, JsonNode? Body)> Requests { get; } = [];

    public int Decimals { get; set; } = 2;

    /// <summary>Creates whose SKU is listed here fail with a per-item error.</summary>
    public HashSet<string> FailCreateSkus { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Updates of these ids fail with a per-item error (not "invalid id").</summary>
    public HashSet<long> FailUpdateIds { get; } = [];

    /// <summary>When set, every batch POST answers this status (no per-item results).</summary>
    public HttpStatusCode? BatchStatus { get; set; }

    public FakeWooProduct Add(FakeWooProduct product)
    {
        if (product.Id == 0)
            product.Id = ++_nextId;
        Products.Add(product);
        return product;
    }

    public FakeWooProduct Simple(long id, string? sku, string price = "10.00", int? stock = null, string status = "publish") =>
        Add(new FakeWooProduct
        {
            Id = id, Sku = sku, Name = "Woo " + sku, RegularPrice = price, Status = status,
            ManageStock = stock is not null, StockQuantity = stock,
        });

    public FakeWooProduct Variable(long id, string? sku) =>
        Add(new FakeWooProduct { Id = id, Type = "variable", Sku = sku, Name = "Woo " + sku });

    public FakeWooProduct Variation(long parentId, long id, string? sku, string price = "10.00", int? stock = null) =>
        Add(new FakeWooProduct
        {
            Id = id, ParentId = parentId, Type = "variation", Sku = sku, Name = "Var " + sku, RegularPrice = price,
            ManageStock = stock is not null, StockQuantity = stock,
        });

    public FakeWooProduct? Find(long id) => Products.FirstOrDefault(p => p.Id == id);

    public IEnumerable<JsonNode?> BatchBodies(string pathEnd) =>
        Requests.Where(r => r.Method == HttpMethod.Post && r.Path.EndsWith(pathEnd, StringComparison.Ordinal)).Select(r => r.Body);

    public List<JsonObject> Updates(string pathEnd = "products/batch") =>
        BatchBodies(pathEnd).SelectMany(b => b?["update"]?.AsArray() ?? []).Select(n => n!.AsObject()).ToList();

    public List<JsonObject> Creates() =>
        BatchBodies("products/batch").SelectMany(b => b?["create"]?.AsArray() ?? []).Select(n => n!.AsObject()).ToList();

    public int BatchRequestCount(string pathEnd = "products/batch") => BatchBodies(pathEnd).Count();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        const string prefix = "/wp-json/wc/v3/";
        Assert.StartsWith(prefix, path);
        path = path[prefix.Length..];
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
        Requests.Add((request.Method, path, request.RequestUri.Query, body));
        var segments = path.Split('/');

        if (request.Method == HttpMethod.Get && path == "settings/general/woocommerce_price_num_decimals")
            return Json(new JsonObject { ["id"] = "woocommerce_price_num_decimals", ["value"] = Decimals.ToString(CultureInfo.InvariantCulture) });

        if (request.Method == HttpMethod.Get && path == "products")
        {
            IEnumerable<FakeWooProduct> list;
            if (query["sku"] is { } skus)
            {
                var wanted = skus.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
                list = Products.Where(p => p.Status != "trash" && p.Sku is not null && wanted.Contains(p.Sku));
            }
            else
            {
                list = Products.Where(p => p.Type != "variation" && p.Status != "trash");
            }
            return Page(list.OrderByDescending(p => p.Id).ToList(), query);
        }

        if (request.Method == HttpMethod.Get && segments is ["products", var pid])
        {
            var product = Find(long.Parse(pid, CultureInfo.InvariantCulture));
            return product is null || product.Type == "variation" ? InvalidId("woocommerce_rest_product_invalid_id") : Json(product.ToJson());
        }

        if (request.Method == HttpMethod.Get && segments is ["products", var parent, "variations"])
        {
            var parentId = long.Parse(parent, CultureInfo.InvariantCulture);
            return Page(Products.Where(p => p.ParentId == parentId && p.Type == "variation").OrderBy(p => p.Id).ToList(), query);
        }

        if (request.Method == HttpMethod.Get && segments is ["products", var vp, "variations", var vid])
        {
            var variation = Find(long.Parse(vid, CultureInfo.InvariantCulture));
            return variation is null || variation.ParentId != long.Parse(vp, CultureInfo.InvariantCulture)
                ? InvalidId("woocommerce_rest_product_variation_invalid_id")
                : Json(variation.ToJson());
        }

        if (request.Method == HttpMethod.Post && (path == "products/batch" || segments is ["products", _, "variations", "batch"]))
        {
            if (BatchStatus is { } status)
                return new HttpResponseMessage(status) { Content = new StringContent("""{"code":"boom","message":"down"}""", Encoding.UTF8, "application/json") };
            long? parentId = segments.Length == 4 ? long.Parse(segments[1], CultureInfo.InvariantCulture) : null;
            var ops = (body!["create"]?.AsArray().Count ?? 0) + (body["update"]?.AsArray().Count ?? 0);
            Assert.True(ops <= 100, $"batch of {ops} operations (Woo caps at 100)");
            return Json(Batch(body.AsObject(), parentId));
        }

        throw new InvalidOperationException($"FakeWooStore: unexpected {request.Method} {path}");
    }

    private JsonObject Batch(JsonObject body, long? parentId)
    {
        var created = new JsonArray();
        foreach (var node in body["create"]?.AsArray() ?? [])
        {
            var write = node!.AsObject();
            var sku = (string?)write["sku"];
            if (sku is not null && (FailCreateSkus.Contains(sku) || Products.Any(p => string.Equals(p.Sku, sku, StringComparison.OrdinalIgnoreCase))))
            {
                created.Add(new JsonObject { ["id"] = 0, ["error"] = new JsonObject { ["code"] = "product_invalid_sku", ["message"] = $"Invalid or duplicated SKU {sku}." } });
                continue;
            }
            var product = Add(new FakeWooProduct
            {
                Type = (string?)write["type"] ?? "simple",
                Status = (string?)write["status"] ?? "publish",
                Sku = sku,
                Name = (string?)write["name"],
                Description = (string?)write["description"],
                RegularPrice = (string?)write["regular_price"] ?? "",
                SalePrice = (string?)write["sale_price"] ?? "",
                ManageStock = (bool?)write["manage_stock"] ?? false,
                StockQuantity = (int?)write["stock_quantity"],
            });
            created.Add(product.ToJson());
        }

        var updated = new JsonArray();
        foreach (var node in body["update"]?.AsArray() ?? [])
        {
            var write = node!.AsObject();
            var id = (long)write["id"]!;
            var product = Find(id);
            var valid = product is not null && product.Status != "trash"
                        && (parentId is null ? product.Type != "variation" : product.ParentId == parentId);
            if (!valid)
            {
                updated.Add(new JsonObject
                {
                    ["id"] = id,
                    ["error"] = new JsonObject
                    {
                        ["code"] = parentId is null ? "woocommerce_rest_product_invalid_id" : "woocommerce_rest_product_variation_invalid_id",
                        ["message"] = "Invalid ID.",
                    },
                });
                continue;
            }
            if (FailUpdateIds.Contains(id))
            {
                updated.Add(new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["code"] = "rest_invalid_param", ["message"] = "Nope." } });
                continue;
            }
            if (write["regular_price"] is { } rp) product!.RegularPrice = (string)rp!;
            if (write["sale_price"] is { } sp) product!.SalePrice = (string)sp!;
            if (write["manage_stock"] is { } ms) product!.ManageStock = (bool)ms!;
            if (write["stock_quantity"] is { } sq) product!.StockQuantity = (int)sq!;
            if (write["name"] is { } n) product!.Name = (string)n!;
            if (write["description"] is { } d) product!.Description = (string)d!;
            updated.Add(product!.ToJson());
        }
        return new JsonObject { ["create"] = created, ["update"] = updated, ["delete"] = new JsonArray() };
    }

    private static HttpResponseMessage Page(List<FakeWooProduct> all, System.Collections.Specialized.NameValueCollection query)
    {
        var perPage = int.Parse(query["per_page"] ?? "10", CultureInfo.InvariantCulture);
        var page = int.Parse(query["page"] ?? "1", CultureInfo.InvariantCulture);
        var items = new JsonArray(all.Skip((page - 1) * perPage).Take(perPage).Select(p => (JsonNode)p.ToJson()).ToArray());
        var response = Json(items);
        response.Headers.Add("X-WP-Total", all.Count.ToString(CultureInfo.InvariantCulture));
        response.Headers.Add("X-WP-TotalPages", Math.Max(1, (all.Count + perPage - 1) / perPage).ToString(CultureInfo.InvariantCulture));
        return response;
    }

    private static HttpResponseMessage InvalidId(string code) =>
        new(HttpStatusCode.NotFound)
        {
            Content = new StringContent(new JsonObject { ["code"] = code, ["message"] = "Invalid ID." }.ToJsonString(), Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
}

/// <summary>A POSitive item held by <see cref="FakePos"/>; <see cref="ModifiedAt"/> drives modifiedSince.</summary>
internal sealed class FakePosItem
{
    public int InvNo { get; init; }
    public string? Sku { get; set; }
    public string? Description { get; set; }
    public string? LongDescription { get; set; }
    public string? ExtendedDescription { get; set; }
    public bool Web { get; set; } = true;

    /// <summary>The API leaves webProduct out (null) for this item, whatever <see cref="Web"/> says.</summary>
    public bool WebUnknown { get; set; }
    public int? ParentInvNo { get; set; }
    public int MatrixItemCount { get; set; }
    public decimal? Available { get; set; }
    public decimal? AvailableLive { get; set; }
    public decimal?[] Prices { get; } = new decimal?[6];
    public decimal?[] SalePrices { get; } = new decimal?[6];
    public List<string> Barcodes { get; } = [];
    public DateTime ModifiedAt { get; set; }

    public PosProduct ToProduct(string? externalId) => new(
        InvNo, externalId, Sku, Description, LongDescription, ParentInvNo, WebUnknown ? null : Web, Available, null, null,
        Enumerable.Range(1, 6).Select(l => new PosPrice(l, Prices[l - 1], SalePrices[l - 1] ?? Prices[l - 1])).ToList(),
        1, AvailableLive, ExtendedDescription, MatrixItemCount);
}

/// <summary>
/// <see cref="IPosGateway"/> over in-memory items, built with DispatchProxy so members the
/// order lane adds to the interface never break this fake (they throw if a catalog path calls them).
/// Implements the product list (webstoreOnly, invNo, modifiedSince exclusive, paging), sku/barcode
/// resolution and the A3 link rules.
/// </summary>
internal class FakePos : DispatchProxy
{
    public List<FakePosItem> Items { get; private set; } = [];

    /// <summary>invNo → this tenant's external id (the APITableLink 'items' row).</summary>
    public Dictionary<int, string> Links { get; private set; } = [];

    public List<PosProductQuery> Queries { get; private set; } = [];

    public List<string> Calls { get; private set; } = [];

    /// <summary>invNos whose single-item read (?invNo=) never answers.</summary>
    public HashSet<int> HangInvNos { get; private set; } = [];

    public IPosGateway Gateway { get; private set; } = null!;

    public static FakePos Create()
    {
        var gateway = Create<IPosGateway, FakePos>();
        var fake = (FakePos)(object)gateway;
        fake.Items = [];
        fake.Links = [];
        fake.Queries = [];
        fake.Calls = [];
        fake.HangInvNos = [];
        fake.Gateway = gateway;
        return fake;
    }

    public FakePosItem Add(int invNo, string? sku, decimal? price, decimal? live, DateTime? modifiedAt = null)
    {
        var item = new FakePosItem
        {
            InvNo = invNo, Sku = sku, Description = "Item " + sku, AvailableLive = live, Available = live + 100,
            ModifiedAt = modifiedAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        item.Prices[0] = price;
        Items.Add(item);
        return item;
    }

    // The item picker reads in parallel: one call at a time in here (every answer is synchronous).
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        lock (_sync)
            return InvokeLocked(targetMethod, args);
    }

    private readonly object _sync = new();

    private object? InvokeLocked(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        Calls.Add(name);
        // A single-item read that never answers (like item 900248 'HX' in a legacy proc).
        if (name == nameof(IPosGateway.ListProductsAsync) && args![0] is PosProductQuery { InvNo: { } hung } && HangInvNos.Contains(hung))
            return new TaskCompletionSource<PosResult<PosProductPage>>().Task;
        return name switch
        {
            nameof(IPosGateway.ListProductsAsync) => Task.FromResult(List((PosProductQuery)args![0]!)),
            nameof(IPosGateway.ResolveSkuAsync) => Task.FromResult(Resolve((string)args![0]!)),
            nameof(IPosGateway.LinkProductAsync) => Task.FromResult(Link((int)args![0]!, (string)args[1]!)),
            nameof(IPosGateway.UnlinkProductAsync) => Task.FromResult(Unlink((int)args![0]!)),
            nameof(IPosGateway.FindLinkedProductAsync) => Task.FromResult(Links.Where(l => string.Equals(l.Value, (string)args![0]!, StringComparison.OrdinalIgnoreCase))
                .Select(l => (int?)l.Key).FirstOrDefault()),
            _ => throw new NotSupportedException($"FakePos: {name} is not used by the catalog push."),
        };
    }

    private PosResult<PosProductPage> List(PosProductQuery query)
    {
        Queries.Add(query);
        Assert.InRange(query.PageSize, 1, 200);
        var all = Items
            .Where(i => !query.WebstoreOnly || i.Web)
            .Where(i => query.InvNo is null || i.InvNo == query.InvNo)
            .Where(i => query.ModifiedSince is null || i.ModifiedAt > query.ModifiedSince.Value)
            .OrderBy(i => i.InvNo)
            .ToList();
        var page = all.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(i => i.ToProduct(Links.GetValueOrDefault(i.InvNo))).ToList();
        // Like the real list: totalItems ignores modifiedSince (G11).
        return PosResult<PosProductPage>.Ok(new PosProductPage(page, query.Page, query.PageSize, Items.Count + 10_000));
    }

    private IReadOnlyList<int> Resolve(string sku) =>
        Items.Where(i => string.Equals(i.Sku, sku, StringComparison.OrdinalIgnoreCase)
                         || i.Barcodes.Contains(sku, StringComparer.OrdinalIgnoreCase))
            .Select(i => i.InvNo).Distinct().ToList();

    private PosResult<bool> Link(int invNo, string externalId)
    {
        if (Items.All(i => i.InvNo != invNo))
            return PosResult<bool>.Fail(PosErrorCodes.ProductNotFound, $"No product {invNo}.");
        if (Links.TryGetValue(invNo, out var existing))
            return string.Equals(existing, externalId, StringComparison.OrdinalIgnoreCase)
                ? PosResult<bool>.Ok(false)
                : PosResult<bool>.Fail(PosErrorCodes.LinkExists, $"Product {invNo} is linked to '{existing}'.");
        if (Links.Any(l => string.Equals(l.Value, externalId, StringComparison.OrdinalIgnoreCase)))
            return PosResult<bool>.Fail(PosErrorCodes.LinkExists, $"'{externalId}' is on another product.");
        Links[invNo] = externalId;
        return PosResult<bool>.Ok(true);
    }

    private PosResult<bool> Unlink(int invNo) => PosResult<bool>.Ok(Links.Remove(invNo));
}
