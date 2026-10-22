using Kacher.Pos.Models;
using Microsoft.EntityFrameworkCore;

namespace Kacher.Pos.Data;

/// <summary>
/// The single EF Core context for the terminal. Registered as <c>Scoped</c> in DI.
///
/// SQLite performance notes (why this is fast under load):
/// * WAL journalling lets reads continue while a sale is committed → no UI stalls at the till.
/// * <c>synchronous=NORMAL</c> is durable under WAL and roughly 3-5x faster than FULL for commits.
/// * <c>cache_size=-20000</c> keeps a 20 MB page cache resident; the catalogue is tiny so lookups
///   become memory reads after warm-up.
/// * <c>temp_store=MEMORY</c> speeds up the reporting GROUP BYs.
/// The connection string is supplied by <see cref="DatabaseConnectionFactory"/>.
/// </summary>
public sealed class KacherDbContext : DbContext
{
    public KacherDbContext(DbContextOptions<KacherDbContext> options) : base(options) { }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceItem> InvoiceItems => Set<InvoiceItem>();
    public DbSet<InvoicePayment> InvoicePayments => Set<InvoicePayment>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<HardwareConfig> HardwareConfigs => Set<HardwareConfig>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.ApplyConfigurationsFromAssembly(typeof(KacherDbContext).Assembly);
    }

    /// <summary>SQLite has no native decimal type; store as TEXT to preserve exact scale.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<decimal>().HaveColumnType("TEXT");
        builder.Properties<DateTime>().HaveColumnType("TEXT");
    }
}
