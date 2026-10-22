using Kacher.Pos.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kacher.Pos.Data;

// ────────────────────────────────────────────────────────────────────────────────────────────────
// Fluent configuration. Kept in one file so the whole physical schema is reviewable at a glance.
// The indexes below are the ones that matter for till latency:
//   UX_Products_Barcode      → every scanner keystroke burst ends in one index seek
//   IX_Products_CategoryId   → category tab switches
//   IX_Invoices_CreatedAt    → reporting windows
//   IX_Invoices_ShiftId      → shift close / Z-report
//   IX_InvoiceItems_InvoiceId→ receipt re-print and invoice detail
// ────────────────────────────────────────────────────────────────────────────────────────────────

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> e)
    {
        e.ToTable("Products");
        e.HasKey(p => p.Id);

        e.Property(p => p.Barcode).IsRequired().HasMaxLength(64);
        e.HasIndex(p => p.Barcode).IsUnique().HasDatabaseName("UX_Products_Barcode");
        e.HasIndex(p => p.Sku);
        e.HasIndex(p => p.CategoryId);
        e.HasIndex(p => new { p.IsActive, p.Name });

        e.Property(p => p.Name).IsRequired().HasMaxLength(160);
        e.Property(p => p.SellingPrice).HasPrecision(18, 2);
        e.Property(p => p.CostPrice).HasPrecision(18, 2);
        e.Property(p => p.StockQuantity);
        e.Property(p => p.TaxRate);

        e.HasOne(p => p.Category)
            .WithMany(c => c.Products)
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        e.Ignore(p => p.IsLowStock);
    }
}

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> e)
    {
        e.ToTable("Categories");
        e.HasKey(c => c.Id);
        e.Property(c => c.Name).IsRequired().HasMaxLength(64);
        e.HasIndex(c => c.Name).IsUnique();
        e.HasIndex(c => c.SortOrder);
    }
}

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> e)
    {
        e.ToTable("Invoices");
        e.HasKey(i => i.Id);

        e.Property(i => i.InvoiceNumber).IsRequired().HasMaxLength(32);
        e.HasIndex(i => i.InvoiceNumber).IsUnique();
        e.HasIndex(i => i.CreatedAtUtc);
        e.HasIndex(i => new { i.ShiftId, i.Status });
        e.HasIndex(i => new { i.CreatedAtUtc, i.Status });

        e.Property(i => i.Subtotal).HasPrecision(18, 2);
        e.Property(i => i.DiscountAmount).HasPrecision(18, 2);
        e.Property(i => i.TaxAmount).HasPrecision(18, 2);
        e.Property(i => i.TotalAmount).HasPrecision(18, 2);
        e.Property(i => i.AmountTendered).HasPrecision(18, 2);
        e.Property(i => i.ChangeGiven).HasPrecision(18, 2);

        e.HasOne(i => i.Cashier)
            .WithMany(u => u.Invoices)
            .HasForeignKey(i => i.CashierId)
            .OnDelete(DeleteBehavior.Restrict);

        e.HasOne(i => i.Shift)
            .WithMany(s => s.Invoices)
            .HasForeignKey(i => i.ShiftId)
            .OnDelete(DeleteBehavior.SetNull);

        e.HasMany(i => i.Items)
            .WithOne(x => x.Invoice!)
            .HasForeignKey(x => x.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        e.HasMany(i => i.Payments)
            .WithOne(p => p.Invoice!)
            .HasForeignKey(p => p.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class InvoiceItemConfiguration : IEntityTypeConfiguration<InvoiceItem>
{
    public void Configure(EntityTypeBuilder<InvoiceItem> e)
    {
        e.ToTable("InvoiceItems");
        e.HasKey(x => x.Id);
        e.HasIndex(x => x.InvoiceId);
        e.HasIndex(x => x.ProductId);

        e.Property(x => x.ProductName).IsRequired().HasMaxLength(160);
        e.Property(x => x.Barcode).HasMaxLength(64);
        e.Property(x => x.UnitPrice).HasPrecision(18, 2);
        e.Property(x => x.TotalPrice).HasPrecision(18, 2);
        e.Property(x => x.DiscountAmount).HasPrecision(18, 2);
        e.Property(x => x.TaxAmount).HasPrecision(18, 2);

        e.HasOne(x => x.Product)
            .WithMany(p => p.InvoiceItems)
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> e)
    {
        e.ToTable("Users");
        e.HasKey(u => u.Id);
        e.Property(u => u.DisplayName).IsRequired().HasMaxLength(64);
        e.HasIndex(u => u.UserName).IsUnique();
        e.HasIndex(u => new { u.IsActive, u.Role });
    }
}

internal sealed class ShiftConfiguration : IEntityTypeConfiguration<Shift>
{
    public void Configure(EntityTypeBuilder<Shift> e)
    {
        e.ToTable("Shifts");
        e.HasKey(s => s.Id);
        e.Property(s => s.ShiftNumber).IsRequired().HasMaxLength(32);
        e.HasIndex(s => s.ShiftNumber).IsUnique();
        e.HasIndex(s => new { s.Status, s.OpenedAtUtc });

        e.HasMany(s => s.CashMovements)
            .WithOne(m => m.Shift!)
            .HasForeignKey(m => m.ShiftId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class HardwareConfigConfiguration : IEntityTypeConfiguration<HardwareConfig>
{
    public void Configure(EntityTypeBuilder<HardwareConfig> e)
    {
        e.ToTable("HardwareConfigs");
        e.HasKey(h => h.Id);
        e.Property(h => h.ProfileName).IsRequired().HasMaxLength(128);
        e.HasIndex(h => h.IsActive);
    }
}

internal sealed class AppSettingConfiguration : IEntityTypeConfiguration<AppSetting>
{
    public void Configure(EntityTypeBuilder<AppSetting> e)
    {
        e.ToTable("AppSettings");
        e.HasKey(s => s.Key);
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> e)
    {
        e.ToTable("AuditLogs");
        e.HasKey(a => a.Id);
        e.HasIndex(a => a.CreatedAtUtc);
    }
}

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> e)
    {
        e.ToTable("StockMovements");
        e.HasKey(s => s.Id);
        e.HasIndex(s => new { s.ProductId, s.CreatedAtUtc });
        e.HasOne(s => s.Product)
            .WithMany(p => p.StockMovements)
            .HasForeignKey(s => s.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
