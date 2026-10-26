using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
using PA.Connectors.WooCommerce;
using Xunit;

namespace PA.Tests.Connectors.Orders;

/// <summary>
/// The host's WooCommerce receiver (design §4.1) for the S3 topics: only
/// order.created / order.updated queue an import; everything else is a quiet
/// 200; the signature is checked over the raw bytes before anything is parsed.
/// (ConnectorHostTests covers the recorded delivery, ping, 404 and 413.)
/// </summary>
public sealed class WebhookReceiverTests : IAsyncLifetime
{
    private const string Secret = "receiver-secret";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pa-receiver-" + Guid.NewGuid().ToString("N"));
    private readonly OrderImportHarness.MemorySecrets _secrets = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var config = Path.Combine(_dir, "connectors.json");
        ConnectorConfigFile.Save(config, new ConnectorsDocument
        {
            Connectors = [new ConnectorSettings { Id = "shop1", StoreUrl = "https://shop.example.com", Enabled = false }],
        });
        _secrets.Set(ConnectorSecretNames.WooWebhookSecret("shop1"), Secret);
        _app = await ConnectorHostApp.BuildAsync(
            [
                $"--Connectors:ConfigPath={config}",
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

    private static HttpRequestMessage Signed(string json, string? topic, string secret = Secret)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var request = new HttpRequestMessage(HttpMethod.Post, "/hooks/woocommerce/shop1") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add(WooWebhookSignature.HeaderName, WooWebhookSignature.Compute(body, secret));
        if (topic is not null)
            request.Headers.Add(WebhookEndpoints.TopicHeader, topic);
        return request;
    }

    [Fact]
    public async Task OrderCreatedAndUpdated_ForTheSameOrder_CoalesceIntoOneImport()
    {
        var created = await _client.SendAsync(Signed("""{"id":77,"status":"processing"}""", "order.created"));
        var updated = await _client.SendAsync(Signed("""{"id":77,"status":"completed"}""", "order.updated"));
        var other = await _client.SendAsync(Signed("""{"id":78}""", "ORDER.UPDATED"));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK], new[] { created.StatusCode, updated.StatusCode, other.StatusCode });
        Assert.Equal(2, await Jobs.CountOpenAsync("shop1"));
        var keys = new List<string>();
        while (await Jobs.ClaimNextAsync("shop1", TimeSpan.FromMinutes(1)) is { } job)
        {
            Assert.Equal(ConnectorJobKinds.OrderImport, job.Kind);
            keys.Add(job.ResourceKey);
            await Jobs.CompleteAsync(job.JobId);
        }
        Assert.Equal(["77", "78"], keys.Order());
    }

    [Theory]
    [InlineData("order.deleted")]
    [InlineData("order.restored")]
    [InlineData("product.updated")]
    [InlineData("action.woocommerce_order_refunded")]
    [InlineData(null)]
    public async Task OtherTopics_Are200_AndIgnored(string? topic)
    {
        var response = await _client.SendAsync(Signed("""{"id":77}""", topic));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"ignored\":true", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await Jobs.CountOpenAsync("shop1"));
    }

    [Fact]
    public async Task WrongSecret_Is401_BeforeTheBodyIsParsed()
    {
        var response = await _client.SendAsync(Signed("this is not json", "order.created", secret: "someone-else"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await Jobs.CountOpenAsync("shop1"));
    }

    [Fact]
    public async Task SignedBodyWithoutAnOrderId_Is400()
    {
        var response = await _client.SendAsync(Signed("""{"status":"processing"}""", "order.created"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Jobs.CountOpenAsync("shop1"));
    }

    [Fact]
    public async Task ABodyOfExactlyTheCap_IsRead_OneByteMoreIs413()
    {
        var atCap = "{\"id\":5,\"pad\":\"" + new string('x', WebhookEndpoints.MaxBodyBytes - 17) + "\"}";
        Assert.Equal(WebhookEndpoints.MaxBodyBytes, Encoding.UTF8.GetByteCount(atCap));

        var ok = await _client.SendAsync(Signed(atCap, "order.updated"));
        var over = await _client.SendAsync(Signed(atCap + " ", "order.updated"));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.Equal(1, await Jobs.CountOpenAsync("shop1"));
    }
}
