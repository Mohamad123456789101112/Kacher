namespace Kacher.Pos.Infrastructure;

/// <summary>
/// Wall-clock abstraction. Services take <see cref="IClock"/> instead of calling DateTime.Now so
/// shift/Z-report logic can be unit-tested around midnight and DST boundaries.
/// </summary>
public interface IClock
{
    DateTime Now { get; }
    DateTime UtcNow { get; }
    DateOnly Today { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime Now => DateTime.Now;
    public DateTime UtcNow => DateTime.UtcNow;
    public DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
}
