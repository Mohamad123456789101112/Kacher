using Kacher.Pos.Data;
using Kacher.Pos.Models;
using Microsoft.EntityFrameworkCore;

namespace Kacher.Pos.Services;

public interface IAppSettingsService
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string? value, CancellationToken ct = default);

    Task<bool> GetBoolAsync(string key, bool fallback = false, CancellationToken ct = default);

    Task<decimal> GetDecimalAsync(string key, decimal fallback = 0m, CancellationToken ct = default);

    /// <summary>Synchronous read from the in-memory snapshot, for binding-heavy paths.</summary>
    string? Peek(string key);

    Task LoadAsync(CancellationToken ct = default);
}

/// <summary>
/// Key/value settings with an in-memory snapshot. The snapshot is loaded once at startup so
/// theme/currency lookups never hit SQLite (and therefore never block a binding).
/// </summary>
public sealed class AppSettingsService : IAppSettingsService
{
    private readonly IDbContextFactory<KacherDbContext> _factory;
    private readonly Dictionary<string, string?> _snapshot = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettingsService(IDbContextFactory<KacherDbContext> factory) => _factory = factory;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await db.AppSettings.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

            _snapshot.Clear();
            foreach (var row in rows)
            {
                _snapshot[row.Key] = row.Value;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? Peek(string key) => _snapshot.TryGetValue(key, out var value) ? value : null;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
            return row?.Value;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);

            if (row is null)
            {
                db.AppSettings.Add(new AppSetting { Key = key, Value = value });
            }
            else
            {
                row.Value = value;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _snapshot[key] = value;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> GetBoolAsync(string key, bool fallback = false, CancellationToken ct = default)
    {
        var raw = Peek(key) ?? await GetAsync(key, ct).ConfigureAwait(false);
        return bool.TryParse(raw, out var parsed) ? parsed : fallback;
    }

    public async Task<decimal> GetDecimalAsync(string key, decimal fallback = 0m, CancellationToken ct = default)
    {
        var raw = Peek(key) ?? await GetAsync(key, ct).ConfigureAwait(false);
        return decimal.TryParse(raw, out var parsed) ? parsed : fallback;
    }
}
