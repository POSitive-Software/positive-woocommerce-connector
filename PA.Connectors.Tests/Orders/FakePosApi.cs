using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace PA.Tests.Connectors.Orders;

/// <summary>
/// A stateful stand-in for PA.Server's /api/v2, just the routes the order import
/// uses, answering with the documented shapes and errorCodes (docs/openapi/v2.json):
/// customers (find by externalId, create → 409 customer_exists, PATCH, billing
/// address → 409 address_exists once set), transactions (409 transaction_exists
/// with the existing transactionId, unknown productId → line skipped, all skipped →
/// 422 no_writable_lines), the pending lookup by externalId (404 transaction_invoiced
/// naming the invoice, or transaction_not_found) and the invoice it points at,
/// payments by localTransactionId (409 payment_exists on a replayed
/// localPaymentId), and the sku lookup. The real PosApiClient talks to it, so
/// the wire format is exercised too. <see cref="LoseResponse"/> makes a write
/// take effect and then answer 503 — a lost response, for resume tests.
/// </summary>
internal sealed class FakePosApi : HttpMessageHandler
{
    private readonly object _gate = new();
    private int _nextCustomerId = 500;
    private int _nextInvoice = 90000;
    private int _nextPaymentId = 1;

    public sealed class Customer
    {
        public int Id { get; init; }
        public required string ExternalId { get; init; }
        public JsonObject Fields { get; } = new();
        public JsonObject? Billing { get; set; }
        public List<string> Patches { get; } = [];
    }

    public sealed record Transaction(int InvoiceNo, string LocalTransactionId, JsonObject Body)
    {
        /// <summary>productId of each line POSitive wrote (skipped lines are absent).</summary>
        public List<int?> WrittenProductIds { get; init; } = [];

        /// <summary>True = posted to an invoice: it leaves /transactions/pending and shows in /invoices.</summary>
        public bool Invoiced { get; set; }

        /// <summary>True = voided in the store: still guards the create (409), but no longer pending and never invoiced.</summary>
        public bool Voided { get; set; }
    }

    public sealed record Payment(int PaymentId, string LocalTransactionId, string? LocalPaymentId, string PaymentType, decimal Amount);

    public Dictionary<string, Customer> Customers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Transaction> Transactions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Payment> Payments { get; } = [];

    /// <summary>sku/barcode → invNos (case-insensitive, like the API's sku rule).</summary>
    public Dictionary<string, List<int>> Skus { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>invNos that exist (a productId line for any other is skipped). Empty = every productId exists.</summary>
    public HashSet<int> Products { get; } = [];

    public List<(HttpMethod Method, string PathAndQuery, string? Body)> Requests { get; } = [];

    private readonly List<(Func<HttpMethod, string, bool> Match, bool Apply, string ErrorCode)> _faults = [];

    /// <summary>The next matching request answers 503 (with <paramref name="errorCode"/>) without doing anything.</summary>
    public void FailNext(HttpMethod method, string pathPrefix, string errorCode = "unavailable") =>
        _faults.Add(((m, p) => m == method && p.StartsWith(pathPrefix, StringComparison.Ordinal), false, errorCode));

    /// <summary>The next matching request is carried out, then answers 503 (the response was "lost").</summary>
    public void LoseResponse(HttpMethod method, string pathPrefix) =>
        _faults.Add(((m, p) => m == method && p.StartsWith(pathPrefix, StringComparison.Ordinal), true, "unavailable"));

    public int Count(HttpMethod method, string pathPrefix) =>
        Requests.Count(r => r.Method == method && r.PathAndQuery.StartsWith(pathPrefix, StringComparison.Ordinal));

    /// <summary>Requests to exactly this path (query ignored).</summary>
    public int CountPath(HttpMethod method, string path) =>
        Requests.Count(r => r.Method == method && r.PathAndQuery.Split('?')[0] == path);

    public void AddSku(string sku, params int[] invNos) => Skus[sku] = [.. invNos];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var pathAndQuery = request.RequestUri!.PathAndQuery;
        lock (_gate)
        {
            Requests.Add((request.Method, pathAndQuery, body));
            var fault = _faults.FindIndex(f => f.Match(request.Method, pathAndQuery));
            (bool Apply, bool Hit) faultState = (false, false);
            if (fault >= 0)
            {
                faultState = (_faults[fault].Apply, true);
                var errorCode = _faults[fault].ErrorCode;
                _faults.RemoveAt(fault);
                if (!faultState.Apply)
                    return Problem(HttpStatusCode.ServiceUnavailable, errorCode, "Injected fault.");
            }
            var response = Route(request.Method, request.RequestUri!, body);
            if (faultState.Hit)
                return Problem(HttpStatusCode.ServiceUnavailable, "unavailable", "Injected fault after the write.");
            return response;
        }
    }

