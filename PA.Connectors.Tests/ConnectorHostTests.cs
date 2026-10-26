using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PA.Connectors.Core;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Secrets;
using PA.Connectors.Core.Stores;
using PA.Connectors.Host;
using Xunit;

namespace PA.Tests.Connectors;

/// <summary>
/// The connector add-on's host (PA.Connectors.Host) in-process on a TestServer,
/// with every machine path redirected to a temp folder: GET /health and the
/// WooCommerce webhook receiver (verify → enqueue, never inline).
/// </summary>
public sealed class ConnectorHostTests : IAsyncLifetime
{
    // Recorded on the dev store (spike S0bc): order.updated delivery for order #59.
    private const string WebhookSecret = "spike-secret";
    private const string RecordedSignature = "u3P1bMNr+rOo1hrrbVK7z7HUBmro50GMSjljf7PMQnw=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pa-connector-host-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySecrets _secrets = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private string ConfigPath => Path.Combine(_dir, "connectors.json");

    private sealed class MemorySecrets : IConnectorSecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string name) => _values.GetValueOrDefault(name);
        public void Set(string name, string value) => _values[name] = value;
        public bool Remove(string name) => _values.Remove(name);
        public IReadOnlyList<string> List() => _values.Keys.ToList();
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        ConnectorConfigFile.Save(ConfigPath, new ConnectorsDocument
        {
            Connectors =
            [
                // Disabled: the worker leaves it alone, but webhooks are still accepted and queued.
                new ConnectorSettings { Id = "shop1", StoreUrl = "https://shop.example.com", Enabled = false },
            ],
        });
        _secrets.Set(ConnectorSecretNames.WooWebhookSecret("shop1"), WebhookSecret);

        _app = await ConnectorHostApp.BuildAsync(
            [
                $"--Connectors:ConfigPath={ConfigPath}",
                $"--Connectors:DatabasePath={Path.Combine(_dir, "connectors.db")}",
                $"--Connectors:SecretsPath={Path.Combine(_dir, "secrets.json")}",
                $"--Logging:Directory={Path.Combine(_dir, "logs")}",
            ],
            builder =>
            {
                builder.WebHost.UseTestServer();
                builder.Services.AddSingleton<IConnectorSecretStore>(_secrets);
            });
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        TestDirectory.Delete(_dir);
    }

    private IConnectorJobStore Jobs => _app.Services.GetRequiredService<IConnectorJobStore>();

    private static HttpRequestMessage Delivery(string connectorId, byte[] body, string? signature = RecordedSignature,
        string topic = "order.updated")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/hooks/woocommerce/{connectorId}")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };
        if (signature is not null)
            request.Headers.Add("X-WC-Webhook-Signature", signature);
        request.Headers.Add("X-WC-Webhook-Topic", topic);
        request.Headers.Add("X-WC-Webhook-Delivery-ID", "d-1");
        return request;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    // --- webhooks ------------------------------------------------------------

    [Fact]
    public async Task SignedOrderDelivery_EnqueuesOneOrderImport_KeyedByOrderId_AndCoalescesRedeliveries()
    {
        var body = WooFixtures.Bytes("webhook-order-updated.body.json");

        var first = await _client.SendAsync(Delivery("shop1", body));
        var second = await _client.SendAsync(Delivery("shop1", body));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var answer = await Json(first);
        Assert.True(answer.GetProperty("queued").GetBoolean());
        Assert.False(answer.GetProperty("coalesced").GetBoolean());
        Assert.True((await Json(second)).GetProperty("coalesced").GetBoolean());

        var job = await Jobs.ClaimNextAsync("shop1", TimeSpan.FromMinutes(1));
        Assert.Equal(ConnectorJobKinds.OrderImport, job!.Kind);
        Assert.Equal("59", job.ResourceKey);
        Assert.Contains("\"topic\":\"order.updated\"", job.Payload);
        Assert.Contains("\"deliveryId\":\"d-1\"", job.Payload);
        Assert.Null(await Jobs.ClaimNextAsync("shop1", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task BadOrMissingSignature_Is401_AndQueuesNothing()
    {
        var body = WooFixtures.Bytes("webhook-order-updated.body.json");
        var tampered = (byte[])body.Clone();
        tampered[^2] ^= 0x01;

        var wrong = await _client.SendAsync(Delivery("shop1", tampered));
        var missing = await _client.SendAsync(Delivery("shop1", body, signature: null));
        _secrets.Remove(ConnectorSecretNames.WooWebhookSecret("shop1"));
        var noSecret = await _client.SendAsync(Delivery("shop1", body));

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_signature", (await Json(wrong)).GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noSecret.StatusCode);
        Assert.Equal(0, await Jobs.CountOpenAsync("shop1"));
    }

    [Fact]
    public async Task UnsignedCreationPing_Is200_AndQueuesNothing()
    {
        var ping = new HttpRequestMessage(HttpMethod.Post, "/hooks/woocommerce/shop1")
        {
            Content = new StringContent("webhook_id=2", Encoding.ASCII, "application/x-www-form-urlencoded"),
        };

        var response = await _client.SendAsync(ping);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("ping").GetBoolean());
        Assert.Equal(0, await Jobs.CountOpenAsync("shop1"));
    }

    [Fact]
    public async Task UnknownConnector_Is401_LikeABadSignature_NonOrderTopic_IsIgnored()
    {
        var body = WooFixtures.Bytes("webhook-order-updated.body.json");

        var unknown = await _client.SendAsync(Delivery("shop9", body));
        var badSignature = await _client.SendAsync(Delivery("shop1", body, signature: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="));
        var product = await _client.SendAsync(Delivery("shop1", body, topic: "product.updated"));

        // L4: an unknown id and a bad signature are indistinguishable (same status, same body).
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, badSignature.StatusCode);
        Assert.Equal(await badSignature.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal("invalid_signature", (await Json(unknown)).GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.OK, product.StatusCode);
        Assert.True((await Json(product)).GetProperty("ignored").GetBoolean());
        Assert.Equal(0, await Jobs.CountOpenAsync("shop1"));
    }

    [Fact]
    public async Task OversizedBody_Is413()
    {
        var response = await _client.SendAsync(Delivery("shop1", new byte[WebhookEndpoints.MaxBodyBytes + 1]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public void ReadsItsOwnSettingsFile_NeverTheServersAppsettings()
    {
        // In the monorepo PA.Server's appsettings.json sits in this test output folder too (as it
        // may beside an install; ConnectorServerBoundaryTests checks it is there); the host must
        // read connectorhost.json only.
        Assert.Equal(ConnectorHostApp.DefaultUrl, _app.Configuration["Urls"]);
        Assert.Null(_app.Configuration["ConnectionStrings:Positive"]);
        Assert.Equal("25", _app.Configuration["Connectors:MaxJobsPerTick"]);
    }

    // --- health -------------------------------------------------------------

    [Fact]
    public async Task Health_ReportsVersionSchemaWorkerAndPerConnectorCounts()
    {
        await Jobs.EnqueueAsync(new JobRequest("shop1", ConnectorJobKinds.OrderImport, "58"));
        await _app.Services.GetRequiredService<IConnectorExceptionStore>().RaiseAsync(
            new ConnectorExceptionInput("shop1", ConnectorExceptionKinds.UnmatchedProduct, "order:58", "SKU x matches no item"));

        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var health = await Json(response);
        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.Equal(ConnectorHostApp.ServiceName, health.GetProperty("service").GetString());
        Assert.False(string.IsNullOrWhiteSpace(health.GetProperty("version").GetString()));
        Assert.Equal(ConnectorDatabase.LatestVersion, health.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, health.GetProperty("queueDepth").GetInt32());
        Assert.Equal(1, health.GetProperty("openExceptions").GetInt32());
        var connector = Assert.Single(health.GetProperty("connectors").EnumerateArray());
        Assert.Equal("shop1", connector.GetProperty("id").GetString());
        Assert.False(connector.GetProperty("enabled").GetBoolean());
        Assert.False(connector.GetProperty("running").GetBoolean());
        Assert.Equal(1, connector.GetProperty("queueDepth").GetInt32());
        Assert.Equal(JsonValueKind.Null, connector.GetProperty("lastImportAt").ValueKind);
    }

    [Fact]
    public async Task Health_UnreadableConfig_IsDegraded_NotAnError()
    {
        await File.WriteAllTextAsync(ConfigPath, "{ not json");

        var health = await Json(await _client.GetAsync("/health"));

        Assert.Equal("degraded", health.GetProperty("status").GetString());
        Assert.Contains("could not be read", health.GetProperty("configError").GetString());
        Assert.DoesNotContain(ConfigPath, health.GetProperty("configError").GetString());
        Assert.False(health.TryGetProperty("configPath", out _));
    }
}

/// <summary>The host listens on loopback only — anything else refuses to start.</summary>
public class ConnectorHostLoopbackTests
{
    [Theory]
    [InlineData("http://localhost:5220")]
    [InlineData("http://127.0.0.1:5220")]
    [InlineData("http://[::1]:5220")]
    [InlineData("http://localhost:5220;https://127.0.0.1:5221")]
    public void LoopbackUrls_AreAccepted(string urls)
    {
        Assert.Equal(urls, ConnectorHostApp.RequireLoopback(urls));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5220")]
    [InlineData("http://*:5220")]
    [InlineData("http://+:5220")]
    [InlineData("http://shop.example.com:5220")]
    [InlineData("http://192.168.1.10:5220")]
    [InlineData("http://localhost:5220;http://0.0.0.0:5221")]
    public void AnythingElse_IsRefused(string urls)
    {
        Assert.Throws<InvalidOperationException>(() => ConnectorHostApp.RequireLoopback(urls));
    }

    [Fact]
    public async Task Build_WithAPublicUrl_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConnectorHostApp.BuildAsync(["--Urls=http://0.0.0.0:5220"]));
    }
}
