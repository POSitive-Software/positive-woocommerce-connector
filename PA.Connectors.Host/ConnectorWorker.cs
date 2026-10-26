using System.Text.Json;
using PA.Connectors.Core;
using PA.Connectors.Core.Pos;
using PA.Connectors.Core.Stores;

namespace PA.Connectors.Host;

/// <summary>Where connectors.json is read from (Connectors:ConfigPath, default ProgramData).</summary>
public interface IConnectorConfigSource
{
    string Path { get; }

    /// <summary>Changes whenever the file does (write time + length); null when there is no file.</summary>
    object? Version();

    ConnectorsDocument Load();
}

public sealed class FileConnectorConfigSource : IConnectorConfigSource
{
    public FileConnectorConfigSource(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public object? Version()
    {
        var file = new FileInfo(Path);
        return file.Exists ? (file.LastWriteTimeUtc, file.Length) : null;
    }

    public ConnectorsDocument Load() => ConnectorConfigFile.Load(Path);
}

/// <summary>
/// The engine's stores and the POSitive API client factory, as one injectable bundle. <paramref name="Ui"/>
/// (S5) carries the match review's ignore marks: connectors raise exceptions through it, so an
/// ignored item's review exception is not raised again.
/// </summary>
public sealed record ConnectorEngine(IPosGatewayFactory Pos, ConnectorStores Stores, SqliteConnectorUiStore? Ui = null);

/// <summary>Worker cadence (Connectors:* in configuration).</summary>
public sealed record ConnectorWorkerOptions
{
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxJobsPerTick { get; init; } = 25;
    public TimeSpan KeepDoneJobs { get; init; } = TimeSpan.FromDays(7);
}

/// <summary>
/// Runs the enabled connectors from connectors.json: every tick, each
/// connector's due recurring jobs are enqueued and its queue drained, one job
/// at a time — so order imports are single-consumer (this is the only process
/// that owns connectors.db; the store's write gate and the lease rule cover the
/// rest). Invisible until a store is connected: with no file or no enabled
/// connector it makes no call at all (no POSitive API, no Woo) and says so once.
/// The file is re-read when it changes, so the connect wizard needs no restart.
/// </summary>
public sealed class ConnectorWorker : BackgroundService
{
    private readonly IConnectorConfigSource _config;
    private readonly IReadOnlyDictionary<string, IConnectorFactory> _factories;
    private readonly ConnectorEngine _engine;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ConnectorWorker> _logger;
    private readonly ConnectorWorkerOptions _options;
    private readonly TimeProvider _time;

    private object? _loadedVersion = new();
    private IReadOnlyList<ConnectorSettings> _runnable = [];
    private List<ConnectorRunner>? _runners;
    private bool _idleLogged;
    private DateTime _nextPurge = DateTime.MinValue;

    public ConnectorWorker(
        IConnectorConfigSource config, IEnumerable<IConnectorFactory> factories, ConnectorEngine engine,
        ILoggerFactory loggerFactory, ConnectorWorkerOptions? options = null, TimeProvider? time = null)
    {
        _config = config;
        _factories = factories.ToDictionary(f => f.Platform, StringComparer.OrdinalIgnoreCase);
        _engine = engine;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ConnectorWorker>();
        _options = options ?? new ConnectorWorkerOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>What the last tick decided — reported by GET /health.</summary>
    public string State { get; private set; } = "starting";

    /// <summary>Ids of the connectors the last tick ran.</summary>
    public IReadOnlyList<string> RunningConnectorIds => _runnable.Select(c => c.Id).ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never hold up host startup
        using var timer = new PeriodicTimer(_options.Tick, _time);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad tick (disk blip) must not kill the loop.
                _logger.LogWarning(ex, "Connector worker tick failed; will retry next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        ReloadIfChanged();

        if (_runnable.Count == 0)
        {
            State = "idle: no enabled connectors";
            if (!_idleLogged)
            {
                _logger.LogInformation("No enabled connectors in {Path}; connector worker idle", _config.Path);
                _idleLogged = true;
            }
            return;
        }
        _idleLogged = false;

        _runners ??= BuildRunners();
        State = $"running {_runners.Count} connector(s)";
        foreach (var runner in _runners)
        {
            try
            {
                await runner.ScheduleDueAsync(cancellationToken);
                await runner.DrainAsync(_options.MaxJobsPerTick, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Connector {ConnectorId} tick failed", runner.ConnectorId);
            }
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (now >= _nextPurge)
        {
            _nextPurge = now.AddHours(1);
            await _engine.Stores.Jobs.PurgeDoneAsync(now - _options.KeepDoneJobs, cancellationToken);
        }
    }

    private void ReloadIfChanged()
    {
        var version = _config.Version();
        if (Equals(version, _loadedVersion))
            return;
        _loadedVersion = version;
        _runners = null;

        ConnectorsDocument document;
        try
        {
            document = _config.Load();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not read {Path}; no connectors will run until it is fixed", _config.Path);
            _runnable = [];
            return;
        }

        _runnable = ConnectorConfigFile.Runnable(document, (id, errors) =>
            _logger.LogError("Connector '{ConnectorId}' in {Path} is enabled but not runnable: {Errors}",
                id, _config.Path, string.Join(" ", errors)));
        if (_runnable.Count > 0)
            _logger.LogInformation("Loaded {Count} enabled connector(s) from {Path}: {Ids}",
                _runnable.Count, _config.Path, string.Join(", ", _runnable.Select(c => c.Id)));
    }

    private List<ConnectorRunner> BuildRunners()
    {
        var runners = new List<ConnectorRunner>();
        var stores = _engine.Stores;
        foreach (var settings in _runnable)
        {
            if (!_factories.TryGetValue(settings.Platform, out var factory))
            {
                _logger.LogError("Connector '{ConnectorId}': no adapter for platform '{Platform}'", settings.Id, settings.Platform);
                continue;
            }

            var exceptions = _engine.Ui is null ? stores.Exceptions : new IgnoreAwareExceptionStore(stores.Exceptions, _engine.Ui);
            var services = new ConnectorServices(
                settings, _engine.Pos.Create(settings), stores.Jobs, stores.Cursors,
                exceptions, stores.Items, stores.Orders, _loggerFactory);
            runners.Add(new ConnectorRunner(
                factory.Create(services), stores.Jobs, stores.Cursors,
                _loggerFactory.CreateLogger($"PA.Connectors.{settings.Platform}.{settings.Id}"), time: _time));
        }
        return runners;
    }
}