    private HttpResponseMessage Route(HttpMethod method, Uri uri, string? body)
    {
        var path = uri.AbsolutePath;
        var query = HttpUtility.ParseQueryString(uri.Query);
        var json = string.IsNullOrEmpty(body) ? null : JsonNode.Parse(body)!.AsObject();

        if (method == HttpMethod.Get && path == "/api/v2/customers")
        {
            var ext = query["externalId"] ?? "";
            var items = Customers.TryGetValue(ext, out var c) ? new[] { CustomerListItem(c) } : [];
            return Ok(new { items, page = 1, pageSize = 1, totalItems = items.Length });
        }
        if (method == HttpMethod.Post && path == "/api/v2/customers")
        {
            var ext = (string?)json!["localCustomerId"] ?? "";
            if (Customers.ContainsKey(ext))
                return Problem(HttpStatusCode.Conflict, "customer_exists", "A customer with this localCustomerId already exists.");
            var customer = new Customer { Id = _nextCustomerId++, ExternalId = ext };
            foreach (var (key, value) in json)
                if (key != "localCustomerId")
                    customer.Fields[key] = value?.DeepClone();
            Customers[ext] = customer;
            return Json(HttpStatusCode.Created, new { customerId = customer.Id, externalId = ext });
        }
        if (method == HttpMethod.Patch && path.StartsWith("/api/v2/customers/", StringComparison.Ordinal))
        {
            var id = int.Parse(path["/api/v2/customers/".Length..]);
            var customer = Customers.Values.FirstOrDefault(c => c.Id == id);
            if (customer is null)
                return Problem(HttpStatusCode.NotFound, "customer_not_found", "No such customer.");
            customer.Patches.Add(body!);
            foreach (var (key, value) in json!)
                customer.Fields[key] = value?.DeepClone();
            return Ok(new { customerId = id, externalId = customer.ExternalId });
        }
        if (method == HttpMethod.Post && path == "/api/v2/customers/addresses")
        {
            if (!Customers.TryGetValue(query["externalId"] ?? "", out var customer))
                return Problem(HttpStatusCode.NotFound, "customer_not_found", "No such customer.");
            if (!string.Equals((string?)json!["type"], "Billing", StringComparison.OrdinalIgnoreCase))
                return Problem(HttpStatusCode.BadRequest, "invalid_address_type", "Only billing in this fake.");
            if (customer.Billing is not null)
                return Problem(HttpStatusCode.Conflict, "address_exists", "address already exists");
            customer.Billing = json;
            return Ok(new { customerId = customer.Id });
        }
        if (method == HttpMethod.Post && path == "/api/v2/transactions")
            return CreateTransaction(json!);
        if (method == HttpMethod.Post && path == "/api/v2/transactions/payments")
        {
            var ext = query["externalId"] ?? "";
            if (!Transactions.TryGetValue(ext, out var transaction))
                return Problem(HttpStatusCode.NotFound, "transaction_not_found", "No such transaction.");
            var localPaymentId = (string?)json!["localPaymentId"];
            var existing = Payments.FirstOrDefault(p => localPaymentId is not null
                                                        && string.Equals(p.LocalPaymentId?.TrimEnd(), localPaymentId.TrimEnd(), StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                return Problem(HttpStatusCode.Conflict, "payment_exists", "Already recorded.",
                    new { transactionId = transaction.InvoiceNo, paymentId = existing.PaymentId, localPaymentId });
            var payment = new Payment(_nextPaymentId++, ext, localPaymentId, (string)json["paymentType"]!, (decimal)json["amount"]!);
            Payments.Add(payment);
            return Ok(new { transactionId = transaction.InvoiceNo, paymentId = payment.PaymentId });
        }
        if (method == HttpMethod.Get && path == "/api/v2/transactions/pending")
        {
            // G13: the caller's transaction by its localTransactionId (only filter the connector sends).
            var ext = (query["externalId"] ?? "").TrimEnd();
            if (!Transactions.TryGetValue(ext, out var t))
                return Problem(HttpStatusCode.NotFound, "transaction_not_found",
                    $"No pending transaction is linked to localTransactionId '{ext}' for your key.");
            if (t.Voided)
                return Problem(HttpStatusCode.NotFound, "transaction_not_found",
                    $"localTransactionId '{ext}' is transaction {t.InvoiceNo}, which is no longer pending (status 'V') and has no invoice.");
            if (t.Invoiced)
                return Problem(HttpStatusCode.NotFound, "transaction_invoiced",
                    $"localTransactionId '{ext}' is transaction {t.InvoiceNo}, which is no longer pending: it was invoiced as {InvoiceNoOf(t)}.",
                    new { transactionId = t.InvoiceNo, invoiceTransactionNumbers = new[] { InvoiceNoOf(t) } });
            return Ok(new { items = new[] { PendingSummary(t) }, page = 1, pageSize = 1, totalItems = 1 });
        }
        if (method == HttpMethod.Get && path.StartsWith("/api/v2/transactions/pending/", StringComparison.Ordinal))
        {
            var number = int.Parse(path["/api/v2/transactions/pending/".Length..]);
            var t = Transactions.Values.FirstOrDefault(x => x.InvoiceNo == number && !x.Invoiced && !x.Voided);
            return t is null
                ? Problem(HttpStatusCode.NotFound, "pending_not_found", "No pending transaction matches the supplied id.")
                : Ok(new { transaction = PendingSummary(t), lines = t.WrittenProductIds.Select(p => new { productId = p }).ToArray() });
        }
        if (method == HttpMethod.Get && path.StartsWith("/api/v2/invoices/", StringComparison.Ordinal))
        {
            var number = int.Parse(path["/api/v2/invoices/".Length..]);
            var t = Transactions.Values.FirstOrDefault(x => x.Invoiced && InvoiceNoOf(x) == number);
            return t is null
                ? Problem(HttpStatusCode.NotFound, "invoice_not_found", "No invoice matches the supplied id.")
                : Ok(new
                {
                    invoice = new
                    {
                        transactionNumber = number, originalPendingTransactionNumber = t.InvoiceNo,
                        originalPendingLocalTransactionId = t.LocalTransactionId,
                        total = (decimal)t.Body["total"]!, taxAmount = (decimal)t.Body["taxAmount"]!,
                    },
                    lines = Array.Empty<object>(),
                });
        }
        if (method == HttpMethod.Get && path == "/api/v2/products" && query["sku"] is { } sku)
        {
            var items = Skus.TryGetValue(sku, out var invNos) ? invNos.Select(i => new { productId = i }).ToArray() : [];
            return Ok(new { items, page = 1, pageSize = 100, totalItems = items.Length });
        }
        return Problem(HttpStatusCode.NotFound, "not_found", $"Fake has no route {method} {path}.");
    }

    private HttpResponseMessage CreateTransaction(JsonObject json)
    {
        var ext = (string)json["localTransactionId"]!;
        if (Transactions.TryGetValue(ext, out var existing))
            return Problem(HttpStatusCode.Conflict, "transaction_exists", "A transaction with this localTransactionId already exists.",
                new { transactionId = existing.InvoiceNo });
        var localCustomerId = (string?)json["localCustomerId"];
        if (localCustomerId is null || !Customers.ContainsKey(localCustomerId))
            return Problem(HttpStatusCode.NotFound, "reference_not_found", "The customer does not exist.");

        var statuses = new List<(string? LocalLineId, string Status)>();
        foreach (var line in json["lines"]!.AsArray())
        {
            var productId = (int?)line!["productId"];
            var status = productId is { } p && Products.Count > 0 && !Products.Contains(p) ? "product could not be found" : "Success";
            statuses.Add(((string?)line["localLineId"], status));
        }
        var results = statuses.Select(s => new { localLineId = s.LocalLineId, status = s.Status }).ToList();
        if (statuses.Count > 0 && statuses.All(s => s.Status != "Success"))
            return Problem(HttpStatusCode.UnprocessableEntity, "no_writable_lines", "Every line was skipped.", new { lines = results });

        var written = json["lines"]!.AsArray().Zip(statuses).Where(x => x.Second.Status == "Success")
            .Select(x => (int?)x.First!["productId"]).ToList();
        var transaction = new Transaction(_nextInvoice++, ext, json) { WrittenProductIds = written };
        Transactions[ext] = transaction;
        return Json(HttpStatusCode.Created, new { transactionId = transaction.InvoiceNo, externalId = ext, lines = results });
    }

    /// <summary>The invoice POSitive posted an invoiced transaction to.</summary>
    private static int InvoiceNoOf(Transaction t) => t.InvoiceNo + 500000;

    private static object PendingSummary(Transaction t) => new
    {
        transactionNumber = t.InvoiceNo,
        externalId = t.LocalTransactionId,
        purchaseOrderNumber = (string?)t.Body["purchaseOrderNumber"],
        total = (decimal)t.Body["total"]!,
        taxAmount = (decimal)t.Body["taxAmount"]!,
    };

    private static object CustomerListItem(Customer c)
    {
        var item = new JsonObject { ["customerId"] = c.Id, ["externalId"] = c.ExternalId };
        foreach (var (key, value) in c.Fields)
            item[key] = value?.DeepClone();
        return item;
    }

    private static HttpResponseMessage Ok(object body) => Json(HttpStatusCode.OK, body);

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Problem(HttpStatusCode status, string errorCode, string detail, object? extra = null)
    {
        var problem = new JsonObject { ["status"] = (int)status, ["title"] = errorCode, ["detail"] = detail, ["errorCode"] = errorCode };
        if (extra is not null)
            foreach (var (key, value) in JsonSerializer.SerializeToNode(extra)!.AsObject())
                problem[key] = value?.DeepClone();
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(problem.ToJsonString(), Encoding.UTF8, "application/problem+json"),
        };
    }
}
