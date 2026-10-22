using System.Globalization;
using System.Text;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Microsoft.EntityFrameworkCore;

namespace Kacher.Pos.Data;

/// <summary>
/// Brings the local database to the latest schema and guarantees the terminal is usable on
/// first run. Called once from <c>App.OnStartup</c> before the shell is shown.
/// </summary>
public sealed class DatabaseBootstrapper
{
    private readonly IDbContextFactory<KacherDbContext> _contextFactory;
    private readonly IAppLog _log;

    public DatabaseBootstrapper(IDbContextFactory<KacherDbContext> contextFactory, IAppLog log)
    {
        _contextFactory = contextFactory;
        _log = log;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        DatabaseConnectionFactory.EnsureDirectories();

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // ── Schema ─────────────────────────────────────────────────────────────────────────────
        // Migrate() is a no-op when the model is unchanged and replays only pending steps, so it is
        // safe (and cheap: one SELECT against __EFMigrationsHistory) to call on every launch.
        // If the migrations assembly has no migrations yet (fresh clone, before `dotnet ef
        // migrations add InitialCreate`), fall back to EnsureCreated so a dev build still runs.
        var hasMigrations = db.Database.GetMigrations().Any();
        if (hasMigrations)
        {
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
            _log.Info("Database schema migrated to latest.");
        }
        else
        {
            await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
            _log.Warn("No EF migrations found — schema created with EnsureCreated. " +
                      "Run `dotnet ef migrations add InitialCreate` for a versioned baseline.");
        }

        await SeedAsync(db, ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("PRAGMA optimize;", ct).ConfigureAwait(false);
    }

    /// <summary>Idempotent seed: each block only runs when its table is still empty.</summary>
    private static async Task SeedAsync(KacherDbContext db, CancellationToken ct)
    {
        await SeedUsersAsync(db, ct).ConfigureAwait(false);
        await SeedHardwareAsync(db, ct).ConfigureAwait(false);
        await SeedCatalogueAsync(db, ct).ConfigureAwait(false);
        await SeedSettingsAsync(db, ct).ConfigureAwait(false);
    }

    private static async Task SeedUsersAsync(KacherDbContext db, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        // Demo PINs. A real deployment forces a PIN change on first admin login (see SettingsView).
        var seed = new (string Name, string Login, string Pin, UserRole Role)[]
        {
            ("Omar Khaled",  "admin",   "1234", UserRole.Admin),
            ("Nour El-Sayed","nour",    "2468", UserRole.Supervisor),
            ("Yara Hassan",  "yara",    "1111", UserRole.Cashier),
            ("Ali Mansour",  "ali",     "2222", UserRole.Cashier)
        };

        foreach (var (name, login, pin, role) in seed)
        {
            var (hash, salt) = PinHasher.Hash(pin);
            db.Users.Add(new User
            {
                DisplayName = name,
                UserName = login,
                PinHash = hash,
                PinSalt = salt,
                Role = role,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task SeedHardwareAsync(KacherDbContext db, CancellationToken ct)
    {
        if (await db.HardwareConfigs.AnyAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        db.HardwareConfigs.Add(new HardwareConfig
        {
            ProfileName = "Front Counter — Epson TM-T20III",
            IsActive = true,
            ThermalPrinterName = "EPSON TM-T20III Receipt",
            ConnectionType = PrinterConnectionType.WindowsDriver,
            NetworkPort = 9100,
            PaperWidth = PaperWidth.Mm80,
            CodePage = 16,
            AutoCut = true,
            PrintQrCode = true,
            PrintReceiptBarcode = true,
            PrintLogo = true,
            OpenDrawerCommand = "ESC p 0 25 250",
            OpenDrawerOnSale = true,
            OpenDrawerOnCashPayment = true,
            DrawerConnectionType = DrawerConnectionType.PrinterKickPin2,
            ScannerMinLength = 3,
            ScannerInterKeyTimeoutMs = 60,
            StoreName = "Kacher Market",
            StoreAddress = "12 Corniche Road, Alexandria, Egypt",
            StorePhone = "+20 3 555 0100",
            TaxRegistrationNumber = "EG-512-884-003",
            ReceiptFooter = "Thank you for shopping with us! Exchange within 14 days with receipt.",
            DigitalReceiptBaseUrl = "https://receipts.kacher.local/i/",
            UpdatedAtUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task SeedSettingsAsync(KacherDbContext db, CancellationToken ct)
    {
        if (await db.AppSettings.AnyAsync(s => s.Key == "ui.theme", ct).ConfigureAwait(false))
        {
            return;
        }

        db.AppSettings.AddRange(
            new AppSetting { Key = "ui.theme", Value = "Dark" },
            new AppSetting { Key = "ui.currency", Value = "EGP" },
            new AppSetting { Key = "ui.culture", Value = "en-EG" },
            new AppSetting { Key = "pos.defaultTaxRate", Value = "0.14" },
            new AppSetting { Key = "pos.requirePinForRefund", Value = "true" },
            new AppSetting { Key = "pos.roundingMode", Value = "Nearest0.05" });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task SeedCatalogueAsync(KacherDbContext db, CancellationToken ct)
    {
        if (await db.Products.AnyAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        // ── Categories ────────────────────────────────────────────────────────────────────────
        var categorySeed = new (string Name, string Icon, string Color, string? Desc)[]
        {
            ("Beverages",   "CupWater",      "#0EA5E9", "Soft drinks, water and juices"),
            ("Coffee",      "Coffee",        "#92400E", "Espresso, tea and hot drinks"),
            ("Bakery",      "BreadSlice",    "#D97706", "Fresh bread and pastries"),
            ("Dairy",       "Cheese",        "#FBBF24", "Milk, cheese and yoghurt"),
            ("Snacks",      "Cookie",        "#F97316", "Chips, biscuits and nuts"),
            ("Grocery",     "Basket",        "#16A34A", "Pantry staples"),
            ("Household",   "SprayBottle",   "#7C3AED", "Cleaning and paper goods"),
            ("Fresh Produce","FoodApple",    "#65A30D", "Fruit and vegetables")
        };

        var categories = new List<Category>();
        for (var i = 0; i < categorySeed.Length; i++)
        {
            var (name, icon, color, desc) = categorySeed[i];
            categories.Add(new Category
            {
                Name = name,
                IconName = icon,
                ColorHex = color,
                Description = desc,
                SortOrder = i
            });
        }

        db.Categories.AddRange(categories);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ── Products ──────────────────────────────────────────────────────────────────────────
        // (Name, Category, Price, Cost, Stock, TaxRate, Weighted)
        var productSeed = new (string Name, string Category, decimal Price, decimal Cost, decimal Stock, decimal Tax, bool Weighted)[]
        {
            ("Coca-Cola 330ml",            "Beverages", 15.00m, 10.50m, 240, 0.14m, false),
            ("Pepsi 330ml",                "Beverages", 15.00m, 10.50m, 180, 0.14m, false),
            ("Sprite 330ml",               "Beverages", 15.00m, 10.50m, 160, 0.14m, false),
            ("Baraka Water 600ml",         "Beverages",  5.00m,  2.80m, 400, 0.14m, false),
            ("Nestlé Pure Life 1.5L",      "Beverages", 10.00m,  6.50m, 220, 0.14m, false),
            ("Juhayna Mango Juice 1L",     "Beverages", 32.00m, 24.00m, 96,  0.14m, false),
            ("Red Bull 250ml",             "Beverages", 65.00m, 50.00m, 72,  0.14m, false),
            ("Schweppes Tonic 300ml",      "Beverages", 18.00m, 13.00m, 84,  0.14m, false),

            ("Espresso Single",            "Coffee",     25.00m,  6.00m, 999, 0.14m, false),
            ("Espresso Double",            "Coffee",     35.00m,  9.00m, 999, 0.14m, false),
            ("Cappuccino",                 "Coffee",     45.00m, 12.00m, 999, 0.14m, false),
            ("Flat White",                 "Coffee",     48.00m, 13.00m, 999, 0.14m, false),
            ("Turkish Coffee",             "Coffee",     30.00m,  8.00m, 999, 0.14m, false),
            ("Tea with Milk",              "Coffee",     22.00m,  5.00m, 999, 0.14m, false),
            ("Iced Latte 400ml",           "Coffee",     55.00m, 16.00m, 120, 0.14m, false),
            ("Hot Chocolate",              "Coffee",     50.00m, 15.00m, 999, 0.14m, false),

            ("Baladi Bread Pack",          "Bakery",     10.00m,  6.00m, 130, 0.00m, false),
            ("Croissant Butter",           "Bakery",     25.00m, 12.00m, 60,  0.14m, false),
            ("Chocolate Muffin",           "Bakery",     30.00m, 15.00m, 48,  0.14m, false),
            ("Cinnamon Danish",            "Bakery",     32.00m, 17.00m, 36,  0.14m, false),
            ("Whole Wheat Toast",          "Bakery",     38.00m, 26.00m, 40,  0.00m, false),
            ("Fino Bread Roll",            "Bakery",     12.00m,  7.00m, 90,  0.00m, false),

            ("Juhayna Full Cream Milk 1L", "Dairy",      42.00m, 34.00m, 120, 0.00m, false),
            ("Juhayna Skim Milk 1L",       "Dairy",      40.00m, 32.00m, 80,  0.00m, false),
            ("Puck Cream Cheese 200g",     "Dairy",      75.00m, 58.00m, 45,  0.14m, false),
            ("Domty White Cheese 500g",    "Dairy",      68.00m, 52.00m, 55,  0.00m, false),
            ("Activia Yoghurt 170g",       "Dairy",      22.00m, 15.00m, 70,  0.00m, false),
            ("President Butter 200g",      "Dairy",      95.00m, 74.00m, 30,  0.14m, false),
            ("Eggs (30 pcs tray)",         "Dairy",     165.00m,138.00m, 24,  0.00m, false),

            ("Chipsy Salt 90g",            "Snacks",     15.00m,  9.50m, 200, 0.14m, false),
            ("Doritos Sweet Chili 90g",    "Snacks",     18.00m, 12.00m, 160, 0.14m, false),
            ("Oreo Original 133g",         "Snacks",     25.00m, 17.00m, 140, 0.14m, false),
            ("Galaxy Smooth Milk 80g",     "Snacks",     45.00m, 33.00m, 110, 0.14m, false),
            ("Mixed Nuts 250g",            "Snacks",    120.00m, 92.00m, 45,  0.14m, false),
            ("Tiger Biscuits 60g",         "Snacks",      8.00m,  4.50m, 300, 0.14m, false),
            ("Lay's Classic 170g",         "Snacks",     30.00m, 21.00m, 95,  0.14m, false),

            ("Sunflower Oil 1L",           "Grocery",   105.00m, 88.00m, 60,  0.14m, false),
            ("Egyptian Rice 1kg",          "Grocery",    42.00m, 33.00m, 85,  0.00m, false),
            ("Pasta Penne 400g",           "Grocery",    18.00m, 12.00m, 120, 0.14m, false),
            ("Tomato Paste 400g",          "Grocery",    26.00m, 19.00m, 90,  0.14m, false),
            ("Sugar 1kg",                  "Grocery",    38.00m, 30.00m, 110, 0.00m, false),
            ("Table Salt 500g",            "Grocery",     7.00m,  3.50m, 150, 0.14m, false),
            ("Elite Honey 500g",           "Grocery",   115.00m, 92.00m, 28,  0.14m, false),
            ("Instant Coffee Nescafé 100g","Grocery",   185.00m,155.00m, 34,  0.14m, false),

            ("Fairy Dish Soap 750ml",      "Household",  58.00m, 44.00m, 55,  0.14m, false),
            ("Fine Paper Towels 2pk",      "Household",  45.00m, 33.00m, 65,  0.14m, false),
            ("Clorox Bleach 1L",           "Household",  32.00m, 23.00m, 70,  0.14m, false),
            ("Garbage Bags 30L",           "Household",  28.00m, 19.00m, 80,  0.14m, false),
            ("Persil Powder 2kg",          "Household", 210.00m,175.00m, 22,  0.14m, false),

            ("Bananas (kg)",               "Fresh Produce", 28.00m, 19.00m, 40, 0.00m, true),
            ("Red Apples (kg)",            "Fresh Produce", 65.00m, 48.00m, 35, 0.00m, true),
            ("Tomatoes (kg)",              "Fresh Produce", 18.00m, 11.00m, 50, 0.00m, true),
            ("Potatoes (kg)",              "Fresh Produce", 22.00m, 14.00m, 60, 0.00m, true),
            ("Oranges (kg)",               "Fresh Produce", 25.00m, 16.00m, 45, 0.00m, true),
            ("Cucumber (kg)",              "Fresh Produce", 20.00m, 12.00m, 30, 0.00m, true)
        };

        var byName = categories.ToDictionary(c => c.Name);
        var index = 0;

        foreach (var (name, categoryName, price, cost, stock, tax, weighted) in productSeed)
        {
            var category = byName[categoryName];
            db.Products.Add(new Product
            {
                Name = name,
                // Deterministic, checksum-valid EAN-13 so barcode scanning can be demoed immediately.
                Barcode = BuildEan13($"200{index + 1000:D9}"),
                Sku = BuildSku(name),
                CategoryId = category.Id,
                SellingPrice = price,
                CostPrice = cost,
                StockQuantity = stock,
                TaxRate = tax,
                ReorderLevel = stock > 500 ? 50 : 12,
                ColorHex = category.ColorHex,
                IsWeighted = weighted,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            index++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Appends the EAN-13 mod-10 check digit to a 12-digit body.</summary>
    public static string BuildEan13(string body12)
    {
        if (body12.Length != 12 || !body12.All(char.IsDigit))
        {
            throw new ArgumentException("EAN-13 body must be exactly 12 digits.", nameof(body12));
        }

        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            var digit = body12[i] - '0';
            sum += (i % 2 == 0) ? digit : digit * 3;
        }

        var check = (10 - (sum % 10)) % 10;
        return body12 + check.ToString(CultureInfo.InvariantCulture);
    }

    private static string BuildSku(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            if (sb.Length == 8)
            {
                break;
            }
        }
        return sb.ToString();
    }
}
