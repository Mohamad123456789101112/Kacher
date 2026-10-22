using System.ComponentModel.DataAnnotations;

namespace Kacher.Pos.Models;

/// <summary>Well-known permission names checked by <c>IAuthorizationService</c>.</summary>
public static class Permissions
{
    public const string ProcessSale = "sale.process";
    public const string RefundSale = "sale.refund";
    public const string ApplyDiscount = "sale.discount";
    public const string EditProducts = "product.edit";
    public const string ViewCatalogue = "catalogue.view";
    public const string AdjustStock = "stock.adjust";
    public const string ViewReports = "report.view";
    public const string CloseShift = "shift.close";
    public const string ManageUsers = "user.manage";
    public const string ManageHardware = "hardware.manage";
    public const string OpenDrawer = "drawer.open";

    /// <summary>Role → permission matrix. Single source of truth for UI gating and service guards.</summary>
    public static IReadOnlyDictionary<UserRole, IReadOnlySet<string>> Matrix { get; } =
        new Dictionary<UserRole, IReadOnlySet<string>>
        {
            [UserRole.Cashier] = new HashSet<string>
            {
                ProcessSale, OpenDrawer
            },
            [UserRole.Supervisor] = new HashSet<string>
            {
                ProcessSale, OpenDrawer, RefundSale, ApplyDiscount, AdjustStock, ViewReports,
                CloseShift, EditProducts, ViewCatalogue
            },
            [UserRole.Admin] = new HashSet<string>
            {
                ProcessSale, OpenDrawer, RefundSale, ApplyDiscount, AdjustStock, ViewReports,
                CloseShift, EditProducts, ViewCatalogue, ManageUsers, ManageHardware
            }
        };

    public static bool IsGranted(UserRole role, string permission) =>
        Matrix.TryGetValue(role, out var set) && set.Contains(permission);
}

/// <summary>
/// An operator who can sign in with a PIN. PINs are stored as PBKDF2-SHA256 hashes with
/// a per-user salt — never as plaintext.
/// </summary>
public class User
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Optional login alias shown on receipts and audit rows.</summary>
    [MaxLength(32)]
    public string UserName { get; set; } = string.Empty;

    /// <summary>PBKDF2-HMAC-SHA256 derived key, Base64 encoded.</summary>
    [MaxLength(128)]
    public string PinHash { get; set; } = string.Empty;

    /// <summary>Cryptographically random salt, Base64 encoded.</summary>
    [MaxLength(64)]
    public string PinSalt { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.Cashier;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginUtc { get; set; }

    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();

    public ICollection<Shift> Shifts { get; set; } = new List<Shift>();

    public bool Can(string permission) => IsActive && Permissions.IsGranted(Role, permission);
}
