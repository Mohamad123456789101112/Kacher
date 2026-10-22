using Kacher.Pos.Data;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Microsoft.EntityFrameworkCore;

namespace Kacher.Pos.Services.Repositories;

public interface IUserRepository
{
    Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Verifies a PIN. Deliberately does not say *which* check failed — the caller shows a single
    /// generic message so the keypad cannot be used to enumerate valid PINs.
    /// </summary>
    Task<User?> AuthenticateAsync(string pin, CancellationToken ct = default);

    Task<(string Hash, string Salt)> CreateAsync(string displayName, string userName, string pin, UserRole role, CancellationToken ct = default);

    Task ChangePinAsync(int userId, string newPin, CancellationToken ct = default);

    Task SetActiveAsync(int userId, bool active, CancellationToken ct = default);
}

public sealed class UserRepository : IUserRepository
{
    private readonly IDbContextFactory<KacherDbContext> _factory;
    private readonly IAppLog _log;

    public UserRepository(IDbContextFactory<KacherDbContext> factory, IAppLog log)
    {
        _factory = factory;
        _log = log;
    }

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<User?> AuthenticateAsync(string pin, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return null;
        }

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // PBKDF2 is intentionally slow (~60-100 ms at 210k iterations). Run the whole scan off the
        // UI thread; with < 50 operators the linear scan is cheaper than maintaining a PIN index.
        var candidates = await db.Users.Where(u => u.IsActive).ToListAsync(ct).ConfigureAwait(false);

        foreach (var user in candidates)
        {
            if (!PinHasher.Verify(pin, user.PinHash, user.PinSalt))
            {
                continue;
            }

            user.LastLoginUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _log.Info($"User '{user.UserName}' signed in as {user.Role}.");
            return user;
        }

        _log.Warn("Failed PIN sign-in attempt.");
        return null;
    }

    public async Task<(string Hash, string Salt)> CreateAsync(
        string displayName, string userName, string pin, UserRole role, CancellationToken ct = default)
    {
        var (hash, salt) = PinHasher.Hash(pin);

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Users.Add(new User
        {
            DisplayName = displayName,
            UserName = userName,
            PinHash = hash,
            PinSalt = salt,
            Role = role,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (hash, salt);
    }

    public async Task ChangePinAsync(int userId, string newPin, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("User not found.");

        var (hash, salt) = PinHasher.Hash(newPin);
        user.PinHash = hash;
        user.PinSalt = salt;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _log.Info($"PIN changed for '{user.UserName}'.");
    }

    public async Task SetActiveAsync(int userId, bool active, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("User not found.");
        user.IsActive = active;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
