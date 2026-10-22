using System.ComponentModel.DataAnnotations;

namespace Kacher.Pos.Models;

/// <summary>Product grouping used by the catalogue tabs.</summary>
public class Category
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? Description { get; set; }

    /// <summary>Hex colour (#RRGGBB) used by the category tile in the catalogue.</summary>
    [MaxLength(9)]
    public string ColorHex { get; set; } = "#0F766E";

    /// <summary>Material Design icon name (e.g. "Coffee", "BreadSlice").</summary>
    [MaxLength(64)]
    public string IconName { get; set; } = "Tag";

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<Product> Products { get; set; } = new List<Product>();
}

/// <summary>Saleable item. Barcode is uniquely indexed for sub-millisecond scanner lookups.</summary>
public class Product
{
    public int Id { get; set; }

    /// <summary>EAN-13 / UPC / internal code. Unique index, used on every scan.</summary>
    [MaxLength(64)]
    public string Barcode { get; set; } = string.Empty;

    /// <summary>Short alias used by the keyboard scanner fallback (e.g. "COLA").</summary>
    [MaxLength(32)]
    public string? Sku { get; set; }

    [MaxLength(160)]
    public string Name { get; set; } = string.Empty;

    public int? CategoryId { get; set; }

    public Category? Category { get; set; }

    public decimal SellingPrice { get; set; }

    public decimal CostPrice { get; set; }

    public decimal StockQuantity { get; set; }

    /// <summary>Per-product tax rate as a fraction (0.14 = 14 %).</summary>
    public decimal TaxRate { get; set; }

    /// <summary>Relative path under the local image store, e.g. "products/cola.png".</summary>
    [MaxLength(260)]
    public string? ImagePath { get; set; }

    /// <summary>0xRRGGBB tile colour when no image is present — keeps the grid visually rich.</summary>
    [MaxLength(9)]
    public string? ColorHex { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Sold by weight (deli, produce) rather than by piece.</summary>
    public bool IsWeighted { get; set; }

    /// <summary>Low-stock alert threshold.</summary>
    public decimal ReorderLevel { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAtUtc { get; set; }

    public ICollection<InvoiceItem> InvoiceItems { get; set; } = new List<InvoiceItem>();

    public ICollection<StockMovement> StockMovements { get; set; } = new List<StockMovement>();

    public bool IsLowStock => StockQuantity <= ReorderLevel;
}

/// <summary>Append-only ledger of every stock change so inventory can be reconciled by hand.</summary>
public class StockMovement
{
    public int Id { get; set; }

    public int ProductId { get; set; }

    public Product? Product { get; set; }

    /// <summary>Signed delta: negative for sales/wastage, positive for receiving.</summary>
    public decimal QuantityDelta { get; set; }

    public decimal QuantityAfter { get; set; }

    public StockMovementReason Reason { get; set; }

    [MaxLength(256)]
    public string? Note { get; set; }

    public int? UserId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
