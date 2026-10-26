using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PA.Connectors.Core;
using PA.Connectors.Core.Catalog;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Orders;
using PA.Connectors.Core.Pos;
using PA.Connectors.Core.Secrets;
using PA.Connectors.Core.Stores;
using PA.Connectors.Host;
using PA.Connectors.Host.Ui;
using PA.Connectors.WooCommerce;
using PA.Tests.Connectors.Catalog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace PA.Tests.Connectors.Host;

/// <summary>
/// S5: the connector UI's JSON API on the host (/connectors/api/**), in-process on a TestServer with
/// temp paths and fakes for Woo, /api/whoami and the POSitive gateway. Covers the trusted-header
/// gate, settings validation, secrets going in but never out, the wizard's key step, job enqueueing
/// (match / create / link), the ignore marks and exception Retry / Ignore. The worker ticks once at
/// start (nothing enabled) and then not again during a test.
/// </summary>
public sealed class ConnectorUiApiTests : IAsyncLifetime
{
    private const string User = "POS Manager (POS)";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pa-connector-ui-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySecrets _secrets = new();
    private readonly FakeWooTester _woo = new();
    private readonly FakeKeyVerifier _keys = new();
    private readonly FakePos _pos = FakePos.Create();
    private readonly AuditSink _log = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private string ConfigPath => Path.Combine(_dir, "connectors.json");

