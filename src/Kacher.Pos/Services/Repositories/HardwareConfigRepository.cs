using Kacher.Pos.Data;
using Kacher.Pos.Models;
using Microsoft.EntityFrameworkCore;

namespace Kacher.Pos.Services.Repositories;

public interface IHardwareConfigRepository
{
    Task<HardwareConfig> GetActiveAsync(CancellationToken ct = default);

    Task<IReadOnlyList<HardwareConfig>> GetAllAsync(CancellationToken ct = default);

    Task SaveAsync(HardwareConfig config, CancellationToken ct = default);

    Task ActivateAsync(int id, CancellationToken ct = default);
}

public sealed class HardwareConfigRepository : IHardwareConfigRepository
{
    private readonly IDbContextFactory<KacherDbContext> _factory;

    /// <summary>In-process cache: hardware settings are read on every receipt print.</summary>
    private HardwareConfig? _cached;

    public HardwareConfigRepository(IDbContextFactory<KacherDbContext> factory) => _factory = factory;

    public async Task<HardwareConfig> GetActiveAsync(CancellationToken ct = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var config = await db.HardwareConfigs
            .AsNoTracking()
            .Where(h => h.IsActive)
            .OrderByDescending(h => h.UpdatedAtUtc)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // Defensive: a corrupted/deleted config must never stop the till from selling.
        config ??= new HardwareConfig();
        _cached = config;
        return config;
    }

    public async Task<IReadOnlyList<HardwareConfig>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.HardwareConfigs.AsNoTracking().OrderBy(h => h.ProfileName).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveAsync(HardwareConfig config, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        config.UpdatedAtUtc = DateTime.UtcNow;

        if (config.Id == 0)
        {
            db.HardwareConfigs.Add(config);
        }
        else
        {
            db.HardwareConfigs.Update(config);
        }

        // Exactly one active profile at a time.
        if (config.IsActive)
        {
            var others = await db.HardwareConfigs
                .Where(h => h.Id != config.Id && h.IsActive)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var other in others)
            {
                other.IsActive = false;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _cached = config;
    }

    public async Task ActivateAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var all = await db.HardwareConfigs.ToListAsync(ct).ConfigureAwait(false);
        foreach (var config in all)
        {
            config.IsActive = config.Id == id;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _cached = all.FirstOrDefault(c => c.Id == id);
    }
}
