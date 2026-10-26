using PA.Connectors.Core;
using PA.Connectors.Core.Pos;
using PA.Connectors.Core.Secrets;
using PA.Connectors.Core.Stores;
using PA.Connectors.Host.Ui;
using PA.Connectors.WooCommerce;
using Serilog;

namespace PA.Connectors.Host;

/// <summary>
/// The connector add-on's host (design §0, revision 2): its own Windows service
/// (<see cref="ServiceName"/>), its own SQLite store, its own secrets, and
/// POSitive reached ONLY through the public /api/v2 with the connector's own key.
/// Listens on loopback only — the internet reaches /hooks through PA.Server's
/// byte-for-byte forward, never this port directly.
/// </summary>
public static class ConnectorHostApp
{
    public const string ServiceName = "POSitiveAnywhereConnectors";
    public const string DefaultUrl = "http://localhost:5220";

    /// <summary>
    /// Builds the host: configuration (appsettings next to the exe), Serilog,
    /// services, endpoints, and the connectors.db schema (migrated before
    /// anything else runs). <paramref name="configure"/> lets tests swap in a
    /// TestServer, temp paths and fakes.
    /// </summary>
    public static async Task<WebApplication> BuildAsync(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        // When hosted as a Windows service the process starts in System32; pin
        // the content root to the exe's folder so appsettings and logs resolve.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Host.UseWindowsService(options => options.ServiceName = ServiceName);
        UseOwnSettingsFile(builder);

        var urls = builder.Configuration["Urls"];
        if (string.IsNullOrWhiteSpace(urls))
            urls = DefaultUrl;
        builder.WebHost.UseUrls(RequireLoopback(urls));
        // Kestrel:Endpoints would bypass Urls — hold them to the same rule.
        foreach (var endpoint in builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren())
            RequireLoopback(endpoint["Url"] ?? "");

        ConfigureLogging(builder);
        builder.Services.AddConnectorHost(builder.Configuration);
        configure?.Invoke(builder);

        var app = builder.Build();
        app.UseSerilogRequestLogging();
        // S5: /connectors/** (the connector pages and their API) — loopback peer and Host only,
        // and the portal user PA.Server's forward names (Ui/ConnectorUiGate).
        app.UseMiddleware<ConnectorUiGate>();
        app.MapHealthEndpoint();
        app.MapWebhookEndpoints();
        app.MapConnectorUiAssets();
        app.MapConnectorUi();

        var db = app.Services.GetRequiredService<ConnectorDatabase>();
        var (before, after) = await db.MigrateAsync();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ConnectorHostApp));
        if (before != after)
            logger.LogInformation("connectors.db at {Path} migrated from schema version {Before} to {After}", db.Path, before, after);
        return app;
    }

    /// <summary>
    /// Replaces the default appsettings.json / appsettings.{env}.json sources
    /// with <see cref="SettingsFile"/> / connectorhost.{env}.json at the same
    /// position (so environment variables and args still win). The add-on may
    /// share a folder with PA.Server; the server's appsettings.json is not ours.
    /// </summary>
    private static void UseOwnSettingsFile(WebApplicationBuilder builder)
    {
        var sources = builder.Configuration.Sources;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is not Microsoft.Extensions.Configuration.Json.JsonConfigurationSource json
                || json.Path is null
                || !json.Path.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase))
                continue;
            var replacement = new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
            {
                Path = "connectorhost." + json.Path["appsettings.".Length..],
                Optional = true,
                ReloadOnChange = false,
                FileProvider = json.FileProvider,
            };
            replacement.ResolveFileProvider();
            sources[i] = replacement;
        }
    }

    /// <summary>The host's settings file beside the exe.</summary>
    public const string SettingsFile = "connectorhost.json";

    /// <summary>
    /// The host must never listen beyond this machine: every configured URL has
    /// to be http(s) on localhost / 127.0.0.1 / [::1]. Anything else refuses to start.
    /// </summary>
    public static string RequireLoopback(string urls)
    {
        foreach (var raw in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var url)
                || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
                || !(url.IsLoopback || string.Equals(url.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"Connector host URL '{raw}' is not a loopback http(s) URL. The connector host listens on this machine only " +
                    $"(default {DefaultUrl}); the internet reaches /hooks through the POSitive Anywhere server's forward.");
        }
        return urls;
    }

    /// <summary>
    /// Serilog from configuration, with every file sink's path pinned to the
    /// ProgramData logs folder by FILE NAME (the server's rule — a relative path
    /// lands in System32 under the SCM). Logging:Directory lets tests redirect.
    /// The static Log.Logger is left alone (preserveStaticLogger), so a test
    /// process can build several hosts.
    /// </summary>
    private static void ConfigureLogging(WebApplicationBuilder builder)
    {
        foreach (var sink in builder.Configuration.GetSection("Serilog:WriteTo").GetChildren())
        {
            var configured = sink["Args:path"];
            if (string.IsNullOrWhiteSpace(configured))
                continue;
            builder.Configuration[$"Serilog:WriteTo:{sink.Key}:Args:path"] =
                ConnectorPaths.LogFile(configured, builder.Configuration["Logging:Directory"]);
        }

        builder.Host.UseSerilog(
            (context, services, configuration) => configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext(),
            preserveStaticLogger: true);
    }

    /// <summary>DI for the engine: config file, SQLite stores, secrets, POSitive API client, the Woo adapter, the worker.</summary>
    public static IServiceCollection AddConnectorHost(this IServiceCollection services, IConfiguration configuration)
    {
        var configPath = PathSetting(configuration, "Connectors:ConfigPath", ConnectorPaths.ConfigFile);
        var databasePath = PathSetting(configuration, "Connectors:DatabasePath", ConnectorPaths.Database);
        var secretsPath = PathSetting(configuration, "Connectors:SecretsPath", ConnectorPaths.Secrets);

        services.AddSingleton<IConnectorConfigSource>(new FileConnectorConfigSource(configPath));
        services.AddSingleton(sp => new ConnectorDatabase(databasePath, sp.GetService<TimeProvider>()));
        services.AddSingleton(sp => ConnectorStores.Sqlite(sp.GetRequiredService<ConnectorDatabase>()));
        services.AddSingleton(sp => sp.GetRequiredService<ConnectorStores>().Jobs);
        services.AddSingleton(sp => sp.GetRequiredService<ConnectorStores>().Exceptions);
        services.AddSingleton<IConnectorSecretStore>(_ =>
            OperatingSystem.IsWindows()
                ? new DpapiConnectorSecretStore(secretsPath)
                : throw new PlatformNotSupportedException("The connector secret store (DPAPI) requires Windows."));

        // Where the connector sends its POSitive API key: a host setting, this machine only (refuses to start otherwise).
        services.AddSingleton(PosApiAddress.Require(configuration[PosApiAddress.SettingKey]));
        // Which store addresses may be called (no private / loopback / link-local unless the dev override is on).
        services.AddSingleton(new WooStoreAddress(configuration.GetValue(WooStoreAddress.AllowPrivateSettingKey, false)));
        // The secret PA.Server's forwards carry; only with it are the X-PA-Portal-* headers believed.
        var forwardTokenPath = PathSetting(configuration, ConnectorForwardToken.PathSettingKey, ConnectorForwardToken.DefaultPath);
        services.AddSingleton(sp => new ConnectorForwardToken(forwardTokenPath,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<ConnectorForwardToken>(), sp.GetService<TimeProvider>()));

        // Both clients carry their own per-request timeouts; neither follows redirects.
        services.AddHttpClient(PosApiClientFactory.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(PosApiClientFactory.CreateHandler);
        services.AddSingleton<IPosGatewayFactory>(sp => new PosApiClientFactory(
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IConnectorSecretStore>(),
            sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<PosApiEndpoint>()));
        services.AddHttpClient(WooConnectorFactory.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<WooStoreAddress>().CreateHandler());
        services.AddSingleton<IConnectorFactory, WooConnectorFactory>();

        services.AddSingleton(sp => new SqliteConnectorUiStore(sp.GetRequiredService<ConnectorDatabase>()));
        services.AddSingleton(sp => new ConnectorEngine(
            sp.GetRequiredService<IPosGatewayFactory>(), sp.GetRequiredService<ConnectorStores>(),
            sp.GetRequiredService<SqliteConnectorUiStore>()));
        services.AddSingleton(new ConnectorWorkerOptions
        {
            Tick = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Connectors:TickSeconds", 5), 1, 300)),
            MaxJobsPerTick = Math.Clamp(configuration.GetValue("Connectors:MaxJobsPerTick", 25), 1, 1000),
        });
        services.AddSingleton(sp => new ConnectorWorker(
            sp.GetRequiredService<IConnectorConfigSource>(),
            sp.GetServices<IConnectorFactory>(),
            sp.GetRequiredService<ConnectorEngine>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<ConnectorWorkerOptions>()));
        services.AddHostedService(sp => sp.GetRequiredService<ConnectorWorker>());

        // S5 connector UI (/connectors/**, reached through PA.Server's forward).
        services.AddSingleton(sp => new ConnectorConfigEditor(sp.GetRequiredService<IConnectorConfigSource>()));
        services.AddSingleton<IWooConnectionTester, WooConnectionTester>();
        services.AddSingleton<IPosKeyVerifier, PosKeyVerifier>();
        services.AddSingleton<IWooItemLookup, WooItemLookup>();
        services.AddSingleton(sp => new PosProductSearch(sp.GetRequiredService<IPosGatewayFactory>(), sp.GetService<TimeProvider>()));
        services.AddSingleton<ConnectorUiAudit>();
        services.AddSingleton(sp => new ConnectorUiRateLimiter(sp.GetService<TimeProvider>()));
        services.AddSingleton(sp => new ConnectorUi(
            sp.GetRequiredService<ConnectorConfigEditor>(), sp.GetRequiredService<IConnectorSecretStore>(),
            sp.GetRequiredService<ConnectorStores>(), sp.GetRequiredService<SqliteConnectorUiStore>(),
            sp.GetRequiredService<ConnectorWorker>(), sp.GetRequiredService<IWooConnectionTester>(),
            sp.GetRequiredService<IPosKeyVerifier>(), sp.GetRequiredService<PosProductSearch>(),
            sp.GetRequiredService<ConnectorUiAudit>(), sp.GetRequiredService<WooStoreAddress>(),
            sp.GetRequiredService<ConnectorUiRateLimiter>(), sp.GetService<TimeProvider>(), sp.GetService<IWooItemLookup>()));
        return services;
    }

    private static string PathSetting(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
