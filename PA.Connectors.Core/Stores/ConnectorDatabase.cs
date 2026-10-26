using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PA.Connectors.Core.Stores;

/// <summary>
/// The add-on's own SQLite database (default
/// <c>%ProgramData%\POSitiveAnywhereV2\connectors\connectors.db</c>): job queue,
/// cursors, item/order maps and exceptions. Nothing here lives in the POSitive
/// database, so a connector release never needs a POSitive schema upgrade.
/// <para>
/// One process (the connector host) owns the file. WAL lets /health read while
/// the worker writes; every write runs under <see cref="WriteGate"/> (in-process
/// serialisation — the "claim lock" the SQL Server version took with
/// sp_getapplock) inside a <c>BEGIN IMMEDIATE</c> transaction, and
/// <c>busy_timeout</c> covers anyone else briefly holding the file (a backup,
/// an admin with a SQLite browser).
/// </para>
/// Times are stored as ISO-8601 UTC text (<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>),
/// which sorts and compares correctly as text; "now" comes from the injected
/// <see cref="TimeProvider"/> so backoff and leases are testable.
/// </summary>
public sealed class ConnectorDatabase
{
    /// <summary>Schema versions, applied in order inside one transaction each; <c>PRAGMA user_version</c> records the last.</summary>
    internal static readonly IReadOnlyList<(int Version, string Sql)> Migrations =
    [
        (1, """
            CREATE TABLE ConnectorJob (
                JobId         INTEGER PRIMARY KEY AUTOINCREMENT,
                ConnectorId   TEXT    NOT NULL,
                Kind          TEXT    NOT NULL,
                ResourceKey   TEXT    NOT NULL DEFAULT '',
                Payload       TEXT    NULL,
                Status        TEXT    NOT NULL DEFAULT 'pending'
                              CHECK (Status IN ('pending', 'running', 'done', 'failed')),
                Attempts      INTEGER NOT NULL DEFAULT 0,
                NextAttemptAt TEXT    NOT NULL,
                LeaseUntil    TEXT    NULL,
                LastError     TEXT    NULL,
                CreatedAt     TEXT    NOT NULL,
                UpdatedAt     TEXT    NOT NULL,
                CompletedAt   TEXT    NULL
            );
            -- At most ONE pending job per key: a duplicate webhook delivery coalesces.
            CREATE UNIQUE INDEX UX_ConnectorJob_Pending ON ConnectorJob (ConnectorId, Kind, ResourceKey)
                WHERE Status = 'pending';
            CREATE INDEX IX_ConnectorJob_Due ON ConnectorJob (ConnectorId, Status, NextAttemptAt);

            CREATE TABLE ConnectorCursor (
                ConnectorId TEXT NOT NULL,
                CursorName  TEXT NOT NULL,
                Value       TEXT NULL,
                UpdatedAt   TEXT NOT NULL,
                PRIMARY KEY (ConnectorId, CursorName)
            );

            CREATE TABLE ConnectorItem (
                ConnectorId      TEXT    NOT NULL,
                InvNo            INTEGER NOT NULL,
                ExternalId       INTEGER NOT NULL,
                ExternalParentId INTEGER NOT NULL DEFAULT 0,
                ExternalType     TEXT    NULL,
                Sku              TEXT    NULL,
                LastPushHash     TEXT    NULL,
                LastPushedAt     TEXT    NULL,
                CreatedAt        TEXT    NOT NULL,
                UpdatedAt        TEXT    NOT NULL,
                PRIMARY KEY (ConnectorId, InvNo)
            );
            CREATE UNIQUE INDEX UX_ConnectorItem_External ON ConnectorItem (ConnectorId, ExternalId);

            CREATE TABLE ConnectorOrder (
                ConnectorId        TEXT    NOT NULL,
                ExternalOrderId    INTEGER NOT NULL,
                OrderNumber        TEXT    NULL,
                InvoiceNo          INTEGER NULL,
                OrdGuid            TEXT    NULL,
                ImportStatus       TEXT    NOT NULL DEFAULT 'pending'
                                   CHECK (ImportStatus IN ('pending', 'imported', 'exception', 'ignored')),
                PaymentPosted      INTEGER NOT NULL DEFAULT 0,
                PaymentPostedAt    TEXT    NULL,
                ExternalStatus     TEXT    NULL,
                ExternalModifiedAt TEXT    NULL,
                LastError          TEXT    NULL,
                CreatedAt          TEXT    NOT NULL,
                UpdatedAt          TEXT    NOT NULL,
                ImportedAt         TEXT    NULL,
                PRIMARY KEY (ConnectorId, ExternalOrderId)
            );
            CREATE INDEX IX_ConnectorOrder_InvoiceNo ON ConnectorOrder (InvoiceNo) WHERE InvoiceNo IS NOT NULL;

            CREATE TABLE ConnectorException (
                ExceptionId INTEGER PRIMARY KEY AUTOINCREMENT,
                ConnectorId TEXT    NOT NULL,
                Kind        TEXT    NOT NULL,
                ExternalRef TEXT    NOT NULL DEFAULT '',
                PosRef      TEXT    NULL,
                Reason      TEXT    NOT NULL,
                Snapshot    TEXT    NULL,
                Status      TEXT    NOT NULL DEFAULT 'open'
                            CHECK (Status IN ('open', 'retried', 'resolved', 'ignored')),
                Occurrences INTEGER NOT NULL DEFAULT 1,
                JobId       INTEGER NULL,
                CreatedAt   TEXT    NOT NULL,
                UpdatedAt   TEXT    NOT NULL,
                ResolvedBy  TEXT    NULL,
                ResolvedAt  TEXT    NULL
            );
            -- One OPEN row per (connector, kind, ref): a repeat bumps Occurrences.
            CREATE UNIQUE INDEX UX_ConnectorException_Open ON ConnectorException (ConnectorId, Kind, ExternalRef)
                WHERE Status = 'open';
            CREATE INDEX IX_ConnectorException_List ON ConnectorException (ConnectorId, Status, CreatedAt);
            """),
        (2, """
            -- What was imported (Woo total incl. tax, and its tax), so a later edit is noticed
            -- and the payment is never posted for an amount the invoice does not carry.
            ALTER TABLE ConnectorOrder ADD COLUMN ImportedTotal TEXT NULL;
            ALTER TABLE ConnectorOrder ADD COLUMN ImportedTax   TEXT NULL;

            -- 1 = dead-lettered because retrying cannot help (not a transient fault that ran out
            -- of attempts); the order poll re-queues only the transient ones.
            ALTER TABLE ConnectorJob ADD COLUMN FailedPermanently INTEGER NOT NULL DEFAULT 0;

            -- Platform orders that hold stock on the platform (Woo reduced its stock for them)
            -- but are not imported into POSitive, per order and item. Pushed stock subtracts
            -- them, so a push never undoes the platform's own hold (oversell).
            CREATE TABLE ConnectorStockHold (
                ConnectorId     TEXT    NOT NULL,
                ExternalOrderId INTEGER NOT NULL,
                InvNo           INTEGER NOT NULL,
                Quantity        TEXT    NOT NULL,
                ExternalStatus  TEXT    NULL,
                UnresolvedLines INTEGER NOT NULL DEFAULT 0,
                CheckedAt       TEXT    NOT NULL,
                PRIMARY KEY (ConnectorId, ExternalOrderId, InvNo)
            );
            CREATE INDEX IX_ConnectorStockHold_Item ON ConnectorStockHold (ConnectorId, InvNo);
            """),
        (3, """
            -- S5 match review "ignore": an item (platform id, or inv:<invNo>) the owner chose to
            -- leave alone. The review exceptions (unmatched / ambiguous) are not raised for it
            -- again until the mark is removed. Kind = the exception it was ignored from (info).
            CREATE TABLE ConnectorIgnore (
                ConnectorId TEXT NOT NULL,
                ExternalRef TEXT NOT NULL,
                Kind        TEXT NOT NULL,
                Note        TEXT NULL,
                IgnoredBy   TEXT NOT NULL,
                IgnoredAt   TEXT NOT NULL,
                PRIMARY KEY (ConnectorId, ExternalRef)
            );
            """),
        (4, """
            -- What fixed a closed exception, as the exceptions page shows it first ("Imported as
            -- POSitive order 50001919 on retry; line 14 linked to …"). Null on older rows and on
            -- rows closed by hand.
            ALTER TABLE ConnectorException ADD COLUMN Resolution TEXT NULL;
            """),
    ];

