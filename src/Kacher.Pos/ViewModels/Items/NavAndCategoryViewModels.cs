using CommunityToolkit.Mvvm.ComponentModel;
using Kacher.Pos.Models;

namespace Kacher.Pos.ViewModels.Items;

/// <summary>Sidebar entry. <see cref="Permission"/> is enforced by the shell when navigating.</summary>
public sealed class NavItemViewModel
{
    public NavItemViewModel(string label, string icon, string viewKey, string? permission = null)
    {
        Label = label;
        Icon = icon;
        ViewKey = viewKey;
        Permission = permission;
    }

    public string Label { get; }

    /// <summary>MaterialDesign PackIcon kind name, e.g. "PointOfSale".</summary>
    public string Icon { get; }

    /// <summary>Stable key resolved by MainViewModel's view-model factory.</summary>
    public string ViewKey { get; }

    public string? Permission { get; }

    /// <summary>Single-letter badge rendered by the navigation rail (no icon-font dependency).</summary>
    public string Badge => string.IsNullOrEmpty(Label) ? "?" : Label[..1].ToUpperInvariant();
}

/// <summary>Catalogue filter chip. Id 0 means "All items".</summary>
public sealed partial class CategoryTabViewModel : ObservableObject
{
    public CategoryTabViewModel(int id, string name, string colorHex, string icon)
    {
        Id = id;
        Name = name;
        ColorHex = colorHex;
        Icon = icon;
    }

    public int Id { get; }

    public string Name { get; }

    public string ColorHex { get; }

    public string Icon { get; }

    /// <summary>Item count badge; refreshed when the catalogue reloads.</summary>
    [ObservableProperty]
    private int _productCount;

    public static CategoryTabViewModel All { get; } = new(0, "All items", "#14B8A6", "ViewGrid");

    public static CategoryTabViewModel FromModel(Category category, int count = 0) =>
        new(category.Id, category.Name, category.ColorHex, category.IconName) { ProductCount = count };
}

/// <summary>Product row for the back-office grid (ProductsView).</summary>
public sealed class ProductRowViewModel
{
    public ProductRowViewModel(Product product)
    {
        Model = product;
        Id = product.Id;
        Name = product.Name;
        Barcode = product.Barcode;
        CategoryName = product.Category?.Name ?? "—";
        SellingPrice = product.SellingPrice;
        CostPrice = product.CostPrice;
        Margin = product.SellingPrice - product.CostPrice;
        StockQuantity = product.StockQuantity;
        ReorderLevel = product.ReorderLevel;
        TaxRate = product.TaxRate;
        IsActive = product.IsActive;
        IsLowStock = product.StockQuantity <= product.ReorderLevel;
    }

    public Product Model { get; }
    public int Id { get; }
    public string Name { get; }
    public string Barcode { get; }
    public string CategoryName { get; }
    public decimal SellingPrice { get; }
    public decimal CostPrice { get; }
    public decimal Margin { get; }
    public decimal StockQuantity { get; }
    public decimal ReorderLevel { get; }
    public decimal TaxRate { get; }
    public bool IsActive { get; }
    public bool IsLowStock { get; }
    public string TaxRateDisplay => TaxRate > 0 ? $"{TaxRate * 100:0.##}%" : "—";
}

/// <summary>Invoice row for the reports/history list.</summary>
public sealed class InvoiceRowViewModel
{
    public InvoiceRowViewModel(InvoiceRow row)
    {
        Id = row.Id;
        InvoiceNumber = row.InvoiceNumber;
        CreatedAt = row.CreatedAt.ToLocalTime();
        TotalAmount = row.TotalAmount;
        PaymentMethod = row.PaymentMethod;
        Status = row.Status;
        ItemCount = row.ItemCount;
        CashierName = row.CashierName ?? "—";
    }

    public int Id { get; }
    public string InvoiceNumber { get; }
    public DateTime CreatedAt { get; }
    public decimal TotalAmount { get; }
    public PaymentMethod PaymentMethod { get; }
    public InvoiceStatus Status { get; }
    public int ItemCount { get; }
    public string CashierName { get; }
    public string StatusText => Status.ToString();
    public bool IsRefunded => Status == InvoiceStatus.Refunded;
}

/// <summary>Stock movement row shown in the inventory detail panel.</summary>
public sealed class StockMovementRowViewModel
{
    public StockMovementRowViewModel(StockMovement movement)
    {
        CreatedAt = movement.CreatedAtUtc.ToLocalTime();
        Reason = movement.Reason.ToString();
        Delta = movement.QuantityDelta;
        QuantityAfter = movement.QuantityAfter;
        Note = movement.Note ?? string.Empty;
    }

    public DateTime CreatedAt { get; }
    public string Reason { get; }
    public decimal Delta { get; }
    public decimal QuantityAfter { get; }
    public string Note { get; }
    public bool IsIncrease => Delta >= 0;
}