    internal sealed class MemorySecrets : IConnectorSecretStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string name) => _values.GetValueOrDefault(name);
        public void Set(string name, string value) => _values[name] = value;
        public bool Remove(string name) => _values.TryRemove(name, out _);
        public IReadOnlyList<string> List() => _values.Keys.ToList();
    }

    private sealed class FakeWooTester : IWooConnectionTester
    {
        public List<(Uri Url, string Key, string Secret)> Calls { get; } = [];
        public Exception? Fail { get; set; }

        public Task<WooStoreInfo> TestAsync(Uri storeUrl, string consumerKey, string consumerSecret, CancellationToken cancellationToken)
        {
            Calls.Add((storeUrl, consumerKey, consumerSecret));
            return Fail is not null ? Task.FromException<WooStoreInfo>(Fail) : Task.FromResult(new WooStoreInfo(18, "NZD", 2, true, true, null));
        }
    }

    private sealed class FakeKeyVerifier : IPosKeyVerifier
    {
        public Dictionary<string, PosKeyCheck> Answers { get; } = [];
        public List<string> Calls { get; } = [];

        /// <summary>When set, calls go to this (the real <see cref="PosKeyVerifier"/> over a fake /api/whoami).</summary>
        public IPosKeyVerifier? Real { get; set; }

        public Task<PosKeyCheck> VerifyAsync(string apiKey, CancellationToken cancellationToken)
        {
            Calls.Add(apiKey);
            if (Real is not null)
                return Real.VerifyAsync(apiKey, cancellationToken);
            return Task.FromResult(Answers.GetValueOrDefault(apiKey) ?? new PosKeyCheck(false, null, null, "POSitive Anywhere rejected this API key (401)."));
        }
    }

    /// <summary>The forward token PA.Server's forwards carry in these tests.</summary>
    internal const string ForwardToken = "test-forward-token-0123456789abcdef";

    /// <summary>DNS for the tests: *.example.com is public (TEST-NET-3), a few names point inside.</summary>
    internal static Task<System.Net.IPAddress[]> FakeDns(string host, CancellationToken cancellationToken) => host.ToLowerInvariant() switch
    {
        "internal.example.com" => Task.FromResult(new[] { System.Net.IPAddress.Parse("10.0.0.5") }),
        "metadata.example.com" => Task.FromResult(new[] { System.Net.IPAddress.Parse("169.254.169.254") }),
        "mixed.example.com" => Task.FromResult(new[] { System.Net.IPAddress.Parse("203.0.113.7"), System.Net.IPAddress.Parse("127.0.0.1") }),
        var h when h.EndsWith(".example.com", StringComparison.Ordinal) => Task.FromResult(new[] { System.Net.IPAddress.Parse("203.0.113.10") }),
        _ => Task.FromException<System.Net.IPAddress[]>(new System.Net.Sockets.SocketException(11001)),
    };

    private readonly FakeWooItems _wooItems = new();

    private sealed class FakeWooItems : IWooItemLookup
    {
        public Dictionary<long, WooItemSummary> Items { get; } = [];

        public Task<WooItemSummary?> GetAsync(ConnectorSettings settings, long wooId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.GetValueOrDefault(wooId));

        /// <summary>Each FindAsync call: the ids and SKUs asked for.</summary>
        public List<(long[] Ids, string[] Skus)> Finds { get; } = [];

        /// <summary>Like Woo: products?include= answers products only (not variations); products?sku= answers both.</summary>
        public Task<IReadOnlyList<WooItemSummary>> FindAsync(ConnectorSettings settings, IReadOnlyCollection<long> ids,
            IReadOnlyCollection<string> skus, CancellationToken cancellationToken)
        {
            Finds.Add((ids.ToArray(), skus.ToArray()));
            var bySku = Items.Values.Where(i => i.Sku is not null && skus.Contains(i.Sku, StringComparer.OrdinalIgnoreCase)).ToList();
            var wanted = ids.Concat(bySku.Where(i => i.ParentId > 0).Select(i => i.ParentId)).ToHashSet();
            var byId = Items.Values.Where(i => wanted.Contains(i.Id) && i.ParentId == 0).ToList();
            return Task.FromResult<IReadOnlyList<WooItemSummary>>(bySku.Concat(byId).DistinctBy(i => i.Id).ToList());
        }

        /// <summary>Null = Woo could not be read.</summary>
        public List<WooGatewaySummary>? Gateways { get; set; } = [];

        public Task<IReadOnlyList<WooGatewaySummary>?> GetPaymentGatewaysAsync(ConnectorSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WooGatewaySummary>?>(Gateways);
    }

    private sealed class FakeGateways(IPosGateway gateway) : IPosGatewayFactory
    {
        public IPosGateway Create(ConnectorSettings settings) => gateway;
    }

    private sealed class AuditSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
        public List<string> Audit() => Events.Where(e => e.MessageTemplate.Text.StartsWith("AUDIT connector", StringComparison.Ordinal))
            .Select(e => e.RenderMessage()).ToList();
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        ConnectorConfigFile.Save(ConfigPath, new ConnectorsDocument
        {
            Connectors = [new ConnectorSettings { Id = "shop1", StoreUrl = "https://shop.example.com", DeveloperId = "WOO-shop1" }],
        });

        _app = await ConnectorHostApp.BuildAsync(
            [
                $"--Connectors:ConfigPath={ConfigPath}",
                $"--Connectors:DatabasePath={Path.Combine(_dir, "connectors.db")}",
                $"--Connectors:SecretsPath={Path.Combine(_dir, "secrets.json")}",
                $"--Logging:Directory={Path.Combine(_dir, "logs")}",
                "--Connectors:TickSeconds=300",
            ],
            builder =>
            {
                builder.WebHost.UseTestServer();
                builder.Services.AddSingleton<IConnectorSecretStore>(_secrets);
                builder.Services.AddSingleton<IWooConnectionTester>(_woo);
                builder.Services.AddSingleton<IPosKeyVerifier>(_keys);
                builder.Services.AddSingleton<IPosGatewayFactory>(new FakeGateways(_pos.Gateway));
                builder.Services.AddSingleton<ILogEventSink>(_log);
                builder.Services.AddSingleton(ConnectorForwardToken.Fixed(ForwardToken));
                builder.Services.AddSingleton(new WooStoreAddress(allowPrivate: false, FakeDns));
                builder.Services.AddSingleton<IWooItemLookup>(_wooItems);
                // A hung single read gives up quickly here (5 s in production).
                builder.Services.AddSingleton(sp => new PosProductSearch(sp.GetRequiredService<IPosGatewayFactory>(), null,
                    TimeSpan.FromMilliseconds(400)));
            });
        await _app.StartAsync();
        _client = Client(User, canWrite: true);
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        TestDirectory.Delete(_dir);
    }

    private HttpClient Client(string? user, bool canWrite, string? forwardToken = ForwardToken)
    {
        var client = _app.GetTestClient();
        if (forwardToken is not null)
            client.DefaultRequestHeaders.Add(ConnectorForwardToken.HeaderName, forwardToken);
        if (user is not null)
            client.DefaultRequestHeaders.Add(PortalHeaders.User, Uri.EscapeDataString(user));
        client.DefaultRequestHeaders.Add(PortalHeaders.CanWrite, canWrite ? "1" : "0");
        return client;
    }

    private ConnectorStores Stores => _app.Services.GetRequiredService<ConnectorStores>();
    private SqliteConnectorUiStore UiStore => _app.Services.GetRequiredService<SqliteConnectorUiStore>();

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    private static object NewShop(string id = "shop2") => new
    {
        id,
        storeUrl = "https://shop2.example.com/",
        consumerKey = "ck_live_9f8e7d6c5b4a",
        consumerSecret = "cs_live_1a2b3c4d5e6f",
        priceLevel = 2,
        stockBuffer = 1,
        publishNewAs = "draft",
        payOnCompletionMethods = new[] { "cod", "cheque" },
        webhookBaseUrl = "https://shop2.ngrok.app/",
    };

    // --- the gate --------------------------------------------------------------------

    [Fact]
    public async Task Api_WithoutThePortalUserHeader_Is401_SoTheHostIsNotUsedDirectly()
    {
        var response = await Client(null, canWrite: true).GetAsync("/connectors/api/connectors");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("portal_user_required", (await Json(response)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Writes_WithoutCanWrite_Are403_AndChangeNothing()
    {
        var viewer = Client(User, canWrite: false);

        var list = await viewer.GetAsync("/connectors/api/connectors");
        var create = await viewer.PostAsJsonAsync("/connectors/api/connectors", NewShop());

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.False((await Json(list)).GetProperty("canWrite").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("settings_permission_required", (await Json(create)).GetProperty("errorCode").GetString());
        Assert.Single(ConnectorConfigFile.Load(ConfigPath).Connectors);
        Assert.Empty(_secrets.List());
    }

    [Fact]
    public async Task ARequestNamingAnotherHost_IsRefused_DnsRebindingCannotReachTheApi()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/connectors/api/connectors");
        request.Headers.Host = "attacker.example";

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("host_not_allowed", (await Json(response)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Me_DecodesTheUser_AndPagesAndHealthNeedNoUser()
    {
        var me = await Json(await Client("José Pérez (JP)", canWrite: false).GetAsync("/connectors/api/me"));
        var anonymous = Client(null, canWrite: false);
        var page = await anonymous.GetAsync("/connectors/");
        var bare = await anonymous.GetAsync("/connectors");
        var script = await anonymous.GetAsync("/connectors/app.js");
        var health = await anonymous.GetAsync("/connectors/health");
        var other = await anonymous.GetAsync("/connectors/secrets.json");

        Assert.Equal("José Pérez (JP)", me.GetProperty("user").GetString());
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("/connectors/", bare.Headers.Location?.OriginalString);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("/connectors/app.js", await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("const esc = s =>", await script.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ok", (await Json(health)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    // --- create / edit -----------------------------------------------------------------

    [Fact]
    public async Task Create_RejectsBadSettings_FieldByField()
    {
        var response = await _client.PostAsJsonAsync("/connectors/api/connectors", new
        {
            id = "Shop 2!",
            storeUrl = "shop2",
            priceLevel = 9,
            publishNewAs = "live",
            webhookBaseUrl = "http://shop2.example.com/hooks?x=1",
            payOnCompletionMethods = new[] { "cash on delivery" },
            consumerKey = "ck with spaces",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await Json(response)).GetProperty("errors");
        foreach (var field in new[] { "id", "storeUrl", "priceLevel", "publishNewAs", "webhookBaseUrl", "payOnCompletionMethods", "consumerKey" })
            Assert.True(errors.TryGetProperty(field, out _), $"no error for {field}: {errors}");
        Assert.Single(ConnectorConfigFile.Load(ConfigPath).Connectors);
        Assert.Empty(_secrets.List());
    }

    [Fact]
    public async Task Create_SavesSettings_StoresTheWooKeysEncryptedOnly_AndNeverReturnsThem()
    {
        var response = await _client.PostAsJsonAsync("/connectors/api/connectors", NewShop());
        var body = await response.Content.ReadAsStringAsync();
        var list = await (await _client.GetAsync("/connectors/api/connectors")).Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        foreach (var text in new[] { body, list, File.ReadAllText(ConfigPath) })
        {
            Assert.DoesNotContain("ck_live_9f8e7d6c5b4a", text);
            Assert.DoesNotContain("cs_live_1a2b3c4d5e6f", text);
        }
        Assert.Equal("ck_live_9f8e7d6c5b4a", _secrets.Get(ConnectorSecretNames.WooConsumerKey("shop2")));
        Assert.Equal("cs_live_1a2b3c4d5e6f", _secrets.Get(ConnectorSecretNames.WooConsumerSecret("shop2")));

        var saved = ConnectorConfigFile.Load(ConfigPath).Connectors.Single(c => c.Id == "shop2");
        Assert.False(saved.Enabled); // enabled from the wizard's last step
        Assert.Equal("https://shop2.example.com", saved.StoreUrl);
        Assert.Equal("WOO-shop2", saved.DeveloperId);
        Assert.Equal(2, saved.PriceLevel);
        Assert.Equal(1m, saved.StockBuffer);
        Assert.Equal(new[] { "cod", "cheque" }, saved.PayOnCompletionMethods);
        Assert.Equal("https://shop2.ngrok.app", saved.WebhookBaseUrl);

        var view = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.True(view.GetProperty("secrets").GetProperty("wooConsumerKey").GetBoolean());
        Assert.False(view.GetProperty("secrets").GetProperty("posApiKey").GetBoolean());
        Assert.Equal("https://shop2.ngrok.app/hooks/woocommerce/shop2",
            view.GetProperty("status").GetProperty("webhooks").GetProperty("deliveryUrl").GetString());

        // Orders: from now, so history is not imported.
        var cursor = await Stores.Cursors.GetAsync("shop2", ConnectorCursorNames.OrdersModifiedAfter);
        Assert.True(DateTime.UtcNow - DateTime.Parse(cursor!, null, System.Globalization.DateTimeStyles.AdjustToUniversal) < TimeSpan.FromMinutes(1));

        var audit = Assert.Single(_log.Audit());
        Assert.Contains("create", audit);
        Assert.Contains(User, audit);
        Assert.DoesNotContain("ck_live", audit);
    }

    [Fact]
    public async Task Create_ADuplicateIdOrAnotherConnectorsDeveloperId_IsRefused()
    {
        var duplicate = await _client.PostAsJsonAsync("/connectors/api/connectors", NewShop("shop1"));
        // shop1 now records the Developer ID shop3 would default to (e.g. its key was minted that way).
        ConnectorConfigFile.Save(ConfigPath, new ConnectorsDocument
        {
            Connectors = [new ConnectorSettings { Id = "shop1", StoreUrl = "https://shop.example.com", DeveloperId = "woo-SHOP3 " }],
        });
        var clash = await _client.PostAsJsonAsync("/connectors/api/connectors", NewShop("shop3"));

        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.True((await Json(duplicate)).GetProperty("errors").TryGetProperty("id", out _));
        Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);
        Assert.Contains("shop1", (await Json(clash)).GetProperty("errors").GetProperty("developerId")[0].GetString());
        Assert.Single(ConnectorConfigFile.Load(ConfigPath).Connectors);
    }

    [Fact]
    public async Task Create_CanStartTheOrderImportAtADate()
    {
        var since = DateOnly.FromDateTime(DateTime.Today.AddDays(-3));
        var response = await _client.PostAsJsonAsync("/connectors/api/connectors",
            new { id = "shop3", storeUrl = "https://shop3.example.com", orderStart = new { mode = "since", since = since.ToString("yyyy-MM-dd") } });
        var future = await _client.PostAsJsonAsync("/connectors/api/connectors",
            new { id = "shop4", storeUrl = "https://shop4.example.com", orderStart = new { mode = "since", since = DateTime.Today.AddDays(5).ToString("yyyy-MM-dd") } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var cursor = DateTime.Parse((await Stores.Cursors.GetAsync("shop3", ConnectorCursorNames.OrdersModifiedAfter))!, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(since.ToDateTime(TimeOnly.MinValue)), cursor);
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
    }

    [Fact]
    public async Task Update_KeepsStoredKeysWhenBlank_AndTheIdCannotChange()
    {
        _secrets.Set(ConnectorSecretNames.WooConsumerKey("shop1"), "ck_old");
        _secrets.Set(ConnectorSecretNames.WooConsumerSecret("shop1"), "cs_old");

        var update = await _client.PutAsJsonAsync("/connectors/api/connectors/shop1",
            new { stockBuffer = 3, importOnHold = true, consumerKey = "", webhookBaseUrl = "" });
        var rename = await _client.PutAsJsonAsync("/connectors/api/connectors/shop1", new { id = "shop9" });
        var missing = await _client.PutAsJsonAsync("/connectors/api/connectors/nope", new { stockBuffer = 1 });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var saved = ConnectorConfigFile.Load(ConfigPath).Connectors.Single();
        Assert.Equal(3m, saved.StockBuffer);
        Assert.True(saved.ImportOnHold);
        Assert.Null(saved.WebhookBaseUrl);
        Assert.Equal("ck_old", _secrets.Get(ConnectorSecretNames.WooConsumerKey("shop1")));
        Assert.Equal(HttpStatusCode.BadRequest, rename.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains(_log.Audit(), a => a.Contains("stockBuffer 0 -> 3"));
    }

    [Fact]
    public async Task Update_PaymentTenders_SavesTheMapWithoutCardRows_AuditsIt_AndRefusesAnUnknownTender()
    {
        var update = await _client.PutAsJsonAsync("/connectors/api/connectors/shop1", new
        {
            paymentTenders = new Dictionary<string, string> { ["bacs"] = "ONACCOUNT", ["cheque"] = "check", ["stripe"] = "card" },
        });
        var bad = await _client.PutAsJsonAsync("/connectors/api/connectors/shop1", new
        {
            paymentTenders = new Dictionary<string, string> { ["cod"] = "cash" },
        });
        var badId = await _client.PutAsJsonAsync("/connectors/api/connectors/shop1", new
        {
            paymentTenders = new Dictionary<string, string> { ["cash on delivery"] = "check" },
        });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var saved = ConnectorConfigFile.Load(ConfigPath).Connectors.Single();
        Assert.Equal(new Dictionary<string, string> { ["bacs"] = "onAccount", ["cheque"] = "check" }, saved.PaymentTenders);
        var view = await Json(update);
        Assert.Equal("onAccount", view.GetProperty("paymentTenders").GetProperty("bacs").GetString());
        Assert.Contains(_log.Audit(), a => a.Contains("paymentTenders (all card) -> bacs=onAccount,cheque=check"));

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("not a tender", (await Json(bad)).GetProperty("errors").GetProperty("paymentTenders")[0].GetString());
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
        Assert.True((await Json(badId)).GetProperty("errors").TryGetProperty("paymentTenders", out _));
        Assert.Equal(2, ConnectorConfigFile.Load(ConfigPath).Connectors.Single().PaymentTenders.Count); // unchanged
    }

    [Fact]
    public async Task PaymentGateways_ListsEnabledGatewaysWithTheirTender_PlusMappedOnesWooNoLongerEnables()
    {
        await _client.PutAsJsonAsync("/connectors/api/connectors/shop1", new
        {
            paymentTenders = new Dictionary<string, string> { ["cheque"] = "check", ["old-gateway"] = "onAccount" },
        });
        _wooItems.Gateways =
        [
            new WooGatewaySummary("stripe", "Credit card (Stripe)", true),
            new WooGatewaySummary("cheque", "Check payments", true),
            new WooGatewaySummary("bacs", "Direct bank transfer", false),
        ];

        var body = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/payment-gateways"));

        Assert.True(body.GetProperty("read").GetBoolean());
        Assert.Equal(["card", "check", "onAccount"], body.GetProperty("tenders").EnumerateArray().Select(t => t.GetString()));
        var rows = body.GetProperty("gateways").EnumerateArray()
            .Select(g => (g.GetProperty("id").GetString(), g.GetProperty("enabled").GetBoolean(), g.GetProperty("tender").GetString())).ToList();
        Assert.Equal([("stripe", true, "card"), ("cheque", true, "check"), ("old-gateway", false, "onAccount")], rows);

        _wooItems.Gateways = null; // Woo unreadable: the mapped methods only
        var offline = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/payment-gateways"));
        Assert.False(offline.GetProperty("read").GetBoolean());
        Assert.Equal(2, offline.GetProperty("gateways").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/connectors/api/connectors/nope/payment-gateways")).StatusCode);
    }

    [Fact]
    public async Task Enable_NeedsTheWooKeysAndThePosKey_ThenEnables()
    {
        var early = await _client.PostAsync("/connectors/api/connectors/shop1/enable", null);
        _secrets.Set(ConnectorSecretNames.WooConsumerKey("shop1"), "ck");
        _secrets.Set(ConnectorSecretNames.WooConsumerSecret("shop1"), "cs");
        _secrets.Set(ConnectorSecretNames.PosApiKey("shop1"), "key");
        var ready = await _client.PostAsync("/connectors/api/connectors/shop1/enable", null);
        var off = await _client.PostAsync("/connectors/api/connectors/shop1/disable", null);

        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        var problems = (await Json(early)).GetProperty("problems").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains(problems, p => p!.Contains("consumer key"));
        Assert.Contains(problems, p => p!.Contains("POSitive API key"));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.True((await Json(ready)).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False(ConnectorConfigFile.Load(ConfigPath).Connectors.Single().Enabled);
    }

    [Fact]
    public async Task Remove_DropsTheConnectorAndItsSecrets_ButKeepsItsData()
    {
        _secrets.Set(ConnectorSecretNames.PosApiKey("shop1"), "key");
        _secrets.Set(ConnectorSecretNames.WooWebhookSecret("shop1"), "hook");
        await Stores.Exceptions.RaiseAsync(new ConnectorExceptionInput("shop1", OrderExceptionKinds.Rejected, "59", "refused"));

        var response = await _client.DeleteAsync("/connectors/api/connectors/shop1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(ConnectorConfigFile.Load(ConfigPath).Connectors);
        Assert.Empty(_secrets.List());
        Assert.Equal(1, await Stores.Exceptions.CountOpenAsync("shop1"));
    }

    [Fact]
    public async Task TestConnection_UsesTheStoredKeysWhenNoneAreTyped_AndExplainsARefusal()
    {
        _secrets.Set(ConnectorSecretNames.WooConsumerKey("shop1"), "ck_stored");
        _secrets.Set(ConnectorSecretNames.WooConsumerSecret("shop1"), "cs_stored");

        var ok = await Json(await _client.PostAsJsonAsync("/connectors/api/test-connection", new { connectorId = "shop1" }));
        _woo.Fail = new WooApiException("Unauthorized", HttpStatusCode.Unauthorized, "woocommerce_rest_authentication_error", transient: false);
        var refused = await Json(await _client.PostAsJsonAsync("/connectors/api/test-connection",
            new { storeUrl = "https://shop.example.com", consumerKey = "ck_x", consumerSecret = "cs_x" }));

        Assert.True(ok.GetProperty("ok").GetBoolean());
        Assert.Equal("NZD", ok.GetProperty("info").GetProperty("currency").GetString());
        Assert.Equal(("ck_stored", "cs_stored"), (_woo.Calls[0].Key, _woo.Calls[0].Secret));
        Assert.DoesNotContain("ck_stored", ok.ToString());
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Contains("consumer key", refused.GetProperty("error").GetString());
    }

    // --- the connector's POSitive API key (wizard step 2) ----------------------------------------

    [Fact]
    public async Task PosKey_IsCheckedWithWhoAmI_StoredWithTheExpectedDeveloperId_AndNeverEchoed()
    {
        _keys.Answers["Kx7pQ2mWz9RtN4vB"] = new PosKeyCheck(true, "WOO-shop1", 1, null);

        var response = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/pos-key", new { apiKey = " Kx7pQ2mWz9RtN4vB " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Kx7pQ2mWz9RtN4vB", await response.Content.ReadAsStringAsync());
        Assert.Equal("Kx7pQ2mWz9RtN4vB", _secrets.Get(ConnectorSecretNames.PosApiKey("shop1")));
        Assert.Equal("Kx7pQ2mWz9RtN4vB", _keys.Calls.Single());
        Assert.Contains(_log.Audit(), a => a.Contains("pos-key stored") && a.Contains("WOO-shop1") && !a.Contains("Kx7pQ2"));

        var verify = await Json(await _client.PostAsync("/connectors/api/connectors/shop1/pos-key/verify", null));
        Assert.True(verify.GetProperty("ok").GetBoolean());
    }

    /// <summary>A fake PA.Server answering GET /api/whoami with a fixed JSON body (the key must arrive in X-Api-Key).</summary>
    private sealed class FakeWhoAmI(string body) : HttpMessageHandler
    {
        public List<(Uri? Url, string? ApiKey)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri,
                request.Headers.TryGetValues(PosApiClient.ApiKeyHeader, out var keys) ? keys.SingleOrDefault() : null));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static PosKeyVerifier RealVerifier(HttpMessageHandler whoami) =>
        new(new SingleClientFactory(whoami), new PosApiEndpoint(new Uri("http://localhost:5210")),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

    // PA.Server's /api/whoami sends settingsId as a string ("1"); the verifier used
    // TryGetInt32, which throws on a string, so the wizard's key step answered 500. A number is
    // accepted too, in case the server ever sends one.
    [Theory]
    [InlineData("""{"developerId":"WOO-shop1","settingsId":"1"}""")]
    [InlineData("""{"developerId":"WOO-shop1","settingsId":1}""")]
    public async Task PosKeyVerifier_ReadsSettingsId_AsAStringOrANumber(string body)
    {
        var whoami = new FakeWhoAmI(body);

        var check = await RealVerifier(whoami).VerifyAsync("Kx7pQ2mWz9RtN4vB", CancellationToken.None);

        Assert.True(check.Ok, check.Error);
        Assert.Equal("WOO-shop1", check.DeveloperId);
        Assert.Equal(1, check.SettingsId);
        var request = Assert.Single(whoami.Requests);
        Assert.Equal("http://localhost:5210/api/whoami", request.Url?.ToString());
        Assert.Equal("Kx7pQ2mWz9RtN4vB", request.ApiKey);
    }

    [Theory]
    [InlineData("""{"developerId":"WOO-shop1","settingsId":"not-a-number"}""")]
    [InlineData("""{"developerId":"WOO-shop1","settingsId":null}""")]
    [InlineData("""{"developerId":"WOO-shop1"}""")]
    public async Task PosKeyVerifier_AnUnreadableSettingsId_IsNullNotAnError(string body)
    {
        var check = await RealVerifier(new FakeWhoAmI(body)).VerifyAsync("Kx7pQ2mWz9RtN4vB", CancellationToken.None);

        Assert.True(check.Ok, check.Error);
        Assert.Equal("WOO-shop1", check.DeveloperId);
        Assert.Null(check.SettingsId);
    }

    [Theory]
    [InlineData("""{"developerId":"WOO-shop1","settingsId":"1"}""")]
    [InlineData("""{"developerId":"WOO-shop1","settingsId":1}""")]
    public async Task PosKey_StoreStep_Succeeds_WhateverTypeWhoAmISendsSettingsIdAs(string body)
    {
        _keys.Real = RealVerifier(new FakeWhoAmI(body));

        var response = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/pos-key", new { apiKey = "Kx7pQ2mWz9RtN4vB" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Kx7pQ2mWz9RtN4vB", _secrets.Get(ConnectorSecretNames.PosApiKey("shop1")));
        var verify = await Json(await _client.PostAsync("/connectors/api/connectors/shop1/pos-key/verify", null));
        Assert.True(verify.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task PosKey_WithAnotherDeveloperId_NeedsAConfirm_AndARejectedKeyIsNotStored()
    {
        _keys.Answers["other-key-0001"] = new PosKeyCheck(true, "WEBSHOP", 1, null);

        var rejected = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/pos-key", new { apiKey = "wrong-key-0001" });
        var mismatch = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/pos-key", new { apiKey = "other-key-0001" });
        Assert.Null(_secrets.Get(ConnectorSecretNames.PosApiKey("shop1")));
        var confirmed = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/pos-key",
            new { apiKey = "other-key-0001", confirmDeveloperId = true });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Equal("api_key_rejected", (await Json(rejected)).GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        Assert.Equal("developer_id_mismatch", (await Json(mismatch)).GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal("other-key-0001", _secrets.Get(ConnectorSecretNames.PosApiKey("shop1")));
        Assert.Equal("WEBSHOP", ConnectorConfigFile.Load(ConfigPath).Connectors.Single().DeveloperId);
    }

    [Fact]
    public async Task PosKey_OfAnotherConnectorsDeveloperId_IsAlwaysRefused()
    {
        await _client.PostAsJsonAsync("/connectors/api/connectors", NewShop("shop2"));
        _keys.Answers["shop1-key-0001"] = new PosKeyCheck(true, "WOO-shop1", 1, null);

        var response = await _client.PostAsJsonAsync("/connectors/api/connectors/shop2/pos-key",
            new { apiKey = "shop1-key-0001", confirmDeveloperId = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("developer_id_in_use", (await Json(response)).GetProperty("errorCode").GetString());
        Assert.Null(_secrets.Get(ConnectorSecretNames.PosApiKey("shop2")));
    }

    // --- security review of the connector code ---------------------------------------------------------------

    [Fact]
    public async Task H1_ThePosApiAddressIsNotASetting_APutNamingOneChangesNothing_AndTheFileDropsAnOldOne()
    {
        // An older connectors.json may still carry posApiBaseUrl: it is ignored on load…
        File.WriteAllText(ConfigPath,
            "{\"connectors\":[{\"id\":\"shop1\",\"storeUrl\":\"https://shop.example.com\",\"developerId\":\"WOO-shop1\",\"posApiBaseUrl\":\"https://evil.example\"}]}");
        _keys.Answers["Kx7pQ2mWz9RtN4vB"] = new PosKeyCheck(true, "WOO-shop1", 1, null);

        var put = await _client.PutAsJsonAsync("/connectors/api/connectors/shop1", new { posApiBaseUrl = "https://attacker.example", stockBuffer = 2 });
        var view = await put.Content.ReadAsStringAsync();
        await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/pos-key", new { apiKey = "Kx7pQ2mWz9RtN4vB" });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.DoesNotContain("posApiBaseUrl", view, StringComparison.OrdinalIgnoreCase);
        // …and dropped on the next save.
        Assert.DoesNotContain("posApiBaseUrl", File.ReadAllText(ConfigPath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil", File.ReadAllText(ConfigPath));
        // The key check goes to the host's own address (the verifier gets no address from the request).
        Assert.Equal("Kx7pQ2mWz9RtN4vB", _keys.Calls.Single());
        Assert.Equal(new Uri("http://localhost:5210"), _app.Services.GetRequiredService<PosApiEndpoint>().BaseUrl);
    }

    [Fact]
    public async Task M1_StoredWooKeysAreUsedOnlyForTheSavedOrigin_AndOnlyAsAPair()
    {
        _secrets.Set(ConnectorSecretNames.WooConsumerKey("shop1"), "ck_stored");
        _secrets.Set(ConnectorSecretNames.WooConsumerSecret("shop1"), "cs_stored");

        var elsewhere = await _client.PostAsJsonAsync("/connectors/api/test-connection",
            new { connectorId = "shop1", storeUrl = "https://attacker.example.com" });
        var otherPort = await _client.PostAsJsonAsync("/connectors/api/test-connection",
            new { connectorId = "shop1", storeUrl = "https://shop.example.com:8443" });
        var otherScheme = await _client.PostAsJsonAsync("/connectors/api/test-connection",
            new { connectorId = "shop1", storeUrl = "http://shop.example.com" });
        var halfPair = await _client.PostAsJsonAsync("/connectors/api/test-connection",
            new { connectorId = "shop1", storeUrl = "https://attacker.example.com", consumerKey = "ck_mine" });
        var samePath = await _client.PostAsJsonAsync("/connectors/api/test-connection",
            new { connectorId = "shop1", storeUrl = "https://SHOP.example.com/shop/" });

        foreach (var refused in new[] { elsewhere, otherPort, otherScheme, halfPair })
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.True((await Json(halfPair)).GetProperty("errors").TryGetProperty("consumerSecret", out _));
        Assert.Equal(HttpStatusCode.OK, samePath.StatusCode);
        var call = Assert.Single(_woo.Calls); // only the same-origin test reached the store, with the stored pair
        Assert.Equal(("shop.example.com", "ck_stored", "cs_stored"), (call.Url.Host, call.Key, call.Secret));
    }

    [Fact]
    public async Task M1_APutThatMovesTheStore_ClearsTheOldKeys_UnlessBothAreReEntered()
    {
        _secrets.Set(ConnectorSecretNames.WooConsumerKey("shop1"), "ck_old");
        _secrets.Set(ConnectorSecretNames.WooConsumerSecret("shop1"), "cs_old");

        var samePlace = await _client.PutAsJsonAsync("/connectors/api/connectors/shop1", new { storeUrl = "https://shop.example.com/" });
        Assert.Equal(HttpStatusCode.OK, samePlace.StatusCode);
        Assert.Equal("ck_old", _secrets.Get(ConnectorSecretNames.WooConsumerKey("shop1")));

        var oneKey = await _client.PutAsJsonAsync("/connectors/api/connectors/shop1",
            new { storeUrl = "https://new-shop.example.com", consumerKey = "ck_new" });
        Assert.Equal(HttpStatusCode.BadRequest, oneKey.StatusCode);
        Assert.Equal("https://shop.example.com", ConnectorConfigFile.Load(ConfigPath).Connectors.Single().StoreUrl);

        var moved = await _client.PutAsJsonAsync("/connectors/api/connectors/shop1", new { storeUrl = "https://new-shop.example.com" });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Null(_secrets.Get(ConnectorSecretNames.WooConsumerKey("shop1")));
        Assert.Null(_secrets.Get(ConnectorSecretNames.WooConsumerSecret("shop1")));
        Assert.False((await Json(moved)).GetProperty("secrets").GetProperty("wooConsumerKey").GetBoolean());
        Assert.Contains(_log.Audit(), a => a.Contains("Woo keys cleared"));

        var withKeys = await _client.PutAsJsonAsync("/connectors/api/connectors/shop1",
            new { storeUrl = "https://third.example.com", consumerKey = "ck_3", consumerSecret = "cs_3" });
        Assert.Equal(HttpStatusCode.OK, withKeys.StatusCode);
        Assert.Equal("ck_3", _secrets.Get(ConnectorSecretNames.WooConsumerKey("shop1")));
        Assert.Equal("cs_3", _secrets.Get(ConnectorSecretNames.WooConsumerSecret("shop1")));
    }

    [Theory]
    [InlineData("http://localhost:8090")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://[::1]:8080")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("https://10.1.2.3")]
    [InlineData("https://172.20.0.1")]
    [InlineData("https://192.168.1.10")]
    [InlineData("https://[fd00::1]")]
    [InlineData("https://[fe80::1]")]
    [InlineData("http://0.0.0.0")]
    [InlineData("https://internal.example.com")]
    [InlineData("https://metadata.example.com")]
    [InlineData("https://mixed.example.com")]
    [InlineData("https://admin:pw@shop.example.com")]
    [InlineData("https://nowhere.invalid")]
    public async Task M2_StoreAddressesInsideTheNetwork_AreRefused_OnCreateEditAndTest(string storeUrl)
    {
        var create = await _client.PostAsJsonAsync("/connectors/api/connectors", new { id = "bad", storeUrl });
        var edit = await _client.PutAsJsonAsync("/connectors/api/connectors/shop1", new { storeUrl });
        var test = await _client.PostAsJsonAsync("/connectors/api/test-connection", new { storeUrl, consumerKey = "ck_x", consumerSecret = "cs_x" });

        foreach (var response in new[] { create, edit, test })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await Json(response)).GetProperty("errors").TryGetProperty("storeUrl", out _));
        }
        Assert.Empty(_woo.Calls);
        Assert.Equal("https://shop.example.com", ConnectorConfigFile.Load(ConfigPath).Connectors.Single().StoreUrl);
        Assert.DoesNotContain(_log.Events, e => e.RenderMessage().Contains("admin:pw"));
    }

    [Fact]
    public async Task M2_TestConnectionErrors_GiveStatusAndWooCodeOnly_NeverTheBodyOrTransportText()
    {
        _woo.Fail = new WooApiException("GET /wp-json/wc/v3/products → 500 internal_error: <html>secret page</html>",
            HttpStatusCode.InternalServerError, "internal_error", transient: true) { WooMessage = "Something broke." };
        var http = await Json(await _client.PostAsJsonAsync("/connectors/api/test-connection",
            new { storeUrl = "https://shop.example.com", consumerKey = "ck_x", consumerSecret = "cs_x" }));
        _woo.Fail = new WooApiException("GET /x failed: No such host is known. (10.0.0.9:443)", null, "connection_failed", transient: true,
            new HttpRequestException("No such host is known. (10.0.0.9:443)"));
        var transport = await Json(await _client.PostAsJsonAsync("/connectors/api/test-connection",
            new { storeUrl = "https://shop.example.com", consumerKey = "ck_x", consumerSecret = "cs_x" }));
        _woo.Fail = new WooApiException("GET /x failed", null, "connection_failed", transient: true,
            new HttpRequestException("blocked", new StoreAddressBlockedException("shop.example.com")));
        var blocked = await Json(await _client.PostAsJsonAsync("/connectors/api/test-connection",
            new { storeUrl = "https://shop.example.com", consumerKey = "ck_x", consumerSecret = "cs_x" }));

        Assert.Equal("WooCommerce answered HTTP 500 (internal_error): Something broke.", http.GetProperty("error").GetString());
        Assert.DoesNotContain("secret page", http.ToString());
        Assert.Equal(500, http.GetProperty("status").GetInt32());
        Assert.DoesNotContain("10.0.0.9", transport.ToString());
        Assert.Contains("could not be reached", transport.GetProperty("error").GetString());
        Assert.Contains("private network", blocked.GetProperty("error").GetString());
    }

    [Fact]
    public async Task M3_PortalHeadersWithoutTheForwardToken_AreAnonymous()
    {
        var noToken = Client(User, canWrite: true, forwardToken: null);
        var wrongToken = Client(User, canWrite: true, forwardToken: ForwardToken[..^1] + "X");

        foreach (var forged in new[] { noToken, wrongToken })
        {
            var read = await forged.GetAsync("/connectors/api/connectors");
            var write = await forged.PostAsJsonAsync("/connectors/api/connectors", NewShop());
            Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
            Assert.Equal("portal_user_required", (await Json(write)).GetProperty("errorCode").GetString());
        }
        Assert.Single(ConnectorConfigFile.Load(ConfigPath).Connectors);
        // The pages themselves need no user.
        Assert.Equal(HttpStatusCode.OK, (await noToken.GetAsync("/connectors/")).StatusCode);
    }

    [Fact]
    public async Task L1_AForwardedPageRequestThatLeavesConnectors_IsRefused()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("path_not_allowed", (await Json(response)).GetProperty("errorCode").GetString());
        // Without a portal user (a local tool) /health still answers.
        Assert.Equal(HttpStatusCode.OK, (await Client(null, canWrite: false, forwardToken: null).GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task L2_TestConnectionAndKeyChecks_AreRateLimitedPerUser()
    {
        var body = new { storeUrl = "https://shop.example.com", consumerKey = "ck_x", consumerSecret = "cs_x" };
        for (var i = 0; i < ConnectorUiRateLimiter.Limit; i++)
            Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/connectors/api/test-connection", body)).StatusCode);
        var over = await _client.PostAsJsonAsync("/connectors/api/test-connection", body);
        var otherUser = await Client("Someone Else (SE)", canWrite: true).PostAsJsonAsync("/connectors/api/test-connection", body);

        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        Assert.Equal("rate_limited", (await Json(over)).GetProperty("errorCode").GetString());
        Assert.True(over.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.OK, otherUser.StatusCode);
        Assert.Equal(ConnectorUiRateLimiter.Limit + 1, _woo.Calls.Count);

        _secrets.Set(ConnectorSecretNames.PosApiKey("shop1"), "key");
        var verifies = new List<HttpStatusCode>();
        for (var i = 0; i <= ConnectorUiRateLimiter.Limit; i++)
            verifies.Add((await _client.PostAsync("/connectors/api/connectors/shop1/pos-key/verify", null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, verifies[^1]);
        Assert.Equal(ConnectorUiRateLimiter.Limit, _keys.Calls.Count);
    }

    [Fact]
    public void L2_TheLimiterWindowSlides()
    {
        var time = new ManualClock(DateTimeOffset.UtcNow);
        var limiter = new ConnectorUiRateLimiter(time);
        for (var i = 0; i < ConnectorUiRateLimiter.Limit; i++)
            Assert.True(limiter.TryAcquire("u", "b", out _));
        Assert.False(limiter.TryAcquire("U", "b", out var wait));
        Assert.Equal(ConnectorUiRateLimiter.Window, wait);
        Assert.True(limiter.TryAcquire("u", "other", out _));
        time.Now += ConnectorUiRateLimiter.Window;
        Assert.True(limiter.TryAcquire("u", "b", out _));
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task L3_AnUnreadableConfig_ShowsAFixedMessage_NotThePathOrTheParserText()
    {
        await File.WriteAllTextAsync(ConfigPath, "{ not json");

        var list = await (await _client.GetAsync("/connectors/api/connectors")).Content.ReadAsStringAsync();
        var health = await (await Client(null, canWrite: false).GetAsync("/connectors/health")).Content.ReadAsStringAsync();

        foreach (var text in new[] { list, health })
        {
            Assert.Contains("could not be read", text);
            Assert.DoesNotContain(_dir.Replace("\\", "\\\\"), text);
            Assert.DoesNotContain("connectors.json", text);
            Assert.DoesNotContain("configPath", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("invalid", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- match review: jobs, never inline --------------------------------------------------------

    [Fact]
    public async Task RunMatch_And_CreateInWoo_EnqueueJobs_ThatCoalesce()
    {
        var first = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/review/match", new { createMissing = false });
        var again = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/review/match", new { createMissing = false });
        var create = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/review/create", new { invNos = new[] { 2002, 2001, 2002 } });
        var none = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/review/create", new { invNos = Array.Empty<int>() });

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.True((await Json(again)).GetProperty("coalesced").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);

        var jobs = await UiStore.ListJobsAsync("shop1");
        var match = Assert.Single(jobs, j => j.Kind == ReviewJobKinds.CatalogMatch);
        Assert.False(ReviewJobKinds.Read<CatalogMatchPayload>(match.Payload)!.CreateMissing);
        var created = Assert.Single(jobs, j => j.Kind == ReviewJobKinds.CatalogCreate);
        Assert.Equal(new[] { 2001, 2002 }, ReviewJobKinds.Read<CatalogCreatePayload>(created.Payload)!.InvNos);
        Assert.Equal(created.JobId, (await Json(create)).GetProperty("jobId").GetInt64());

        var job = await Json(await _client.GetAsync($"/connectors/api/connectors/shop1/jobs/{match.JobId}"));
        Assert.Equal("pending", job.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Link_FromAnUnmatchedOrderLine_QueuesTheLink_WithTheOrderReImport()
    {
        var exceptionId = await Stores.Exceptions.RaiseAsync(new ConnectorExceptionInput(
            "shop1", OrderExceptionKinds.UnmatchedProduct, "59", "line 3 has no POSitive item"));

        var response = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/review/link",
            new { externalId = 34, invNo = 2001, exceptionId });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = Assert.Single(await UiStore.ListJobsAsync("shop1"));
        Assert.Equal((ReviewJobKinds.CatalogLink, "woo:34"), (job.Kind, job.ResourceKey));
        Assert.Equal(new CatalogLinkPayload(2001, 34, 59), ReviewJobKinds.Read<CatalogLinkPayload>(job.Payload));
        Assert.Contains(_log.Audit(), a => a.Contains("Woo item 34 -> POSitive item 2001") && a.Contains("re-import Woo order 59"));
    }

    [Fact]
    public async Task Ignore_ClosesTheReviewException_AndTheItemStopsReRaising_UntilUnignored()
    {
        var ignoring = new IgnoreAwareExceptionStore(Stores.Exceptions, UiStore);
        await ignoring.RaiseAsync(new ConnectorExceptionInput("shop1", CatalogExceptionKinds.UnmatchedPlatform, "77", "no match"));

        var ignore = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/review/ignore",
            new { externalRef = "77", kind = CatalogExceptionKinds.UnmatchedPlatform, note = "sample product" });
        var raisedAgain = await ignoring.RaiseAsync(new ConnectorExceptionInput("shop1", CatalogExceptionKinds.UnmatchedPlatform, "77", "no match"));
        // Only review kinds are suppressed: a push failure on the same id still shows.
        await ignoring.RaiseAsync(new ConnectorExceptionInput("shop1", ConnectorExceptionKinds.PushFailed, "77", "push failed"));
        var review = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/review"));

        Assert.Equal(HttpStatusCode.OK, ignore.StatusCode);
        Assert.Equal(1, (await Json(ignore)).GetProperty("closed").GetInt32());
        Assert.Equal(0, raisedAgain);
        Assert.Equal("77", review.GetProperty("ignored")[0].GetProperty("externalRef").GetString());
        // A push failure is an exception (needs action), so it is not on the match review.
        Assert.Empty(review.GetProperty("items").EnumerateArray());
        Assert.Equal(1, review.GetProperty("openExceptions").GetInt32());
        var exceptions = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/exceptions"));
        Assert.Equal("push_failed", Assert.Single(exceptions.GetProperty("items").EnumerateArray()).GetProperty("kind").GetString());

        var unignore = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/review/unignore", new { externalRef = "77" });
        Assert.Equal(HttpStatusCode.OK, unignore.StatusCode);
        Assert.NotEqual(0, await ignoring.RaiseAsync(new ConnectorExceptionInput("shop1", CatalogExceptionKinds.UnmatchedPlatform, "77", "no match")));

        var badKind = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/review/ignore",
            new { externalRef = "59", kind = OrderExceptionKinds.Rejected });
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);
    }

    [Fact]
    public async Task SearchProducts_UsesTheConnectorsGateway_ByInvNoSkuAndDescription()
    {
        _secrets.Set(ConnectorSecretNames.PosApiKey("shop1"), "key");
        _pos.Add(2001, "CAP-1", 10m, 5m).Description = "Blue cap";
        _pos.Add(2002, "HAT-2", 12m, 1m).Description = "Sun hat";
        _pos.Add(2003, "CAP-3", 12m, 1m).Description = "Red cap";

        var byText = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/pos-products?q=cap"));
        var byInv = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/pos-products?q=2002"));
        var blank = await _client.GetAsync("/connectors/api/connectors/shop1/pos-products?q=");

        Assert.Equal(new[] { 2001, 2003 }, byText.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("invNo").GetInt32()).ToArray());
        Assert.Equal(2002, byInv.GetProperty("items")[0].GetProperty("invNo").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    // --- exceptions ---------------------------------------------------------------------------

    [Fact]
    public async Task Retry_ReQueuesTheRelatedJob_AndClosesTheExceptionAsRetried()
    {
        var order = await Stores.Exceptions.RaiseAsync(new ConnectorExceptionInput("shop1", OrderExceptionKinds.TotalsMismatch, "59", "totals"));
        var hooks = await Stores.Exceptions.RaiseAsync(new ConnectorExceptionInput("shop1", ConnectorExceptionKinds.WebhookUnhealthy, "order.created", "disabled"));
        await Stores.Items.UpsertAsync(new ConnectorItem("shop1", 2001, 16, 0, CatalogItemTypes.Simple, "CAP-1", null, null));
        var push = await Stores.Exceptions.RaiseAsync(new ConnectorExceptionInput("shop1", ConnectorExceptionKinds.PushFailed, "16", "push"));
        var unmatched = await Stores.Exceptions.RaiseAsync(new ConnectorExceptionInput("shop1", CatalogExceptionKinds.UnmatchedPlatform, "77", "no match"));

        foreach (var id in new[] { order, hooks, push, unmatched })
            Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/connectors/api/connectors/shop1/exceptions/{id}/retry", null)).StatusCode);
        var twice = await _client.PostAsync($"/connectors/api/connectors/shop1/exceptions/{order}/retry", null);

        var jobs = await UiStore.ListJobsAsync("shop1");
        Assert.Contains(jobs, j => j.Kind == ConnectorJobKinds.OrderImport && j.ResourceKey == "59");
        Assert.Contains(jobs, j => j.Kind == ConnectorJobKinds.WebhookHealth);
        var pushJob = Assert.Single(jobs, j => j.Kind == ConnectorJobKinds.CatalogPush);
        Assert.Equal(new[] { 2001 }, CatalogPayloads.ReadInvNos(pushJob.Payload)!);
        Assert.False(ReviewJobKinds.Read<CatalogMatchPayload>(Assert.Single(jobs, j => j.Kind == ReviewJobKinds.CatalogMatch).Payload)!.CreateMissing);
        Assert.Equal(ConnectorExceptionStatuses.Retried, (await Stores.Exceptions.GetAsync(order))!.Status);
        Assert.Equal(User, (await Stores.Exceptions.GetAsync(order))!.ResolvedBy);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
    }

    [Fact]
    public async Task Retry_OfAJobFailure_ReRunsThatJob_AndOtherKindsSayWhyNot()
    {
        await Stores.Jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.OrderPoll, "", "{\"x\":1}"));
        var claimed = await Stores.Jobs.ClaimNextAsync("shop1", TimeSpan.FromMinutes(1));
        await Stores.Jobs.FailAsync(claimed!, "bad key", retryable: false, JobRetryPolicy.Default);
        var failed = (await Stores.Exceptions.ListAsync("shop1")).Single(e => e.Kind == ConnectorExceptionKinds.JobFailed);
        var odd = await Stores.Exceptions.RaiseAsync(new ConnectorExceptionInput("shop1", "something_else", "x", "?"));

        var retry = await _client.PostAsync($"/connectors/api/connectors/shop1/exceptions/{failed.ExceptionId}/retry", null);
        var cannot = await _client.PostAsync($"/connectors/api/connectors/shop1/exceptions/{odd}/retry", null);
        var otherConnector = await _client.PostAsync($"/connectors/api/connectors/shop2/exceptions/{odd}/retry", null);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var again = (await UiStore.ListJobsAsync("shop1")).First();
        Assert.Equal((ConnectorJobKinds.OrderPoll, "{\"x\":1}", ConnectorJobStatuses.Pending), (again.Kind, again.Payload, again.Status));
        Assert.Equal(HttpStatusCode.BadRequest, cannot.StatusCode);
        Assert.Equal("not_retryable", (await Json(cannot)).GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.NotFound, otherConnector.StatusCode);
    }

    [Fact]
    public async Task Exceptions_ListByStatusWithCounts_DetailHasTheSnapshot_AndIgnoreMarksReviewItems()
    {
        var order = await Stores.Exceptions.RaiseAsync(new ConnectorExceptionInput("shop1", OrderExceptionKinds.UnmatchedProduct, "59",
            "line has no item", Snapshot: "{\"orderId\":59,\"unmatched\":[{\"WooItemId\":34}]}"));
        var review = await Stores.Exceptions.RaiseAsync(new ConnectorExceptionInput("shop1", CatalogExceptionKinds.UnmatchedPos, "inv:2001", "matrix", PosRef: "2001"));

        var ignored = await _client.PostAsync($"/connectors/api/connectors/shop1/exceptions/{review}/ignore", null);
        var open = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/exceptions?status=open"));
        var closed = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/exceptions?status=closed"));
        var detail = await Json(await _client.GetAsync($"/connectors/api/connectors/shop1/exceptions/{order}"));
        var badStatus = await _client.GetAsync("/connectors/api/connectors/shop1/exceptions?status=weird");

        Assert.True((await Json(ignored)).GetProperty("itemIgnored").GetBoolean());
        Assert.True(await UiStore.IsIgnoredAsync("shop1", "inv:2001"));
        Assert.Equal(1, open.GetProperty("counts").GetProperty("open").GetInt32());
        // The ignored review item is a match review matter: not counted or listed among exceptions.
        Assert.Equal(0, open.GetProperty("counts").GetProperty("ignored").GetInt32());
        var listed = Assert.Single(open.GetProperty("items").EnumerateArray());
        Assert.True(listed.GetProperty("canLink").GetBoolean());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("snapshot").ValueKind); // the list stays light
        Assert.Empty(closed.GetProperty("items").EnumerateArray());
        Assert.Contains("WooItemId", detail.GetProperty("exception").GetProperty("snapshot").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
    }

    [Fact]
    public async Task ClosingAnOrderChangedAfterImport_IsAuditedAndLoggedAsHandled_OtherKindsAsIgnored()
    {
        var changed = await Stores.Exceptions.RaiseAsync(new ConnectorExceptionInput("shop1", OrderExceptionKinds.ChangedAfterImport, "58",
            "Woo order #58 is now 'cancelled' (Gina Guest) as invoice 50001919."));
        var refused = await Stores.Exceptions.RaiseAsync(new ConnectorExceptionInput("shop1", OrderExceptionKinds.Rejected, "59", "refused"));

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/connectors/api/connectors/shop1/exceptions/{changed}/ignore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/connectors/api/connectors/shop1/exceptions/{refused}/ignore", null)).StatusCode);

        Assert.Equal(ConnectorExceptionStatuses.Ignored, (await Stores.Exceptions.GetAsync(changed))!.Status); // same stored status
        var audit = _log.Audit();
        Assert.Contains(audit, a => a.Contains("handled") && a.Contains($"exception {changed} (order_changed_after_import 58) marked as handled"));
        Assert.Contains(audit, a => a.Contains($"exception {refused} (order_rejected 59) ignored"));
        var lines = _log.Events.Select(e => e.RenderMessage()).Where(m => m.StartsWith("Order exception", StringComparison.Ordinal)).ToList();
        Assert.Contains(lines, l => l.Contains("order_changed_after_import") && l.Contains("58") && l.Contains("marked as handled"));
        Assert.Contains(lines, l => l.Contains("order_rejected") && l.Contains("59") && l.Contains("ignored"));
        Assert.DoesNotContain(lines, l => l.Contains("Gina"));
    }

    [Fact]
    public async Task OrderStart_MovesThePollCursor_AndStatusReportsIt()
    {
        var moved = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/order-start", new { mode = "now" });
        var status = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/status"));

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal((await Json(moved)).GetProperty("ordersCursor").GetString(),
            status.GetProperty("connector").GetProperty("status").GetProperty("ordersCursor").GetString());
        Assert.Equal("polling", status.GetProperty("connector").GetProperty("status").GetProperty("webhooks").GetProperty("mode").GetString());
    }

    // --- 2026-10-09 manual-test findings (F1–F8) ------------------------------------------------

    private Task<long> RaiseAsync(string kind, string externalRef, string reason, string? snapshot = null, string? posRef = null) =>
        Stores.Exceptions.RaiseAsync(new ConnectorExceptionInput("shop1", kind, externalRef, reason, posRef, snapshot));

    // F2: what the match leaves for review is not an exception — counted and listed apart everywhere.
    [Fact]
    public async Task F2_ReviewItems_AreCountedApartFromExceptions_OnStatusExceptionsReviewAndHealth()
    {
        await RaiseAsync(CatalogExceptionKinds.UnmatchedPlatform, "77", "No POSitive web item has SKU 'X'; it stays unmanaged.");
        await RaiseAsync(CatalogExceptionKinds.UnmatchedPlatform, "78", "Woo simple product #78 has no SKU, so it cannot be matched; it stays unmanaged.");
        await RaiseAsync(CatalogExceptionKinds.UnmatchedPos, "inv:2001", "matrix item");
        await RaiseAsync(CatalogExceptionKinds.LinkConflict, "inv:2002", "already linked elsewhere");
        await RaiseAsync(OrderExceptionKinds.Rejected, "59", "guest without email");
        await RaiseAsync(CatalogExceptionKinds.ManualLinkRefused, "96", "Linking … was refused", posRef: "4198");

        var status = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/status"));
        var exceptions = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/exceptions"));
        var review = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/review"));
        var list = await Json(await _client.GetAsync("/connectors/api/connectors"));
        var health = await Json(await _client.GetAsync("/connectors/health"));

        var s = status.GetProperty("connector").GetProperty("status");
        Assert.Equal((2, 4), (s.GetProperty("openExceptions").GetInt32(), s.GetProperty("needsReview").GetInt32()));
        Assert.Equal(2, status.GetProperty("exceptions").GetProperty("open").GetInt32());
        Assert.Equal(new[] { "catalog_link_refused", "order_rejected" },
            exceptions.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("kind").GetString()).Order().ToArray());
        Assert.Equal(2, exceptions.GetProperty("counts").GetProperty("open").GetInt32());
        Assert.Equal(4, exceptions.GetProperty("needsReview").GetInt32());
        Assert.Equal(4, review.GetProperty("needsReview").GetInt32());
        Assert.Equal(2, review.GetProperty("openExceptions").GetInt32());
        Assert.DoesNotContain(review.GetProperty("items").EnumerateArray(), i => i.GetProperty("kind").GetString() is "order_rejected" or "catalog_link_refused");
        Assert.Equal(4, list.GetProperty("connectors")[0].GetProperty("status").GetProperty("needsReview").GetInt32());
        var shop = health.GetProperty("connectors")[0];
        Assert.Equal((2, 4), (shop.GetProperty("openExceptions").GetInt32(), shop.GetProperty("needsReview").GetInt32()));
        Assert.Equal((2, 4), (health.GetProperty("openExceptions").GetInt32(), health.GetProperty("needsReview").GetInt32()));
    }

    // F2: variations grouped under their parent (snapshot, or the reason of an older row), matrix items explained once.
    [Fact]
    public async Task F2_Review_GroupsVariationsUnderTheirParent_AndExplainsMatrixItemsOnce()
    {
        await RaiseAsync(CatalogExceptionKinds.UnmatchedPlatform, "51", "No POSitive web item has SKU 'TEE-R'.",
            """{"wooId":51,"parentId":50,"type":"variation","name":"Tee - Red","sku":"TEE-R","status":"publish"}""");
        await RaiseAsync(CatalogExceptionKinds.UnmatchedPlatform, "52", "Woo variation #52 of #50 has no SKU, so it cannot be matched; it stays unmanaged.");
        await RaiseAsync(CatalogExceptionKinds.UnmatchedPlatform, "60", "No POSitive web item has SKU 'MUG'.",
            """{"wooId":60,"parentId":0,"type":"simple","name":"Mug","sku":"MUG","status":"draft"}""");
        await RaiseAsync(CatalogExceptionKinds.UnmatchedPos, "inv:3001", "POSitive item 3001 (SH-1) is a matrix item. The connector links …", posRef: "3001");
        await RaiseAsync(CatalogExceptionKinds.UnmatchedPos, "inv:3002", "x", """{"invNo":3002,"sku":"SH-2","name":"Shirt","group":"matrix"}""", "3002");

        var review = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/review"));

        var items = review.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("externalRef").GetString()!);
        Assert.Equal("parent:50", items["51"].GetProperty("group").GetString());
        Assert.Equal("parent:50", items["52"].GetProperty("group").GetString()); // from the reason of an older row
        Assert.Equal(("woo", "Mug", "Draft"), (items["60"].GetProperty("group").GetString(), items["60"].GetProperty("name").GetString(),
            items["60"].GetProperty("wooStatus").GetString()));
        Assert.Equal("matrix", items["inv:3001"].GetProperty("group").GetString());
        Assert.Equal("matrix", items["inv:3002"].GetProperty("group").GetString());
        Assert.False(items["inv:3002"].GetProperty("canCreate").GetBoolean()); // the connector does not create matrix items
        Assert.True(items["60"].GetProperty("canLink").GetBoolean());

        var groups = review.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(new[] { "woo", "parent:50", "matrix" }, groups.Select(g => g.GetProperty("key").GetString()).ToArray());
        Assert.Equal(2, groups[1].GetProperty("count").GetInt32());
        var matrix = groups.Single(g => g.GetProperty("key").GetString() == "matrix");
        Assert.Equal(2, matrix.GetProperty("count").GetInt32());
        Assert.Contains("does not create variable products", matrix.GetProperty("explanation").GetString());
        Assert.Contains("Normal, not an error", groups[0].GetProperty("explanation").GetString());
    }

    // F2: bulk Ignore / Un-ignore (review kinds only).
    [Fact]
    public async Task F2_BulkIgnore_ClosesTheGroup_BulkUnignore_ReopensItForTheNextMatch()
    {
        await RaiseAsync(CatalogExceptionKinds.UnmatchedPlatform, "51", "a");
        await RaiseAsync(CatalogExceptionKinds.UnmatchedPlatform, "52", "b");
        await RaiseAsync(CatalogExceptionKinds.LinkConflict, "inv:2002", "c");
        await RaiseAsync(OrderExceptionKinds.Rejected, "59", "d");

        var ignore = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/review/ignore-many", new
        {
            items = new[]
            {
                new { externalRef = "51", kind = CatalogExceptionKinds.UnmatchedPlatform },
                new { externalRef = "52", kind = CatalogExceptionKinds.UnmatchedPlatform },
                new { externalRef = "inv:2002", kind = CatalogExceptionKinds.LinkConflict },
            },
        });
        var notReview = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/review/ignore-many",
            new { items = new[] { new { externalRef = "59", kind = OrderExceptionKinds.Rejected } } });
        var review = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/review"));

        Assert.Equal(HttpStatusCode.OK, ignore.StatusCode);
        Assert.Equal((3, 3), ((await Json(ignore)).GetProperty("ignored").GetInt32(), (await Json(ignore)).GetProperty("closed").GetInt32()));
        Assert.Equal(HttpStatusCode.BadRequest, notReview.StatusCode);
        Assert.Empty(review.GetProperty("items").EnumerateArray());
        Assert.Equal(3, review.GetProperty("ignored").GetArrayLength());
        Assert.Equal(1, await Stores.Exceptions.CountOpenAsync("shop1")); // the order problem stays open

        var unignore = await Json(await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/review/unignore-many",
            new { externalRefs = new[] { "51", "inv:2002", "nope" } }));
        Assert.Equal(2, unignore.GetProperty("unignored").GetInt32());
        Assert.Equal(new[] { "52" }, (await UiStore.ListIgnoredAsync("shop1")).Select(i => i.ExternalRef).ToArray());
    }

    // F3: one hung single-item read does not sink the search; it comes back as an "unavailable" row.
    [Fact]
    public async Task F3_Search_ReturnsWhatItCould_WhenOneItemReadHangs()
    {
        _secrets.Set(ConnectorSecretNames.PosApiKey("shop1"), "key");
        var hung = _pos.Add(900248, "HX", 10m, 5m);
        hung.Description = "Quarter tap";
        hung.Web = false; // found only by its SKU, whose item read hangs
        _pos.Add(900249, "HX-2", 10m, 5m).Description = "HX spare washer";
        _pos.HangInvNos.Add(900248);

        var started = DateTime.UtcNow;
        var response = await _client.GetAsync("/connectors/api/connectors/shop1/pos-products?q=HX");
        var elapsed = DateTime.UtcNow - started;
        var body = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"took {elapsed}");
        // The web-item search still answers (its cached list is not blocked by the hung read).
        Assert.Equal(new[] { 900249 }, body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("invNo").GetInt32()).ToArray());
        var unavailable = Assert.Single(body.GetProperty("unavailable").EnumerateArray());
        Assert.Equal(900248, unavailable.GetProperty("invNo").GetInt32());
        Assert.Contains("Item 900248 couldn't be loaded right now", unavailable.GetProperty("message").GetString());
    }

    [Fact]
    public async Task F3_Search_OnlyFailsWhenNothingCouldBeRead()
    {
        _secrets.Set(ConnectorSecretNames.PosApiKey("shop1"), "key");
        _pos.Add(900248, "HX", 10m, 5m).Web = false; // not in the web list, and its read hangs
        _pos.HangInvNos.Add(900248);

        var byNumber = await _client.GetAsync("/connectors/api/connectors/shop1/pos-products?q=900248");

        // The SKU lookup and the web list answered (nothing), so it is "nothing found" plus the row — not a 502.
        Assert.Equal(HttpStatusCode.OK, byNumber.StatusCode);
        var body = await Json(byNumber);
        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.Equal(900248, Assert.Single(body.GetProperty("unavailable").EnumerateArray()).GetProperty("invNo").GetInt32());
    }

    // F7: webProduct null stays null (unknown, no tag); a web-list hit is a web item; an explicit false stays false.
    [Fact]
    public async Task F7_Search_KeepsWebProductUnknown_AsNull()
    {
        _secrets.Set(ConnectorSecretNames.PosApiKey("shop1"), "key");
        var unknown = _pos.Add(4001, "ZZ-1", 1m, 1m);
        unknown.WebUnknown = true;
        unknown.Web = false; // not in the web list either: found by its number only
        var notWeb = _pos.Add(4002, "ZZ-2", 1m, 1m);
        notWeb.Web = false;
        _pos.Add(4003, "ZZ-3", 1m, 1m).WebUnknown = true; // in the web list, flag left out

        var a = (await Json(await _client.GetAsync("/connectors/api/connectors/shop1/pos-products?q=4001"))).GetProperty("items")[0];
        var b = (await Json(await _client.GetAsync("/connectors/api/connectors/shop1/pos-products?q=4002"))).GetProperty("items")[0];
        var c = (await Json(await _client.GetAsync("/connectors/api/connectors/shop1/pos-products?q=ZZ-3"))).GetProperty("items")[0];

        Assert.Equal(JsonValueKind.Null, a.GetProperty("webProduct").ValueKind);
        Assert.False(b.GetProperty("webProduct").GetBoolean());
        Assert.True(c.GetProperty("webProduct").GetBoolean());
    }

    // F4: the clash is refused at once, in words, naming the Woo item in the way — and Replace queues the unlink + link.
    [Fact]
    public async Task F4_Link_ToAnItemLinkedElsewhere_Is409InWords_AndReplaceQueuesTheUnlinkAndLink()
    {
        _secrets.Set(ConnectorSecretNames.PosApiKey("shop1"), "key");
        await Stores.Items.UpsertAsync(new ConnectorItem("shop1", 4198, 58, 0, CatalogItemTypes.Simple, "WID-011", null, null));
        await Stores.Items.UpsertAsync(new ConnectorItem("shop1", 3300, 70, 0, CatalogItemTypes.Simple, "OTHER", null, null));
        _wooItems.Items[58] = new WooItemSummary(58, "Widget Pro", "draft", "simple", 0);
        _pos.Add(4198, "WID-011", 1m, 1m).Description = "Widget pro";

        var refused = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/review/link", new { externalId = 96, invNo = 4198 });
        var wooTaken = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/review/link", new { externalId = 70, invNo = 3199 });
        var search = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/pos-products?q=4198"));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await Json(refused);
        Assert.Equal("link_conflict", body.GetProperty("errorCode").GetString());
        Assert.Equal("WID-011 is already linked to Woo product #58 (Draft, 'Widget Pro'). Link a different POSitive item, or unlink #58 first.",
            body.GetProperty("detail").GetString());
        var conflict = body.GetProperty("conflict");
        Assert.Equal(("pos", 58L), (conflict.GetProperty("side").GetString(), conflict.GetProperty("linkedWooId").GetInt64()));
        Assert.Equal(HttpStatusCode.Conflict, wooTaken.StatusCode);
        Assert.Contains("already linked to POSitive item 3300", (await Json(wooTaken)).GetProperty("detail").GetString());
        Assert.Empty(await UiStore.ListJobsAsync("shop1"));
        // The picker marks it: linked to Woo #58.
        Assert.Equal(58, search.GetProperty("items")[0].GetProperty("linkedWooId").GetInt64());

        var replaced = await _client.PostAsJsonAsync("/connectors/api/connectors/shop1/review/link", new { externalId = 96, invNo = 4198, replace = true });

        Assert.Equal(HttpStatusCode.Accepted, replaced.StatusCode);
        var job = Assert.Single(await UiStore.ListJobsAsync("shop1"));
        Assert.Equal(new CatalogLinkPayload(4198, 96, null, ReplaceExisting: true), ReviewJobKinds.Read<CatalogLinkPayload>(job.Payload));
        Assert.Contains(_log.Audit(), a => a.Contains("link refused") && a.Contains("#58"));
        Assert.Contains(_log.Audit(), a => a.Contains("unlinking what either is linked to now"));
    }

    // F4: a refused hand link (from the job) is an exception whose Retry re-queues that link.
    [Fact]
    public async Task F4_ARefusedHandLink_IsAnException_AndRetryQueuesTheSameLink()
    {
        var id = await RaiseAsync(CatalogExceptionKinds.ManualLinkRefused, "96", "refused", posRef: "4198");

        var retry = await _client.PostAsync($"/connectors/api/connectors/shop1/exceptions/{id}/retry", null);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var job = Assert.Single(await UiStore.ListJobsAsync("shop1"));
        Assert.Equal((ReviewJobKinds.CatalogLink, "woo:96"), (job.Kind, job.ResourceKey));
        Assert.Equal(new CatalogLinkPayload(4198, 96), ReviewJobKinds.Read<CatalogLinkPayload>(job.Payload));
    }

    // F6: a closed exception carries its outcome for the page to lead with.
    [Fact]
    public async Task F6_AResolvedException_CarriesItsOutcome()
    {
        var id = await RaiseAsync(OrderExceptionKinds.UnmatchedProduct, "59", "line 14 has no POSitive item");
        await Stores.Exceptions.CloseAsync(id, ConnectorExceptionStatuses.Resolved, "connector: imported on retry",
            "Imported as POSitive order 50001919 on retry; line 14 'Demo Widget' linked to POSitive item 900996.");

        var detail = await Json(await _client.GetAsync($"/connectors/api/connectors/shop1/exceptions/{id}"));
        var closed = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/exceptions?status=closed"));

        var x = detail.GetProperty("exception");
        Assert.Equal("resolved", x.GetProperty("status").GetString());
        Assert.StartsWith("Imported as POSitive order 50001919", x.GetProperty("resolution").GetString());
        Assert.Equal("line 14 has no POSitive item", x.GetProperty("reason").GetString()); // the original problem is kept
        Assert.StartsWith("Imported as", Assert.Single(closed.GetProperty("items").EnumerateArray()).GetProperty("resolution").GetString());
    }

    // F1: the "next steps" panel knows whether the first match ran, is running, or what it found.
    [Fact]
    public async Task F1_Status_ReportsMatchProgress_ForTheNextStepsPanel()
    {
        Assert.False((await MatchAsync()).GetProperty("done").GetBoolean());

        // Enabled, no change-feed cursor yet: the waiting catalog.push IS the first match.
        var push = await Stores.Jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.CatalogPush));
        var running = await MatchAsync();
        Assert.False(running.GetProperty("done").GetBoolean());
        Assert.Equal(push.JobId, running.GetProperty("runningJobId").GetInt64());

        // The first push finished: it recorded the summary like Run the match does.
        await Stores.Cursors.SetAsync("shop1", ConnectorCursorNames.PosModifiedSince, "2026-10-09T01:00:00.0000000Z");
        await Stores.Cursors.SetAsync("shop1", ReviewJobKinds.LastRunCursor,
            """{"jobId":null,"kind":"catalog.push","completedAt":"2026-10-09T01:00:00Z","summary":{"matched":12,"created":3,"unmatchedWoo":24}}""");
        var done = await MatchAsync();
        Assert.True(done.GetProperty("done").GetBoolean());
        Assert.Equal(JsonValueKind.Null, done.GetProperty("runningJobId").ValueKind); // a routine push is not "the match"
        Assert.Equal(24, done.GetProperty("summary").GetProperty("unmatchedWoo").GetInt32());
        Assert.Equal(new DateTime(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc), done.GetProperty("lastRunAt").GetDateTime().ToUniversalTime());
    }

    // --- 2026-10-10 re-test: the wizard rail, the key step's done state, review names -------------

    private static Dictionary<string, (string State, string Note)> Steps(JsonElement view) =>
        view.GetProperty("setup").GetProperty("steps").EnumerateArray()
            .ToDictionary(s => s.GetProperty("key").GetString()!, s => (s.GetProperty("state").GetString()!, s.GetProperty("note").GetString()!));

    private static string? Current(JsonElement view) => view.GetProperty("setup").GetProperty("current").GetString();

    // R1: a new connector is saved at step 1; settings and orders stay "to do" until their own Next saves them.
    [Fact]
    public async Task R1_Setup_ANewConnector_TicksEachStepAsItsNextSavesIt()
    {
        var created = await Json(await _client.PostAsJsonAsync("/connectors/api/connectors",
            new { id = "shop2", storeUrl = "https://shop2.example.com", consumerKey = "ck_live_9f8e7d6c5b4a", consumerSecret = "cs_live_1a2b3c4d5e6f" }));
        var s = Steps(created);
        Assert.Equal(new[] { "store", "settings", "key", "orders", "enable" }, s.Keys.ToArray());
        Assert.Equal(("done", "todo", "todo", "todo", "todo"), (s["store"].State, s["settings"].State, s["key"].State, s["orders"].State, s["enable"].State));
        Assert.Equal("settings", Current(created));
        Assert.Contains("keys stored", s["store"].Note);

        var badStep = await _client.PutAsJsonAsync("/connectors/api/connectors/shop2", new { setupStep = "enable" });
        Assert.Equal(HttpStatusCode.BadRequest, badStep.StatusCode);

        var settings = await Json(await _client.PutAsJsonAsync("/connectors/api/connectors/shop2", new { priceLevel = 3, setupStep = "settings" }));
        Assert.Equal("done", Steps(settings)["settings"].State);
        Assert.Contains("Price level 3", Steps(settings)["settings"].Note);
        Assert.Equal("key", Current(settings));

        _keys.Answers["Kx7pQ2mWz9RtN4vB"] = new PosKeyCheck(true, "WOO-shop2", 1, null);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/connectors/api/connectors/shop2/pos-key", new { apiKey = "Kx7pQ2mWz9RtN4vB" })).StatusCode);
        var keyed = await Json(await _client.GetAsync("/connectors/api/connectors/shop2"));
        Assert.Equal(("done", "Stored for Developer ID WOO-shop2."), Steps(keyed)["key"]);
        Assert.True(keyed.GetProperty("secrets").GetProperty("posApiKey").GetBoolean());
        Assert.DoesNotContain("Kx7pQ2mWz9RtN4vB", keyed.ToString());
        Assert.Equal("orders", Current(keyed));

        // A plain edit (no setupStep) does not tick the orders step.
        var plain = await Json(await _client.PutAsJsonAsync("/connectors/api/connectors/shop2", new { webhookBaseUrl = "" }));
        Assert.Equal("todo", Steps(plain)["orders"].State);
        var orders = await Json(await _client.PutAsJsonAsync("/connectors/api/connectors/shop2", new { webhookBaseUrl = "", setupStep = "orders" }));
        Assert.Equal("done", Steps(orders)["orders"].State);
        Assert.Contains("Polling only", Steps(orders)["orders"].Note);
        Assert.Equal("enable", Current(orders));

        var enabled = await Json(await _client.PostAsync("/connectors/api/connectors/shop2/enable", null));
        Assert.All(Steps(enabled).Values, v => Assert.Equal("done", v.State));
        Assert.Equal(JsonValueKind.Null, enabled.GetProperty("setup").GetProperty("current").ValueKind);
        Assert.Contains(_log.Audit(), a => a.Contains("wizard step 'settings' saved"));
    }

    // R1: a connector made before the rail has nothing pending; what goes missing on an enabled one needs attention.
    [Fact]
    public async Task R1_Setup_AnOlderConnector_ShowsDoneOrNeedsAttention_NotToDo()
    {
        var bare = await Json(await _client.GetAsync("/connectors/api/connectors/shop1"));
        Assert.Equal(("todo", "done", "todo", "done", "todo"),
            (Steps(bare)["store"].State, Steps(bare)["settings"].State, Steps(bare)["key"].State, Steps(bare)["orders"].State, Steps(bare)["enable"].State));
        Assert.Equal("store", Current(bare));

        _secrets.Set(ConnectorSecretNames.WooConsumerKey("shop1"), "ck");
        _secrets.Set(ConnectorSecretNames.WooConsumerSecret("shop1"), "cs");
        _secrets.Set(ConnectorSecretNames.PosApiKey("shop1"), "key");
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/connectors/api/connectors/shop1/enable", null)).StatusCode);
        var running = await Json(await _client.GetAsync("/connectors/api/connectors/shop1"));
        Assert.All(Steps(running).Values, v => Assert.Equal("done", v.State));

        _secrets.Remove(ConnectorSecretNames.PosApiKey("shop1"));
        var broken = await Json(await _client.GetAsync("/connectors/api/connectors/shop1"));
        Assert.Equal("attention", Steps(broken)["key"].State);
        Assert.Equal("attention", Steps(broken)["enable"].State);
        Assert.Contains("POSitive API key", Steps(broken)["enable"].Note);
        Assert.Equal("key", Current(broken));
    }

    // R3: rows raised before the snapshot (no parent, no name — the manual test's "Woo #24 ×2") are named and grouped from Woo,
    // in one batched lookup that the next page load does not repeat.
    [Fact]
    public async Task R3_Review_NamesAndGroupsOlderRowsFromWoo_InOneCachedLookup()
    {
        _wooItems.Items[10] = new WooItemSummary(10, "V-Neck T-Shirt", "publish", "variable", 0, "woo-vneck-tee", null, 3);
        _wooItems.Items[24] = new WooItemSummary(24, "V-Neck T-Shirt - Red", "publish", "variation", 10, "woo-vneck-tee-red", "Red");
        _wooItems.Items[25] = new WooItemSummary(25, "V-Neck T-Shirt - Green", "publish", "variation", 10, "woo-vneck-tee-green", "Green");
        _wooItems.Items[26] = new WooItemSummary(26, "V-Neck T-Shirt - Blue", "publish", "variation", 10, "woo-vneck-tee-blue", "Blue");
        _wooItems.Items[11] = new WooItemSummary(11, "Hoodie", "publish", "variable", 0, "woo-hoodie", null, 4);
        _wooItems.Items[23] = new WooItemSummary(23, "Single", "draft", "simple", 0, "woo-single");
        foreach (var (id, sku) in new[] { ("24", "woo-vneck-tee-red"), ("25", "woo-vneck-tee-green"), ("26", "woo-vneck-tee-blue"), ("23", "woo-single") })
        {
            await RaiseAsync(CatalogExceptionKinds.UnmatchedPlatform, id, $"No POSitive web item has SKU or barcode '{sku}'; it stays unmanaged.");
            await RaiseAsync(CatalogExceptionKinds.UnmatchedPlatform, id, $"No POSitive web item has SKU or barcode '{sku}'; it stays unmanaged.");
        }
        // A newer row: its snapshot has the parent and the label; Woo is asked for the parent's name only.
        await RaiseAsync(CatalogExceptionKinds.UnmatchedPlatform, "27", "No POSitive web item has SKU or barcode 'woo-hoodie-red'; it stays unmanaged.",
            """{"wooId":27,"parentId":11,"type":"variation","name":"Red, No","sku":"woo-hoodie-red","status":"publish"}""");
        await RaiseAsync(CatalogExceptionKinds.UnmatchedPos, "inv:4019", "POSitive item 4019 (MX16) is a matrix item. The connector links …", posRef: "4019");

        var review = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/review"));
        var again = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/review"));

        var items = review.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("externalRef").GetString()!);
        Assert.Equal(("parent:10", "Red", "woo-vneck-tee-red", 10L),
            (items["24"].GetProperty("group").GetString(), items["24"].GetProperty("label").GetString(), items["24"].GetProperty("sku").GetString(),
                items["24"].GetProperty("parentId").GetInt64()));
        Assert.Equal("Blue", items["26"].GetProperty("label").GetString());
        Assert.Equal(("woo", "Single", "woo-single", "Draft"),
            (items["23"].GetProperty("group").GetString(), items["23"].GetProperty("label").GetString(), items["23"].GetProperty("sku").GetString(),
                items["23"].GetProperty("wooStatus").GetString()));
        Assert.Equal(("parent:11", "Red, No"), (items["27"].GetProperty("group").GetString(), items["27"].GetProperty("label").GetString()));
        Assert.Equal("MX16", items["inv:4019"].GetProperty("sku").GetString()); // from "POSitive item 4019 (MX16)"
        // Every Woo row has a name and a SKU: no bare "Woo #24".
        Assert.All(items.Values.Where(i => i.GetProperty("wooId").ValueKind == JsonValueKind.Number), i =>
        {
            Assert.False(string.IsNullOrEmpty(i.GetProperty("label").GetString()));
            Assert.False(string.IsNullOrEmpty(i.GetProperty("sku").GetString()));
        });

        var groups = review.GetProperty("groups").EnumerateArray().ToDictionary(g => g.GetProperty("key").GetString()!);
        Assert.Equal("V-Neck T-Shirt (Woo #10, variable, 3 variations, none matched)", groups["parent:10"].GetProperty("title").GetString());
        Assert.Equal(("V-Neck T-Shirt", 3, 3), (groups["parent:10"].GetProperty("parentName").GetString(),
            groups["parent:10"].GetProperty("variationCount").GetInt32(), groups["parent:10"].GetProperty("count").GetInt32()));
        Assert.Equal("Hoodie (Woo #11, variable, 4 variations, 1 to review)", groups["parent:11"].GetProperty("title").GetString());
        Assert.Equal(1, groups["woo"].GetProperty("count").GetInt32());

        // One batched lookup (the SKUs of the rows without a snapshot, the ids, the parents); the second load used the cache.
        var find = Assert.Single(_wooItems.Finds);
        Assert.Contains("woo-vneck-tee-red", find.Skus);
        Assert.Contains(11L, find.Ids);
        Assert.Equal(review.GetProperty("groups").ToString(), again.GetProperty("groups").ToString());
        Assert.Equal(1, _app.Services.GetRequiredService<ConnectorUi>().ReviewNames.Lookups);
    }

    // R3: what Woo does not answer keeps what the row had (grouped from the reason, titled by the parent id).
    [Fact]
    public async Task R3_Review_WhenWooDoesNotAnswer_RowsKeepWhatTheyHad()
    {
        await RaiseAsync(CatalogExceptionKinds.UnmatchedPlatform, "52", "Woo variation #52 of #50 has no SKU, so it cannot be matched; it stays unmanaged.");
        await RaiseAsync(CatalogExceptionKinds.UnmatchedPlatform, "24", "No POSitive web item has SKU or barcode 'woo-vneck-tee-red'; it stays unmanaged.");

        var review = await Json(await _client.GetAsync("/connectors/api/connectors/shop1/review"));

        var items = review.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("externalRef").GetString()!);
        Assert.Equal("parent:50", items["52"].GetProperty("group").GetString());
        Assert.Equal(("woo", "woo-vneck-tee-red"), (items["24"].GetProperty("group").GetString(), items["24"].GetProperty("sku").GetString()));
        Assert.Equal("Variations of Woo product #50",
            review.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("key").GetString() == "parent:50").GetProperty("title").GetString());
    }

    private async Task<JsonElement> MatchAsync() =>
        (await Json(await _client.GetAsync("/connectors/api/connectors/shop1/status"))).GetProperty("connector").GetProperty("status").GetProperty("match");
}
