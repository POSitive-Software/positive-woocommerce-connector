namespace PA.Connectors.Core;

/// <summary>
/// Where the connector add-on keeps its machine-level files. Same root as the
/// POSitive Anywhere service (%ProgramData%\POSitiveAnywhereV2) so support finds
/// everything in one place, but the add-on owns its own files and never touches
/// the server's (instance.json, secrets.json): it is released separately.
/// GetFolderPath is a shell API, so it resolves the same for LocalSystem (the
/// service) and an interactive admin — environment tokens do not (no ProgramData
/// in a service's environment block).
/// </summary>
public static class ConnectorPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "POSitiveAnywhereV2");

    /// <summary>Connector settings (design §2.1: config is a file, not a table).</summary>
    public static string ConfigFile => Path.Combine(Root, "connectors.json");

    /// <summary>The add-on's own folder: its database and its secrets.</summary>
    public static string Folder => Path.Combine(Root, "connectors");

    /// <summary>SQLite: job queue, cursors, item/order maps, exceptions.</summary>
    public static string Database => Path.Combine(Folder, "connectors.db");

    /// <summary>DPAPI (LocalMachine) store for platform credentials and the connector's POSitive API key.</summary>
    public static string Secrets => Path.Combine(Folder, "secrets.json");

    /// <summary>Shared logs folder (the server's pa-server-*.log lives there too).</summary>
    public static string Logs => Path.Combine(Root, "logs");

    /// <summary>
    /// Absolute path for a Serilog file sink from the FILE NAME held in config
    /// (a relative path would land in System32 under the SCM). Mirrors the
    /// server's rule; <paramref name="directoryOverride"/> keeps test output out
    /// of ProgramData.
    /// </summary>
    public static string LogFile(string configuredPathOrName, string? directoryOverride = null) =>
        Path.Combine(
            string.IsNullOrWhiteSpace(directoryOverride) ? Logs : directoryOverride,
            Path.GetFileName(configuredPathOrName));
}
