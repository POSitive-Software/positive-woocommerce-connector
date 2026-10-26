using Microsoft.Data.Sqlite;

namespace PA.Tests.Connectors;

/// <summary>
/// Removes a test's temp folder (connectors.db and its -wal/-shm, connectors.json, logs) after the
/// test. Pooled SQLite connections are released first; then a delete that Windows refuses because a
/// handle is still closing (the pool's last connection checkpointing the WAL, or an on-access
/// virus scan of a file just written) is retried for a few seconds instead of failing the test in
/// its cleanup.
/// </summary>
internal static class TestDirectory
{
    public static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 50)
            {
                Thread.Sleep(100);
                SqliteConnection.ClearAllPools();
            }
        }
    }
}
