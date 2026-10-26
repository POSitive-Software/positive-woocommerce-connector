using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PA.Connectors.Core.Secrets;

/// <summary>Named secret storage for the connector add-on (platform credentials, its POSitive API key).</summary>
public interface IConnectorSecretStore
{
    /// <summary>The value, or null if not present.</summary>
    string? Get(string name);

    /// <summary>Creates or replaces a secret.</summary>
    void Set(string name, string value);

    /// <summary>Removes a secret. False if it did not exist.</summary>
    bool Remove(string name);

    /// <summary>Names of all stored secrets (never the values).</summary>
    IReadOnlyList<string> List();
}

/// <summary>
/// Secret names, keyed by connector id (connectors.json). One place, so the
/// webhook receiver, the worker and the connect wizard agree.
/// </summary>
public static class ConnectorSecretNames
{
    /// <summary>WooCommerce REST consumer key (ck_…).</summary>
    public static string WooConsumerKey(string connectorId) => ForConnector(connectorId, "woo-consumer-key");

    /// <summary>WooCommerce REST consumer secret (cs_…).</summary>
    public static string WooConsumerSecret(string connectorId) => ForConnector(connectorId, "woo-consumer-secret");

    /// <summary>Secret the connector generated for the webhooks it registered (HMAC key for X-WC-Webhook-Signature).</summary>
    public static string WooWebhookSecret(string connectorId) => ForConnector(connectorId, "woo-webhook-secret");

    /// <summary>
    /// The connector's OWN POSitive API key (X-Api-Key for /api/v2). Minted for
    /// the connector's own DeveloperID, so its APITableLink space is separate.
    /// </summary>
    public static string PosApiKey(string connectorId) => ForConnector(connectorId, "pos-api-key");

    /// <summary>Every secret name a connector can own — what removing the connector clears.</summary>
    public static IReadOnlyList<string> ForConnectorAll(string connectorId) =>
        [WooConsumerKey(connectorId), WooConsumerSecret(connectorId), WooWebhookSecret(connectorId), PosApiKey(connectorId)];

    private static string ForConnector(string connectorId, string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        return $"connector-{connectorId.Trim().ToLowerInvariant()}-{suffix}";
    }
}

/// <summary>
/// File-backed secret store; each value encrypted with Windows DPAPI
/// (LocalMachine scope), so the service account and an elevated admin on THIS
/// machine can read it, and a copied file is useless elsewhere. Same scheme as
/// the server's store, but its own file (connectors\secrets.json) and entropy:
/// the add-on never reads or writes the server's secrets.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiConnectorSecretStore : IConnectorSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("POSitiveAnywhereV2.Connectors.SecretStore.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();

    public DpapiConnectorSecretStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public string? Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_lock)
        {
            if (!Load().TryGetValue(name, out var cipherB64))
                return null;
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(cipherB64), Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
    }

    public void Set(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        lock (_lock)
        {
            var map = Load();
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.LocalMachine);
            map[name] = Convert.ToBase64String(cipher);
            Save(map);
        }
    }

    public bool Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_lock)
        {
            var map = Load();
            if (!map.Remove(name))
                return false;
            Save(map);
            return true;
        }
    }

    public IReadOnlyList<string> List()
    {
        lock (_lock)
            return Load().Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var json = File.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        return new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
    }

    private void Save(Dictionary<string, string> map)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(map, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }
}
