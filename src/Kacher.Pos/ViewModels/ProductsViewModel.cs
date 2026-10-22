using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Kacher.Pos.Services;
using Kacher.Pos.Services.Repositories;
using Kacher.Pos.ViewModels.Items;

namespace Kacher.Pos.ViewModels;

/// <summary>
/// Catalogue administration: filter, edit inline, create and soft-delete products.
/// The list is projected (never bound to EF entities) so the grid has no change-tracking overhead.
/// </summary>
public sealed partial class ProductsViewModel : ViewModelBase
{
    private readonly IProductRepository _products;
    private readonly ISessionService _session;
    private readonly IDialogService _dialogs;

    public ProductsViewModel(
        IProductRepository products,
        ISessionService session,
        IDialogService dialogs,
        IUserNotifier notifier) : base(notifier)
    {
        _products = products;
        _session = session;
        _dialogs = dialogs;

        Title = "Products";
    }

    public ObservableCollection<ProductRowViewModel> Products { get; } = new();

    public ObservableCollection<Category> Categories { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private Category? _selectedCategory;

    [ObservableProperty]
    private bool _lowStockOnly;

    [ObservableProperty]
    private ProductRowViewModel? _selectedRow;

    [ObservableProperty]
    private ProductEditorViewModel? _editor;

    [ObservableProperty]
    private bool _isEditorOpen;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _lowStockCount;

    public bool CanEditProducts => _session.HasPermission(Permissions.EditProducts);

    public override async Task LoadAsync(CancellationToken ct = default)
    {
        await RunAsync(async token =>
        {
            var categories = await _products.GetCategoriesAsync(token).ConfigureAwait(true);
            Categories.Clear();
            foreach (var category in categories)
            {
                Categories.Add(category);
            }

            var all = await _products.GetCatalogueAsync(null, null, false, token).ConfigureAwait(true);
            TotalCount = all.Count;
            LowStockCount = all.Count(p => p.StockQuantity <= p.ReorderLevel);
        }, "Could not load products.").ConfigureAwait(true);
    }

    partial void OnSearchTextChanged(string value) => _ = RefreshAsync();

    partial void OnLowStockOnlyChanged(bool value) => _ = RefreshAsync();

    partial void OnSelectedCategoryChanged(Category? value) => _ = RefreshAsync();

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(_ => RefreshCoreAsync(), "Could not refresh products.");

    /// <summary>
    /// The actual query. Kept separate from <see cref="RefreshAsync"/> because <see cref="RunAsync"/>
    /// ignores re-entrant calls while busy — commands that are already inside a guarded block must
    /// call this directly.
    /// </summary>
    private async Task RefreshCoreAsync()
    {
        var rows = await _products
            .GetCatalogueAsync(SelectedCategory?.Id, SearchText, LowStockOnly, CancellationToken.None)
            .ConfigureAwait(true);

        Products.Clear();
        foreach (var product in rows)
        {
            Products.Add(new ProductRowViewModel(product));
        }
    }

    [RelayCommand]
    private void NewProduct()
    {
        if (!CanEditProducts)
        {
            Notifier.Warn("Your role cannot edit products.");
            return;
        }

        Editor = new ProductEditorViewModel
        {
            CategoryId = SelectedCategory?.Id,
            Barcode = GenerateBarcode()
        };

        IsEditorOpen = true;
    }

    [RelayCommand]
    private async Task EditProductAsync(ProductRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!CanEditProducts)
        {
            Notifier.Warn("Your role cannot edit products.");
            return;
        }

        // Re-read from the store: the grid row may be minutes old.
        var fresh = await _products.GetByIdAsync(row.Id).ConfigureAwait(true);

        if (fresh is null)
        {
            Notifier.Warn("That product no longer exists.");
            return;
        }

        Editor = ProductEditorViewModel.FromProduct(fresh);
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        Editor = null;
        IsEditorOpen = false;
    }

    [RelayCommand]
    private async Task SaveEditorAsync()
    {
        var editor = Editor;

        if (editor is null)
        {
            return;
        }

        var product = editor.TryBuild();

        if (product is null)
        {
            Notifier.Warn(editor.Error ?? "Please review the form.");
            return;
        }

        await RunAsync(async ct =>
        {
            await _products.SaveAsync(product, ct).ConfigureAwait(true);

            // Stock entered on creation should be auditable, not silently back-dated.
            if (editor.IsNew && product.StockQuantity > 0 && product.Id > 0)
            {
                await _products.AdjustStockAsync(
                    product.Id, 0m, StockMovementReason.GoodsReceived,
                    _session.CurrentUser?.Id, "Opening stock (product created)", ct).ConfigureAwait(true);
            }

            Notifier.Success(editor.IsNew ? "Product created." : "Product saved.");
            IsEditorOpen = false;
            Editor = null;
            await RefreshCoreAsync().ConfigureAwait(true);
        }, "Could not save the product.").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteProductAsync(ProductRowViewModel? row)
    {
        if (row is null || !CanEditProducts)
        {
            return;
        }

        var confirmed = await ConfirmAsync($"Retire {row.Name}?",
            "The product is hidden from the till but kept on historical invoices.").ConfigureAwait(true);

        if (!confirmed)
        {
            return;
        }

        await RunAsync(async ct =>
        {
            await _products.DeleteAsync(row.Id, ct).ConfigureAwait(true);
            Notifier.Success($"{row.Name} retired.");
            await RefreshCoreAsync().ConfigureAwait(true);
        }, "Could not retire the product.").ConfigureAwait(true);
    }

    [RelayCommand]
    private void GenerateBarcodeForEditor()
    {
        if (Editor is not null)
        {
            Editor.Barcode = GenerateBarcode();
        }
    }

    /// <summary>
    /// Builds a 13-digit EAN-13 body with a valid mod-10 check digit from a time-seeded counter.
    /// Good enough for in-store labels; real GS1 prefixes are issued by the local authority.
    /// </summary>
    private static string GenerateBarcode()
    {
        var seed = (int)(DateTime.UtcNow.Ticks % 100_000_000);
        var body = $"20{seed:D10}"[..12];
        return Data.DatabaseBootstrapper.BuildEan13(body);
    }

    private Task<bool> ConfirmAsync(string title, string message) =>
        _dialogs.ConfirmAsync(title, message, "Yes", "No");

    public string TotalCountDisplay => TotalCount.ToString(CultureInfo.InvariantCulture);
}
