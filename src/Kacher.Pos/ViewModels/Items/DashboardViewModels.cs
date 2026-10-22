using Kacher.Pos.Models;

namespace Kacher.Pos.ViewModels.Items;

/// <summary>One KPI card on the dashboard.</summary>
public sealed class KpiCardViewModel
{
    public KpiCardViewModel(string label, string value, string caption, string iconKey, string toneHex = "#14B8A6")
    {
        Label = label;
        Value = value;
        Caption = caption;
        IconKey = iconKey;
        ToneHex = toneHex;
    }

    public string Label { get; }

    /// <summary>Pre-formatted so the view never has to guess a culture or a currency suffix.</summary>
    public string Value { get; }

    public string Caption { get; }

    /// <summary>Key into Themes/Icons.xaml.</summary>
    public string IconKey { get; }

    /// <summary>Accent colour for the icon badge (hex, converted by HexBrush).</summary>
    public string ToneHex { get; }
}

/// <summary>A single alert row ("3 products below reorder level", "drawer not reconciled").</summary>
public sealed class AttentionItemViewModel
{
    public AttentionItemViewModel(string title, string detail, string iconKey, string toneHex, string actionKey, string actionLabel)
    {
        Title = title;
        Detail = detail;
        IconKey = iconKey;
        ToneHex = toneHex;
        ActionKey = actionKey;
        ActionLabel = actionLabel;
    }

    public string Title { get; }
    public string Detail { get; }
    public string IconKey { get; }
    public string ToneHex { get; }

    /// <summary>View key the shell should navigate to when the row is tapped.</summary>
    public string ActionKey { get; }

    public string ActionLabel { get; }
}

/// <summary>A product tile in the "top sellers" strip.</summary>
public sealed class TopSellerViewModel
{
    public TopSellerViewModel(TopProduct product, int rank, decimal maxRevenue)
    {
        Rank = rank;
        Name = product.Name;
        Quantity = product.Quantity;
        Revenue = product.Revenue;
        Ratio = maxRevenue <= 0 ? 0 : (double)(product.Revenue / maxRevenue);
        Initial = string.IsNullOrWhiteSpace(product.Name) ? "?" : product.Name[..1].ToUpperInvariant();
    }

    public int Rank { get; }
    public string Name { get; }
    public decimal Quantity { get; }
    public decimal Revenue { get; }
    public double Ratio { get; }
    public string Initial { get; }
    public string QuantityText => $"{Quantity:0.##} sold";
    public string RevenueText => $"{Revenue:0.00} EGP";
}
