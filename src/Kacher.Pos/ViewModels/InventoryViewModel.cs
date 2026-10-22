using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Kacher.Pos.Services;
using Kacher.Pos.Services.Repositories;
using Kacher.Pos.ViewModels.Items;

namespace Kacher.Pos.ViewModels;

/// <summary>
/// Stock control: low-stock worklist, manual adjustments and the movement ledger.
/// Every adjustment is written to <c>StockMovements</c> with the operator id, giving a full audit
/// trail that reconciles with sales.
/// </summary>
public sealed partial class InventoryViewModel : ViewModelBase
{
    private readonly IProductRepository _products;
    private readonly ISessionService _session;
    private readonly IDialogService _dialogs;

    public InventoryViewModel(
        IProductRepository products,
        ISessionService session,
        IDialogService dialogs,
        IUserNotifier notifier) : base(notifier)
    {
        _products = products;
        _session = session;
        _dialogs = dialogs;
        Title = "Inventory";
    }

    public ObservableCollection<ProductRowViewModel> Items { get; } = new();

    public ObservableCollection<StockMovementRowViewModel> Movements { get; } = new();

    [ObservableProperty]
    private ProductRowViewModel? _selectedItem;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _lowStockOnly = true;

    [ObservableProperty]
    private int _lowStockCount;

    [ObservableProperty]
    private decimal _stockValue;

    public bool CanAdjust => _session.HasPermission(Permissions.AdjustStock);

    public override Task LoadAsync(CancellationToken ct = default) => RefreshAsync();

    partial void OnSearchTextChanged(string value) => _ = RefreshAsync();

    partial void OnLowStockOnlyChanged(bool value) => _ = RefreshAsync();

    partial void OnSelectedItemChanged(ProductRowViewModel? value)
    {
        Movements.Clear();

        if (value is not null)
        {
            _ = LoadMovementsAsync(value.Id);
        }
    }

    private async Task LoadMovementsAsync(int productId)
    {
        var movements = await _products.GetStockMovementsAsync(productId, 40).ConfigureAwait(true);
        Movements.Clear();
        foreach (var movement in movements)
        {
            Movements.Add(new StockMovementRowViewModel(movement));
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(_ => RefreshCoreAsync(), "Could not load inventory.");

    /// <summary>Unguarded refresh, callable from inside another <see cref="ViewModelBase.RunAsync"/> block.</summary>
    private async Task RefreshCoreAsync()
    {
        var rows = await _products
            .GetCatalogueAsync(null, SearchText, LowStockOnly, CancellationToken.None)
            .ConfigureAwait(true);

        Items.Clear();
        foreach (var product in rows)
        {
            Items.Add(new ProductRowViewModel(product));
        }

        var all = await _products
            .GetCatalogueAsync(null, null, false, CancellationToken.None)
            .ConfigureAwait(true);

        LowStockCount = all.Count(p => p.StockQuantity <= p.ReorderLevel);

        // Stock value at cost — the figure the accountant asks for.
        StockValue = all.Sum(p => p.CostPrice * p.StockQuantity);
    }

    [RelayCommand]
    private async Task ReceiveStockAsync(ProductRowViewModel? row)
    {
        if (!await GuardAsync().ConfigureAwait(true))
        {
            return;
        }

        var target = row ?? SelectedItem;

        if (target is null)
        {
            Notifier.Warn("Select a product first.");
            return;
        }

        var input = await _dialogs
            .PromptAmountAsync($"Receive stock — {target.Name}", "Quantity received", 1m, "Add")
            .ConfigureAwait(true);

        if (input is null || !TryParseQuantity(input, out var quantity) || quantity <= 0)
        {
            return;
        }

        await AdjustAsync(target.Id, quantity, StockMovementReason.GoodsReceived, "Goods received").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AdjustStockAsync(ProductRowViewModel? row)
    {
        if (!await GuardAsync().ConfigureAwait(true))
        {
            return;
        }

        var target = row ?? SelectedItem;

        if (target is null)
        {
            Notifier.Warn("Select a product first.");
            return;
        }

        var input = await _dialogs.PromptAmountAsync(
            $"Stock adjustment — {target.Name}",
            "Signed quantity (+ receive, − write-off)",
            target.StockQuantity,
            "Apply").ConfigureAwait(true);

        if (input is null || !TryParseQuantity(input, out var newQuantity))
        {
            return;
        }

        var delta = newQuantity - target.StockQuantity;

        if (delta == 0)
        {
            return;
        }

        await AdjustAsync(target.Id, delta, StockMovementReason.ManualAdjustment, "Manual stock take").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task WriteOffAsync(ProductRowViewModel? row)
    {
        if (!await GuardAsync().ConfigureAwait(true))
        {
            return;
        }

        var target = row ?? SelectedItem;

        if (target is null)
        {
            return;
        }

        var input = await _dialogs
            .PromptAmountAsync($"Write-off — {target.Name}", "Quantity lost / damaged", 1m, "Write off")
            .ConfigureAwait(true);

        if (input is null || !TryParseQuantity(input, out var quantity) || quantity <= 0)
        {
            return;
        }

        await AdjustAsync(target.Id, -quantity, StockMovementReason.Wastage, "Damaged / expired").ConfigureAwait(true);
    }

    private async Task AdjustAsync(int productId, decimal delta, StockMovementReason reason, string note)
    {
        await RunAsync(async ct =>
        {
            await _products.AdjustStockAsync(productId, delta, reason, _session.CurrentUser?.Id, note, ct)
                .ConfigureAwait(true);

            Notifier.Success($"Stock {(delta > 0 ? "increased" : "reduced")} by {Math.Abs(delta):0.###}.");
            await RefreshCoreAsync().ConfigureAwait(true);

            if (SelectedItem?.Id == productId)
            {
                await LoadMovementsAsync(productId).ConfigureAwait(true);
            }
        }, "Stock adjustment failed.").ConfigureAwait(true);
    }

    /// <summary>Dialog amounts are normalised to invariant culture, so parse them the same way.</summary>
    private static bool TryParseQuantity(string text, out decimal value) =>
        decimal.TryParse(
            text.Replace(',', '.'),
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);

    private async Task<bool> GuardAsync()
    {
        if (CanAdjust)
        {
            return true;
        }

        Notifier.Warn("Your role cannot adjust stock. Ask a supervisor.");
        await Task.CompletedTask;
        return false;
    }
}
