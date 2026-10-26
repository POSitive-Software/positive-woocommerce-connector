namespace PA.Connectors.Host;

/// <summary>
/// Entry point of the connector add-on service. An explicit class (not
/// top-level statements) so its type does not collide with PA.Server's global
/// <c>Program</c> in the test project, which references both.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        WebApplication? app = null;
        try
        {
            app = await ConnectorHostApp.BuildAsync(args);
            app.Logger.LogInformation("POSitive Anywhere connector host starting (version {Version})", HealthEndpoint.Version);
            await app.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            if (app is not null)
                app.Logger.LogCritical(ex, "POSitive Anywhere connector host terminated unexpectedly");
            else
                ReportStartupFailure(ex);
            return 1;
        }
        finally
        {
            if (app is not null)
                await app.DisposeAsync();
        }
    }

    /// <summary>
    /// Before Serilog exists (bad config, non-loopback URL, unreadable
    /// connectors.db) there is no log sink; a service has no console either, so
    /// leave the reason where support looks: the shared logs folder.
    /// </summary>
    private static void ReportStartupFailure(Exception ex)
    {
        var text = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [FTL] Connector host failed to start: {ex}{Environment.NewLine}";
        Console.Error.Write(text);
        try
        {
            Directory.CreateDirectory(PA.Connectors.Core.ConnectorPaths.Logs);
            File.AppendAllText(PA.Connectors.Core.ConnectorPaths.LogFile("pa-connectors-startup.log"), text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nowhere left to report to; the exit code still says it failed.
        }
    }
}
