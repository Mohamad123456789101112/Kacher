using Kacher.Pos.Data;
using Kacher.Pos.Models;
using Microsoft.EntityFrameworkCore;

namespace Kacher.Pos.Services.Repositories;

public interface IProductRepository
{
    Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default);

    /// <summary>Loads the catalogue for the grid. Filtering happens in SQL, projection is slim.</summary>
    Task<IReadOnlyList<Product>> GetCatalogueAsync(int? categoryId, string? search, bool lowStockOnly = false, CancellationToken ct = default);

    /// <summary>O(1) barcode lookup served by <c>UX_Products_Barcode</c>. This is the hot path.</summary>
    Task<Product?> FindByBarcodeAsync(string barcode, CancellationToken ct = default);

    Task<Product?> GetByIdAsync(int id, CancellationToken ct = default);

    Task<Product> SaveAsync(Product product, CancellationToken ct = default);

    Task<bool> DeleteAsync(int id, CancellationToken ct = default);

    Task AdjustStockAsync(int productId, decimal delta, StockMovementReason reason, int? userId, string? note, CancellationToken ct = default);

    Task<IReadOnlyList<StockMovement>> GetStockMovementsAsync(int productId, int take = 50, CancellationToken ct = default);
}

public sealed class ProductRepository : IProductRepository
{
    private readonly IDbContextFactory<KacherDbContext> _factory;

    /// <summary>
    /// Compiled query: EF caches the SQL + materialiser, shaving the LINQ translation cost off the
    /// scanner path. Scanning happens dozens of times a minute, so this matters in practice.
    /// </summary>
    /// <remarks>
    /// No <c>Include</c> here on purpose: EF Core compiled queries cannot contain Include, and the
    /// scanner path only needs the product columns. The tile colour falls back to the product's own
    /// <see cref="Product.ColorHex"/>, which the catalogue seed always populates.
    /// </remarks>
    private static readonly Func<KacherDbContext, string, Task<Product?>> BarcodeQuery =
        EF.CompileAsyncQuery((KacherDbContext db, string barcode) =>
            db.Products
              .AsNoTracking()
              .FirstOrDefault(p => p.Barcode == barcode));

    public ProductRepository(IDbContextFactory<KacherDbContext> factory) => _factory = factory;

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Categories
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Product>> GetCatalogueAsync(
        int? categoryId, string? search, bool lowStockOnly = false, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = db.Products.AsNoTracking().Include(p => p.Category).Where(p => p.IsActive);

        if (categoryId is > 0)
        {
            query = query.Where(p => p.CategoryId == categoryId);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(p => EF.Functions.Like(p.Name, $"%{term}%") || EF.Functions.Like(p.Barcode, $"{term}%"));
        }

        if (lowStockOnly)
        {
            query = query.Where(p => p.StockQuantity <= p.ReorderLevel);
        }

        return await query
            .OrderBy(p => p.Name)
            .Take(500)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<Product?> FindByBarcodeAsync(string barcode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return null;
        }

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await BarcodeQuery(db, barcode.Trim()).ConfigureAwait(false);
    }

    public async Task<Product?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Products.AsNoTracking().Include(p => p.Category)
            .FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<Product> SaveAsync(Product product, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (product.Id == 0)
        {
            product.CreatedAtUtc = DateTime.UtcNow;
            db.Products.Add(product);
        }
        else
        {
            product.UpdatedAtUtc = DateTime.UtcNow;
            db.Products.Update(product);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return product;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
        if (product is null)
        {
            return false;
        }

        // Soft delete: historical invoices still reference this row.
        product.IsActive = false;
        product.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task AdjustStockAsync(
        int productId, decimal delta, StockMovementReason reason, int? userId, string? note,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"Product {productId} not found.");

        product.StockQuantity += delta;
        product.UpdatedAtUtc = DateTime.UtcNow;

        db.StockMovements.Add(new StockMovement
        {
            ProductId = productId,
            QuantityDelta = delta,
            QuantityAfter = product.StockQuantity,
            Reason = reason,
            Note = note,
            UserId = userId,
            CreatedAtUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StockMovement>> GetStockMovementsAsync(int productId, int take = 50, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.StockMovements.AsNoTracking()
            .Where(s => s.ProductId == productId)
            .OrderByDescending(s => s.CreatedAtUtc)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
