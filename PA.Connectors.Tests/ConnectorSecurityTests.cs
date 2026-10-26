using System.Net;
using System.Net.Sockets;
using System.Text;
using PA.Connectors.Host;
using PA.Connectors.WooCommerce;
using Xunit;

namespace PA.Tests.Connectors;

/// <summary>
/// Security review of the connector code (connector UI): the store-address guard (M2, also at connect time
/// against DNS rebinding, no redirects), Woo error bodies kept out of messages (M2), and the forward
/// token both services share (M3) — made once, ACL-restricted, replaced when not trusted, compared
/// in constant time.
/// </summary>
public class ConnectorSecurityTests
{
    // --- M2: which store addresses may be called ---------------------------------------------------

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.8.9.10")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.10")]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("100.64.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    public void PrivateLoopbackAndLinkLocalAddresses_AreBlocked(string address)
    {
        Assert.True(WooStoreAddress.IsBlocked(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("203.0.113.10")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("2001:4860:4860::8888")]
    public void PublicAddresses_AreAllowed(string address)
    {
        Assert.False(WooStoreAddress.IsBlocked(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task Check_ResolvesDns_AndRefusesUserInfo_EvenWithTheDevOverride()
    {
        var strict = new WooStoreAddress(false, (host, _) => Task.FromResult(new[] { IPAddress.Parse(host == "shop.test" ? "203.0.113.5" : "192.168.1.5") }));
        var dev = new WooStoreAddress(true, (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));

        Assert.Null(await strict.CheckAsync(new Uri("https://shop.test"), CancellationToken.None));
        Assert.NotNull(await strict.CheckAsync(new Uri("https://lan.test"), CancellationToken.None));
        Assert.NotNull(await strict.CheckAsync(new Uri("https://u:p@shop.test"), CancellationToken.None));
        Assert.Null(await dev.CheckAsync(new Uri("http://localhost:8090"), CancellationToken.None));
        Assert.Null(await dev.CheckAsync(new Uri("http://host.docker.internal:8090"), CancellationToken.None));
        Assert.NotNull(await dev.CheckAsync(new Uri("http://u:p@localhost:8090"), CancellationToken.None));
    }

    [Fact]
    public async Task EveryNewConnection_ReResolves_SoDnsRebindingToAPrivateAddressIsRefused()
    {
        // The name passed the save-time check, then answers 127.0.0.1 when the connection is made.
        var resolver = (string _, CancellationToken _) => Task.FromResult(new[] { IPAddress.Loopback });
        using var client = new HttpClient(new WooStoreAddress(false, resolver).CreateHandler());

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://rebound.test:9/"));

        var causes = new List<Exception>();
        for (Exception? e = error; e is not null; e = e.InnerException)
            causes.Add(e);
        Assert.Contains(causes, e => e is StoreAddressBlockedException);
    }

    [Fact]
    public async Task TheWooHandler_NeverFollowsARedirect_AndTheDevOverrideReachesALocalStore()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            var buffer = new byte[4096];
            await socket.ReceiveAsync(buffer);
            await socket.SendAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/latest/meta-data\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        });
        var handler = new WooStoreAddress(true, (_, _) => Task.FromResult(new[] { IPAddress.Loopback })).CreateHandler();
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync($"http://dev-store.test:{port}/wp-json/wc/v3/products");
        await serve;

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public async Task AWooErrorThatIsNotWooJson_NeverPutsTheBodyInTheMessage()
    {
        var html = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html><body>internal proxy 10.0.0.9 secret-token-xyz</body></html>", Encoding.UTF8, "text/html"),
        };
        var woo = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"code\":\"woocommerce_rest_invalid\",\"message\":\"" + new string('x', 500) + "\\n<script>\"}", Encoding.UTF8, "application/json"),
        };
        var answers = new Queue<HttpResponseMessage>([html, woo]);
        var client = new WooClient(new HttpClient(new Answering(answers)),
            new WooClientOptions { StoreUrl = new Uri("https://shop.test"), ConsumerKey = "ck", ConsumerSecret = "cs", MaxRetries = 0 });

        var first = await Assert.ThrowsAsync<WooApiException>(() => client.TestConnectionAsync());
        var second = await Assert.ThrowsAsync<WooApiException>(() => client.TestConnectionAsync());

        Assert.DoesNotContain("secret-token", first.Message);
        Assert.DoesNotContain("10.0.0.9", first.Message);
        Assert.Null(first.WooMessage);
        Assert.Equal(HttpStatusCode.BadGateway, first.Status);
        Assert.Equal("woocommerce_rest_invalid", second.Code);
        Assert.True(second.WooMessage!.Length <= WooClient.MaxErrorMessageLength + 1);
        Assert.DoesNotContain('\n', second.WooMessage);
    }

    [Theory]
    [InlineData("woocommerce_rest_cannot_view", "woocommerce_rest_cannot_view")]
    [InlineData("<b>x</b>", null)]
    [InlineData("", null)]
    public void WooErrorCodes_AreIdentifiersOrNothing(string code, string? expected)
    {
        Assert.Equal(expected, WooClient.CleanCode(code));
    }

    private sealed class Answering(Queue<HttpResponseMessage> answers) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answers.Dequeue());
    }

    // --- M3: the forward token ---------------------------------------------------------------------

    private static string TempTokenPath() =>
        Path.Combine(Path.GetTempPath(), "pa-forward-token-" + Guid.NewGuid().ToString("N"), "connectors", "forward.token");

    // TheToken_IsMadeOnce_AndBothServicesReadTheSameOne needs PA.Server's half of the token: it
    // lives in ConnectorServerBoundaryTests, which the public connector source export leaves out.

    [Fact]
    public void TheTokenFile_IsReadableBySystemAdministratorsAndItsMakerOnly()
    {
        if (OperatingSystem.IsWindows())
            AssertTokenFileAcl();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertTokenFileAcl()
    {
        var path = TempTokenPath();
        try
        {
            ConnectorForwardToken.LoadOrCreate(path);

            var security = new FileInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected); // nothing inherited from the folder
            var allowed = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<System.Security.AccessControl.FileSystemAccessRule>()
                .Select(r => (System.Security.Principal.SecurityIdentifier)r.IdentityReference).ToHashSet();
            var expected = new HashSet<System.Security.Principal.SecurityIdentifier>
            {
                new(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                new(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            };
            Assert.True(allowed.SetEquals(expected), string.Join(", ", allowed));
            Assert.True(ConnectorForwardToken.IsTrusted(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path))!, recursive: true);
        }
    }

    [Fact]
    public void ATokenFileOthersCanRead_OrAMalformedOne_IsReplaced()
    {
        var path = TempTokenPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "short");
            var replaced = ConnectorForwardToken.LoadOrCreate(path);
            Assert.NotEqual("short", replaced);
            Assert.True(ConnectorForwardToken.IsWellFormed(replaced));

            if (OperatingSystem.IsWindows())
            {
                // A planted file everyone can read (e.g. made by a local user before the services ran).
                var planted = new string('a', 43);
                File.Delete(path);
                File.WriteAllText(path, planted);
                var security = new FileInfo(path).GetAccessControl();
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                    System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(security);
                Assert.False(ConnectorForwardToken.IsTrusted(path));

                var fresh = ConnectorForwardToken.LoadOrCreate(path);

                Assert.NotEqual(planted, fresh);
                Assert.True(ConnectorForwardToken.IsTrusted(path));
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path))!, recursive: true);
        }
    }

    [Fact]
    public void AnUnreadableTokenLocation_IsNoToken_NotAnException()
    {
        // A file where the folder should be: the token cannot be made, so forwards stay anonymous.
        var root = Path.Combine(Path.GetTempPath(), "pa-forward-token-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(root, "not a folder");
        try
        {
            var token = new ConnectorForwardToken(Path.Combine(root, "forward.token"));

            Assert.Null(token.Current());
            Assert.False(token.Matches("anything-at-all-anything-at-all-xx"));
        }
        finally
        {
            File.Delete(root);
        }
    }
}
