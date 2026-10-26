using System.Text.Json;
using PA.Connectors.Core;
using PA.Connectors.Core.Secrets;
using Xunit;

namespace PA.Tests.Connectors;

public class ConnectorConfigTests
{
    private static ConnectorSettings Valid(string id = "shop1") => new()
    {
        Id = id,
        Enabled = true,
        StoreUrl = "https://shop.example.com",
        DeveloperId = "WOO-" + id,
    };

    [Fact]
    public void MinimalEntry_GetsTheApprovedDefaults()
    {
        var document = ConnectorConfigFile.Parse("""
            { "connectors": [ { "id": "shop1", "storeUrl": "https://shop.example.com" } ] }
            """);

        var c = Assert.Single(document.Connectors);
        Assert.Equal("shop1", c.Id);
        Assert.Equal(ConnectorPlatforms.WooCommerce, c.Platform);
        Assert.False(c.Enabled);
        Assert.Equal(1, c.PriceLevel);
        Assert.Equal(0m, c.StockBuffer);
        Assert.Equal(PublishStatuses.Draft, c.PublishNewAs);
        Assert.False(c.OverwriteTitleDescription);
        Assert.False(c.ImportOnHold);
        Assert.Equal(60, c.PushIntervalSeconds);
        Assert.Equal(5, c.OrderPollMinutes);
        Assert.Null(c.DeveloperId);
    }

    [Fact]
    public void FullEntry_RoundTrips_CaseInsensitive_WithComments()
    {
        var document = ConnectorConfigFile.Parse("""
            {
              // hand-edited
              "Connectors": [
                { "Id": "shop2", "platform": "woocommerce", "enabled": true, "storeUrl": "https://b.example",
                  "priceLevel": 3, "stockBuffer": 2.5, "publishNewAs": "publish", "overwriteTitleDescription": true,
                  "importOnHold": true, "pushIntervalSeconds": 120, "orderPollMinutes": 10, "developerId": "WOO-shop2", },
              ],
            }
            """);

        var c = Assert.Single(document.Connectors);
        Assert.Equal(3, c.PriceLevel);
        Assert.Equal(2.5m, c.StockBuffer);
        Assert.Equal(PublishStatuses.Publish, c.PublishNewAs);
        Assert.True(c.OverwriteTitleDescription && c.ImportOnHold && c.Enabled);
        Assert.Equal(120, c.PushIntervalSeconds);
        Assert.Equal(10, c.OrderPollMinutes);
        Assert.Empty(ConnectorConfigFile.Validate(c));
    }