    public static int LatestVersion => Migrations[^1].Version;

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private readonly string _connectionString;

    public ConnectorDatabase(string path, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        Time = time ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string Path { get; }

    public TimeProvider Time { get; }

    /// <summary>Serialises every write in this process (one writer at a time; readers are not blocked under WAL).</summary>
    internal SemaphoreSlim WriteGate { get; } = new(1, 1);

    internal DateTime UtcNow() => Time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Creates the file if needed, switches it to WAL and applies any pending
    /// migrations. Returns the version before and after. Refuses a database
    /// written by a NEWER build (its schema is unknown here).
    /// </summary>
    public async Task<(int Before, int After)> MigrateAsync(CancellationToken cancellationToken = default)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await WriteGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await ExecAsync(connection, null, "PRAGMA journal_mode = WAL;", cancellationToken);
            var before = await GetVersionAsync(connection, cancellationToken);
            if (before > LatestVersion)
                throw new InvalidOperationException(
                    $"{Path} is at schema version {before}, newer than this build understands ({LatestVersion}). " +
                    "Install the newer connector add-on, or restore a matching database.");

            foreach (var (version, sql) in Migrations.Where(m => m.Version > before))
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                await ExecAsync(connection, transaction, sql, cancellationToken);
                // user_version lives in the file header and is written inside the transaction.
                await ExecAsync(connection, transaction,
                    $"PRAGMA user_version = {version.ToString(CultureInfo.InvariantCulture)};", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            return (before, await GetVersionAsync(connection, cancellationToken));
        }
        finally
        {
            WriteGate.Release();
        }
    }

