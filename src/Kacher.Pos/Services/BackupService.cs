using System.Globalization;
using Kacher.Pos.Data;
using Kacher.Pos.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kacher.Pos.Services;

/// <summary>Result of a backup run: where the copy went and how big it is.</summary>
public sealed record BackupResult(string Path, long Bytes, DateTime CreatedAtLocal)
{
    public string SizeText => Bytes < 1024 * 1024
        ? $"{Bytes / 1024d:0} KB"
        : $"{Bytes / 1024d / 1024d:0.00} MB";
}

public interface IBackupService
{
    /// <summary>Folder that holds the rotating backup set.</summary>
    string BackupDirectory { get; }

    /// <summary>
    /// Creates a consistent snapshot with SQLite's <c>VACUUM INTO</c>. Safe to call while the till is
    /// trading: the engine serialises the copy against writers instead of locking the file.
    /// </summary>
    Task<BackupResult> CreateAsync(CancellationToken ct = default);

    /// <summary>Newest backup on disk, or <c>null</c> when none exists yet.</summary>
    BackupResult? GetLatest();

    /// <summary>
    /// Runs a backup when the newest one is older than <paramref name="maxAge"/>. Called on startup so
    /// a terminal left running for weeks still has a recent copy.
    /// </summary>
    Task<BackupResult?> EnsureRecentAsync(TimeSpan maxAge, CancellationToken ct = default);

    /// <summary>Deletes all but the newest <paramref name="keep"/> snapshots.</summary>
    int Prune(int keep = 30);
}

/// <summary>
/// Local, zero-dependency backup of the SQLite database.
/// </summary>
/// <remarks>
/// A POS database is the business: losing it means losing the day's takings and the stock history.
/// The strategy here is deliberately boring and therefore reliable — a consistent file snapshot into
/// <c>%LOCALAPPDATA%\Kacher\backups</c>, one per day plus any manual copies, pruned to the last 30.
/// Point a cloud-sync folder (OneDrive / Dropbox) at that directory and off-site backup is free.
/// </remarks>
public sealed class BackupService : IBackupService
{
    private const string FilePrefix = "kacher-";
    private const string FileExtension = ".db";

    private readonly IDbContextFactory<KacherDbContext> _factory;
    private readonly IAppLog _log;

    public BackupService(IDbContextFactory<KacherDbContext> factory, IAppLog log)
    {
        _factory = factory;
        _log = log;
    }

    public string BackupDirectory => Path.Combine(
        Path.GetDirectoryName(DatabaseConnectionFactory.DatabasePath) ?? AppContext.BaseDirectory,
        "backups");

    public async Task<BackupResult> CreateAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(BackupDirectory);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(BackupDirectory, $"{FilePrefix}{stamp}{FileExtension}");

        // VACUUM INTO refuses to overwrite, so two clicks in the same second would fail. Suffix a
        // counter instead of throwing at the operator.
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(BackupDirectory, $"{FilePrefix}{stamp}-{n}{FileExtension}");
        }

        // VACUUM INTO writes a fully-formed, defragmented copy of the live database in one statement.
        var target = path.Replace("'", "''", StringComparison.Ordinal);

        await using (var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{target}';", ct).ConfigureAwait(false);
        }

        var info = new FileInfo(path);
        var result = new BackupResult(path, info.Exists ? info.Length : 0, DateTime.Now);

        _log.Info($"Database backed up to {path} ({result.SizeText}).");
        return result;
    }

    public BackupResult? GetLatest()
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return null;
        }

        var newest = new DirectoryInfo(BackupDirectory)
            .GetFiles($"{FilePrefix}*{FileExtension}")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        return newest is null ? null : new BackupResult(newest.FullName, newest.Length, newest.LastWriteTime);
    }

    public async Task<BackupResult?> EnsureRecentAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        var latest = GetLatest();

        if (latest is not null && DateTime.Now - latest.CreatedAtLocal < maxAge)
        {
            return null;
        }

        try
        {
            var result = await CreateAsync(ct).ConfigureAwait(false);
            Prune();
            return result;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // A backup failure must never stop the till from opening.
            _log.Error("Scheduled backup failed.", ex);
            return null;
        }
    }

    public int Prune(int keep = 30)
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return 0;
        }

        var files = new DirectoryInfo(BackupDirectory)
            .GetFiles($"{FilePrefix}*{FileExtension}")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(keep)
            .ToList();

        var removed = 0;

        foreach (var file in files)
        {
            try
            {
                file.Delete();
                removed++;
            }
            catch (IOException)
            {
                // Locked file (rare) — leave it for the next run.
            }
        }

        return removed;
    }
}