    [Fact]
    public void MissingOrBlankFile_IsNoConnectors()
    {
        var dir = Directory.CreateTempSubdirectory("pa-connectors-").FullName;
        try
        {
            var path = Path.Combine(dir, "connectors.json");
            Assert.Empty(ConnectorConfigFile.Load(path).Connectors);
            File.WriteAllText(path, "  ");
            Assert.Empty(ConnectorConfigFile.Load(path).Connectors);
            File.WriteAllText(path, """{ "connectors": null }""");
            Assert.Empty(ConnectorConfigFile.Load(path).Connectors);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MalformedFile_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => ConnectorConfigFile.Parse("{ connectors: [ "));
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips_Atomically()
    {
        var dir = Directory.CreateTempSubdirectory("pa-connectors-").FullName;
        try
        {
            var path = Path.Combine(dir, "sub", "connectors.json");
            ConnectorConfigFile.Save(path, new ConnectorsDocument { Connectors = [Valid() with { PriceLevel = 4 }] });

            Assert.False(File.Exists(path + ".tmp"));
            var json = File.ReadAllText(path);
            Assert.Contains("\"priceLevel\": 4", json); // camelCase on disk
            Assert.DoesNotContain("consumer", json, StringComparison.OrdinalIgnoreCase); // no credentials in the file
            Assert.Equal(4, Assert.Single(ConnectorConfigFile.Load(path).Connectors).PriceLevel);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("Shop1")]
    [InlineData("-shop")]
    [InlineData("shop 1")]
    [InlineData("")]
    [InlineData("a234567890123456789012345678901234567890x")] // 41 chars
    public void Validate_RejectsBadIds(string id)
    {
        Assert.Contains(ConnectorConfigFile.Validate(Valid() with { Id = id }), e => e.StartsWith("id "));
    }

    [Fact]
    public void Validate_RangesAndChoices()
    {
        Assert.Empty(ConnectorConfigFile.Validate(Valid()));
        Assert.NotEmpty(ConnectorConfigFile.Validate(Valid() with { PriceLevel = 0 }));
        Assert.NotEmpty(ConnectorConfigFile.Validate(Valid() with { PriceLevel = 7 }));
        Assert.NotEmpty(ConnectorConfigFile.Validate(Valid() with { StockBuffer = -1 }));
        Assert.NotEmpty(ConnectorConfigFile.Validate(Valid() with { PublishNewAs = "pending" }));
        Assert.NotEmpty(ConnectorConfigFile.Validate(Valid() with { Platform = "shopify" }));
        Assert.NotEmpty(ConnectorConfigFile.Validate(Valid() with { StoreUrl = "shop.example.com" }));
        Assert.NotEmpty(ConnectorConfigFile.Validate(Valid() with { StoreUrl = "ftp://shop.example.com" }));
        Assert.NotEmpty(ConnectorConfigFile.Validate(Valid() with { PushIntervalSeconds = 5 }));
        Assert.NotEmpty(ConnectorConfigFile.Validate(Valid() with { OrderPollMinutes = 0 }));
        Assert.NotEmpty(ConnectorConfigFile.Validate(Valid() with { StoreUrl = "https://user:pw@shop.example.com" }));
        // The API key (secret store), not developerId, is what the connector needs to run.
        Assert.Empty(ConnectorConfigFile.Validate(Valid() with { DeveloperId = null }));
    }

    [Fact]
    public void Runnable_OnlyEnabledValidAndUniqueIds()
    {
        var skipped = new List<string>();
        var runnable = ConnectorConfigFile.Runnable(new ConnectorsDocument
        {
            Connectors =
            [
                Valid("shop1"),
                Valid("shop2") with { Enabled = false },
                Valid("shop3") with { PriceLevel = 9 },
                Valid("dup"),
                Valid("dup"),
            ],
        }, (id, _) => skipped.Add(id));

        Assert.Equal(["shop1"], runnable.Select(c => c.Id));
        Assert.Equal(["shop3", "dup", "dup"], skipped);
    }

    [Fact]
    public void Runnable_RejectsConnectorsSharingADeveloperId_IgnoringCaseAndSpaces()
    {
        var skipped = new Dictionary<string, IReadOnlyList<string>>();
        var runnable = ConnectorConfigFile.Runnable(new ConnectorsDocument
        {
            Connectors =
            [
                Valid("shop1") with { DeveloperId = "WOO-shared" },
                Valid("shop2") with { DeveloperId = " woo-SHARED " },
                Valid("shop3") with { DeveloperId = "WOO-shop3" },
                Valid("shop4"), // no developerId recorded: nothing to compare
                Valid("shop5"),
            ],
        }, (id, errors) => skipped[id] = errors);

        Assert.Equal(["shop3", "shop4", "shop5"], runnable.Select(c => c.Id));
        Assert.Equal(["shop1", "shop2"], skipped.Keys.Order());
        Assert.All(skipped.Values, e => Assert.Contains(e, m => m.Contains("developerId 'WOO-shared'", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Runnable_RejectsConnectorsSharingAnApiKeySecret_EvenWhenOneIsDisabled()
    {
        var skipped = new Dictionary<string, IReadOnlyList<string>>();
        var runnable = ConnectorConfigFile.Runnable(new ConnectorsDocument
        {
            Connectors =
            [
                Valid("shop1"),
                Valid("shop1 ") with { Enabled = false }, // the same secret name: connector-shop1-pos-api-key
                Valid("shop2") with { DeveloperId = "WOO-x" },
                Valid("shop9") with { DeveloperId = "woo-X", Enabled = false }, // disabled, but it holds the Developer ID
            ],
        }, (id, errors) => skipped[id] = errors);

        Assert.Empty(runnable);
        Assert.Contains(skipped["shop1"], m => m.Contains("connector-shop1-pos-api-key", StringComparison.Ordinal));
        Assert.Contains(skipped["shop2"], m => m.Contains("developerId", StringComparison.Ordinal));
    }

    [Fact]
    public void Paths_AreTheAddOnsOwn_UnderTheSharedProgramDataRoot()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "POSitiveAnywhereV2");
        Assert.Equal(root, ConnectorPaths.Root);
        Assert.Equal(Path.Combine(root, "connectors.json"), ConnectorPaths.ConfigFile);
        Assert.Equal(Path.Combine(root, "connectors", "connectors.db"), ConnectorPaths.Database);
        // Its own secrets file — never the server's secrets.json.
        Assert.Equal(Path.Combine(root, "connectors", "secrets.json"), ConnectorPaths.Secrets);
        Assert.Equal(Path.Combine("C:\\x", "pa-connectors-.log"), ConnectorPaths.LogFile("logs/pa-connectors-.log", "C:\\x"));
    }

    [Fact]
    public void SecretNames_AreKeyedByConnectorId()
    {
        Assert.Equal("connector-shop1-woo-consumer-key", ConnectorSecretNames.WooConsumerKey("shop1"));
        Assert.Equal("connector-shop1-woo-consumer-secret", ConnectorSecretNames.WooConsumerSecret("shop1"));
        Assert.Equal("connector-shop1-woo-webhook-secret", ConnectorSecretNames.WooWebhookSecret("Shop1"));
        Assert.Equal("connector-shop1-pos-api-key", ConnectorSecretNames.PosApiKey("shop1"));
        Assert.NotEqual(ConnectorSecretNames.WooConsumerKey("shop1"), ConnectorSecretNames.WooConsumerKey("shop2"));
        Assert.Equal(4, ConnectorSecretNames.ForConnectorAll("shop1").Distinct().Count());
        Assert.Throws<ArgumentException>(() => ConnectorSecretNames.WooConsumerKey(" "));
    }

    [Fact]
    public void DpapiSecretStore_RoundTrips_InItsOwnFile()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var dir = Path.Combine(Path.GetTempPath(), "pa-connector-secrets-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "secrets.json");
            var store = new DpapiConnectorSecretStore(path);
            store.Set(ConnectorSecretNames.PosApiKey("shop1"), "k3y");

            Assert.Equal("k3y", new DpapiConnectorSecretStore(path).Get(ConnectorSecretNames.PosApiKey("shop1")));
            Assert.DoesNotContain("k3y", File.ReadAllText(path)); // encrypted at rest
            Assert.Equal([ConnectorSecretNames.PosApiKey("shop1")], store.List());
            Assert.True(store.Remove(ConnectorSecretNames.PosApiKey("shop1")));
            Assert.Null(store.Get(ConnectorSecretNames.PosApiKey("shop1")));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ConnectorProjects_DoNotReferencePACore()
    {
        // Revision 2 (design §0): the add-on talks to POSitive only through /api/v2.
        foreach (var assembly in new[]
                 {
                     typeof(ConnectorSettings).Assembly,
                     typeof(PA.Connectors.WooCommerce.WooClient).Assembly,
                     typeof(PA.Connectors.Host.ConnectorWorker).Assembly,
                 })
        {
            var references = assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
            Assert.DoesNotContain("PA.Core", references);
            Assert.DoesNotContain("PA.Server", references);
            Assert.DoesNotContain("Microsoft.Data.SqlClient", references);
        }

        // The other direction (PA.Server carries no connector code) is in ConnectorServerBoundaryTests.
    }
}
