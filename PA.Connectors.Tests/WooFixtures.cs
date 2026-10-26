using System.Text.Json;
using PA.Connectors.WooCommerce;

namespace PA.Tests.Connectors;

/// <summary>Recorded WooCommerce responses from the dev store (spike S0bc), copied to the test output byte-exact.</summary>
internal static class WooFixtures
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "WooCommerce");

    public static string Path_(string name) => Path.Combine(Directory, name);

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path_(name));

    public static string Text(string name) => File.ReadAllText(Path_(name));

    public static T Read<T>(string name) =>
        JsonSerializer.Deserialize<T>(Text(name), WooJson.Options)
        ?? throw new InvalidOperationException($"{name} deserialized to null");

    /// <summary>A property of a JSON object fixture, deserialized (order-refund.json is {order, refund}).</summary>
    public static T ReadProperty<T>(string name, string property)
    {
        using var document = JsonDocument.Parse(Text(name));
        return document.RootElement.GetProperty(property).Deserialize<T>(WooJson.Options)
               ?? throw new InvalidOperationException($"{name}.{property} deserialized to null");
    }
}