    /// <summary>The recorded schema version (0 = empty/new file).</summary>
    public async Task<int> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await GetVersionAsync(connection, cancellationToken);
    }

    private static async Task<int> GetVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    internal async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    /// <summary>Runs <paramref name="work"/> as THE writer: in-process gate + BEGIN IMMEDIATE transaction, committed on success.</summary>
    internal async Task<T> WriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        await WriteGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            // deferred: false = BEGIN IMMEDIATE — take the write lock up front, so a
            // read-then-write transaction can never deadlock on lock upgrade.
            await using var transaction = connection.BeginTransaction(deferred: false);
            var result = await work(connection, transaction);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            WriteGate.Release();
        }
    }

    internal async Task<T> ReadAsync<T>(Func<SqliteConnection, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await work(connection);
    }

    // --- tiny ADO helpers (explicit mapping: no ORM type-guessing over SQLite's dynamic types) ---

    internal static SqliteCommand Command(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] args)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    internal static async Task<int> ExecAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken,
        params (string Name, object? Value)[] args)
    {
        await using var command = Command(connection, transaction, sql, args);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<object?> ScalarAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken,
        params (string Name, object? Value)[] args)
    {
        await using var command = Command(connection, transaction, sql, args);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull ? null : value;
    }

    internal static async Task<List<T>> QueryAsync<T>(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken, params (string Name, object? Value)[] args)
    {
        await using var command = Command(connection, transaction, sql, args);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(map(reader));
        return rows;
    }

    internal static string FormatTime(DateTime value) =>
        (value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime())
        .ToString(TimeFormat, CultureInfo.InvariantCulture);

    internal static string? FormatTime(DateTime? value) => value is null ? null : FormatTime(value.Value);

    internal static DateTime ParseTime(string value) =>
        DateTime.ParseExact(value, TimeFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    internal static string? Str(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    internal static long Long(SqliteDataReader reader, string column) => reader.GetInt64(reader.GetOrdinal(column));

    internal static long? NullableLong(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    internal static DateTime ReadTime(SqliteDataReader reader, string column) => ParseTime(reader.GetString(reader.GetOrdinal(column)));

    internal static DateTime? ReadNullableTime(SqliteDataReader reader, string column) =>
        Str(reader, column) is { } text ? ParseTime(text) : null;

    internal static string? FormatDecimal(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);

    internal static decimal? ReadNullableDecimal(SqliteDataReader reader, string column) =>
        Str(reader, column) is { } text ? decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture) : null;

    internal static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
