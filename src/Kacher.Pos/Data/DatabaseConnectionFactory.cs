using System.Data.Common;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Kacher.Pos.Data;

/// <summary>
/// Owns the on-disk database location and builds the SQLite connection string.
/// Everything lives under <c>%LOCALAPPDATA%\Kacher</c> so the terminal needs no admin rights.
/// </summary>
public static class DatabaseConnectionFactory
{
    public const string AppFolderName = "Kacher";

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string DatabasePath => Path.Combine(DataDirectory, "pos.db");

    /// <summary>Folder for product images and generated PDF reports.</summary>
    public static string ImagesDirectory => Path.Combine(DataDirectory, "images");

    public static string ReportsDirectory => Path.Combine(DataDirectory, "reports");

    /// <summary>Folder for the raw ESC/POS diagnostic captures used when commissioning hardware.</summary>
    public static string SpoolDirectory => Path.Combine(DataDirectory, "spool");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static void EnsureDirectories()
    {
        foreach (var dir in new[] { DataDirectory, ImagesDirectory, ReportsDirectory, SpoolDirectory, LogDirectory })
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>
    /// <c>Pooling=True</c> avoids reconnect churn on every save (a measurable win at the till, where a
    /// sale commit happens every few seconds). <c>Foreign Keys=True</c> is required for the cascade
    /// deletes declared in the model configuration.
    /// </summary>
    public static string BuildConnectionString(string? overridePath = null)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = overridePath ?? DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 15
        };
        return builder.ToString();
    }

    public static DbContextOptions<KacherDbContext> BuildOptions(string? overridePath = null) =>
        new DbContextOptionsBuilder<KacherDbContext>()
            .UseSqlite(BuildConnectionString(overridePath))
            .AddInterceptors(new SqlitePragmaInterceptor())
            .EnableSensitiveDataLogging(false)
            .Options;

    public static KacherDbContext CreateContext(string? overridePath = null) =>
        new(BuildOptions(overridePath));
}

/// <summary>
/// Applies the per-connection pragmas that Microsoft.Data.Sqlite does not expose as connection
/// string keywords. These are the difference between a 4 ms and a 20 ms commit on spinning rust,
/// and they also stop the UI thread from ever blocking on a writer lock (<c>busy_timeout</c>).
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string PragmaScript = """
        PRAGMA journal_mode = WAL;        -- readers never block the writer
        PRAGMA synchronous = NORMAL;      -- durable under WAL, far cheaper fsync
        PRAGMA busy_timeout = 5000;       -- wait instead of throwing "database is locked"
        PRAGMA cache_size = -20000;       -- 20 MB resident page cache
        PRAGMA temp_store = MEMORY;       -- reporting GROUP BYs stay in RAM
        PRAGMA mmap_size = 134217728;     -- 128 MB memory-mapped I/O
        """;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyAsync(connection, cancellationToken).ConfigureAwait(false);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    private static void Apply(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = PragmaScript;
        cmd.ExecuteNonQuery();
    }

    private static async Task ApplyAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = PragmaScript;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
