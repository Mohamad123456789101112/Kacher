using CommunityToolkit.Mvvm.ComponentModel;
using Kacher.Pos.Models;

namespace Kacher.Pos.ViewModels.Items;

/// <summary>
/// Display projection of a <see cref="Product"/> for the touch catalogue grid.
/// Everything the tile binds to is computed once at construction (no converters on the hot path —
/// a 60-tile grid rebinding on every keystroke is exactly where WPF frame drops appear).
/// </summary>
public sealed partial class ProductTileViewModel : ObservableObject
{
    private readonly Product _product;

    public ProductTileViewModel(Product product)
    {
        _product = product;
        ColorHex = string.IsNullOrWhiteSpace(product.ColorHex)
            ? product.Category?.ColorHex ?? "#334155"
            : product.ColorHex;
    }

    public int Id => _product.Id;

    public Product Model => _product;

    public string Name => _product.Name;

    public string Barcode => _product.Barcode;

    public string CategoryName => _product.Category?.Name ?? "Uncategorised";

    public decimal Price => _product.SellingPrice;

    public string PriceDisplay => _product.SellingPrice.ToString("0.00");

    public string TaxRateDisplay => _product.TaxRate > 0 ? $"VAT {_product.TaxRate * 100:0.##}%" : "VAT exempt";

    public string? ImagePath => _product.ImagePath;

    public string ColorHex { get; }

    /// <summary>First letter used as the fallback tile glyph when there is no image.</summary>
    public string Initial => string.IsNullOrEmpty(_product.Name) ? "?" : _product.Name[..1].ToUpperInvariant();

    public decimal Stock => _product.StockQuantity;

    public bool IsWeighted => _product.IsWeighted;

    public string StockDisplay => _product.IsWeighted
        ? $"{_product.StockQuantity:0.###} kg"
        : $"{_product.StockQuantity:0} in stock";

    public bool IsLowStock => _product.StockQuantity <= _product.ReorderLevel;

    public bool IsOutOfStock => !_product.IsWeighted && _product.StockQuantity <= 0;

    /// <summary>Out-of-stock tiles stay visible but cannot be added (weighted items always can).</summary>
    public bool CanSell => !IsOutOfStock;
}
