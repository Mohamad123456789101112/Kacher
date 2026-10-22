using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Kacher.Pos.Models;

namespace Kacher.Pos.ViewModels.Items;

/// <summary>
/// Editable copy of a product used by the inline editor panel. Numeric fields are held as strings so
/// partial input ("12.") does not fight the parser; <see cref="TryBuild"/> is the single validation
/// gate and is called only on save.
/// </summary>
public sealed partial class ProductEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _barcode = string.Empty;
    [ObservableProperty] private string _sku = string.Empty;
    [ObservableProperty] private int? _categoryId;
    [ObservableProperty] private string _sellingPriceText = "0.00";
    [ObservableProperty] private string _costPriceText = "0.00";
    [ObservableProperty] private string _stockText = "0";
    [ObservableProperty] private string _taxRatePercentText = "14";
    [ObservableProperty] private string _reorderLevelText = "10";
    [ObservableProperty] private string? _imagePath;
    [ObservableProperty] private string? _colorHex = "#0F766E";
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private bool _isWeighted;
    [ObservableProperty] private string? _error;

    public int Id { get; private set; }

    public bool IsNew => Id == 0;

    public string Header => IsNew ? "New product" : $"Edit — {Name}";

    public static ProductEditorViewModel FromProduct(Product product) => new()
    {
        Id = product.Id,
        Name = product.Name,
        Barcode = product.Barcode,
        Sku = product.Sku ?? string.Empty,
        CategoryId = product.CategoryId,
        SellingPriceText = product.SellingPrice.ToString("0.00", CultureInfo.InvariantCulture),
        CostPriceText = product.CostPrice.ToString("0.00", CultureInfo.InvariantCulture),
        StockText = product.StockQuantity.ToString("0.###", CultureInfo.InvariantCulture),
        TaxRatePercentText = (product.TaxRate * 100m).ToString("0.##", CultureInfo.InvariantCulture),
        ReorderLevelText = product.ReorderLevel.ToString("0.###", CultureInfo.InvariantCulture),
        ImagePath = product.ImagePath,
        ColorHex = product.ColorHex,
        IsActive = product.IsActive,
        IsWeighted = product.IsWeighted
    };

    /// <summary>Validates and materialises the entity. Returns null and sets <see cref="Error"/> on failure.</summary>
    public Product? TryBuild()
    {
        Error = null;

        if (string.IsNullOrWhiteSpace(Name))
        {
            Error = "Product name is required.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(Barcode))
        {
            Error = "A barcode is required for scanning.";
            return null;
        }

        if (!decimal.TryParse(SellingPriceText.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var price) || price < 0)
        {
            Error = "Selling price must be a non-negative number.";
            return null;
        }

        if (!decimal.TryParse(CostPriceText.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var cost) || cost < 0)
        {
            Error = "Cost price must be a non-negative number.";
            return null;
        }

        if (!decimal.TryParse(StockText.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var stock))
        {
            Error = "Stock must be a number.";
            return null;
        }

        if (!decimal.TryParse(TaxRatePercentText.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var taxPercent)
            || taxPercent is < 0 or > 100)
        {
            Error = "Tax rate must be between 0 and 100 %.";
            return null;
        }

        _ = decimal.TryParse(ReorderLevelText.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var reorder);

        if (cost > price)
        {
            // Warning only — loss-leader pricing is a legitimate retail decision.
            Error = null;
        }

        return new Product
        {
            Id = Id,
            Name = Name.Trim(),
            Barcode = Barcode.Trim(),
            Sku = string.IsNullOrWhiteSpace(Sku) ? null : Sku.Trim().ToUpperInvariant(),
            CategoryId = CategoryId,
            SellingPrice = Math.Round(price, 2),
            CostPrice = Math.Round(cost, 2),
            StockQuantity = stock,
            TaxRate = Math.Round(taxPercent / 100m, 4),
            ReorderLevel = reorder,
            ImagePath = string.IsNullOrWhiteSpace(ImagePath) ? null : ImagePath.Trim(),
            ColorHex = ColorHex,
            IsActive = IsActive,
            IsWeighted = IsWeighted
        };
    }
}
