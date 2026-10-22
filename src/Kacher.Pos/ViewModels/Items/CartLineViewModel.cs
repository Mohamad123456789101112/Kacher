using CommunityToolkit.Mvvm.ComponentModel;
using Kacher.Pos.Models;
using Kacher.Pos.Services;

namespace Kacher.Pos.ViewModels.Items;

/// <summary>
/// One row of the till cart. Holds the snapshot values (price and tax rate) so a mid-sale catalogue
/// edit by a manager cannot change what the customer is about to be charged.
///
/// Derived money properties are raised explicitly on every mutation: <see cref="Quantity"/> and
/// <see cref="DiscountAmount"/> are the only writable inputs, and <see cref="Recalculate"/> guarantees
/// the cart summary and the UI agree to the cent.
/// </summary>
public sealed partial class CartLineViewModel : ObservableObject
{
    public CartLineViewModel(Product product, decimal quantity = 1)
    {
        ProductId = product.Id;
        Name = product.Name;
        Barcode = product.Barcode;
        UnitPrice = product.SellingPrice;
        TaxRate = product.TaxRate;
        IsWeighted = product.IsWeighted;
        ColorHex = string.IsNullOrWhiteSpace(product.ColorHex)
            ? product.Category?.ColorHex ?? "#334155"
            : product.ColorHex;
        ImagePath = product.ImagePath;
        _quantity = quantity;
        Recalculate();
    }

    public int? ProductId { get; }

    public string Name { get; }

    public string Barcode { get; }

    public decimal UnitPrice { get; }

    public decimal TaxRate { get; }

    public bool IsWeighted { get; }

    public string? ImagePath { get; }

    public string ColorHex { get; }

    /// <summary>Letter shown in the cart's colour chip (no image dependency).</summary>
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();

    /// <summary>Non-zero when a cashier applied a manual line discount.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotal))]
    [NotifyPropertyChangedFor(nameof(DiscountDisplay))]
    private decimal _discountAmount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotal))]
    [NotifyPropertyChangedFor(nameof(QuantityDisplay))]
    [NotifyPropertyChangedFor(nameof(GrossAmount))]
    private decimal _quantity;

    /// <summary>Line gross after discount, before tax.</summary>
    public decimal GrossAmount => Math.Round((Quantity * UnitPrice) - DiscountAmount, 2, MidpointRounding.AwayFromZero);

    public decimal TaxAmount => Math.Round(GrossAmount * TaxRate, 2, MidpointRounding.AwayFromZero);

    /// <summary>What the customer pays for this line (tax inclusive).</summary>
    public decimal LineTotal => GrossAmount + TaxAmount;

    public string QuantityDisplay => IsWeighted
        ? $"{Quantity:0.###} kg"
        : Quantity.ToString("0.##");

    public string UnitPriceDisplay => UnitPrice.ToString("0.00");

    public string LineTotalDisplay => LineTotal.ToString("0.00");

    public string DiscountDisplay => DiscountAmount > 0 ? $"-{DiscountAmount:0.00}" : string.Empty;

    /// <summary>Keeps the cart total in sync when quantity/discount change from the touch buttons.</summary>
    public void Recalculate()
    {
        OnPropertyChanged(nameof(GrossAmount));
        OnPropertyChanged(nameof(TaxAmount));
        OnPropertyChanged(nameof(LineTotal));
        OnPropertyChanged(nameof(LineTotalDisplay));
        OnPropertyChanged(nameof(QuantityDisplay));
    }

    public SaleLineRequest ToRequest() =>
        new(ProductId, Name, Barcode, Quantity, UnitPrice, DiscountAmount, TaxRate);
}
