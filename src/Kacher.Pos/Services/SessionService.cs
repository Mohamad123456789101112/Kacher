using Kacher.Pos.Models;

namespace Kacher.Pos.Services;

/// <summary>
/// Holds the signed-in operator and the open shift for the lifetime of the process.
/// Registered as a singleton; every view model reads identity/permissions from here rather than
/// passing a user object around.
/// </summary>
public interface ISessionService
{
    User? CurrentUser { get; }

    Shift? CurrentShift { get; }

    bool IsSignedIn { get; }

    bool ShiftIsOpen { get; }

    string CashierName { get; }

    UserRole Role { get; }

    event EventHandler? SessionChanged;

    void SignIn(User user);

    void SignOut();

    void SetShift(Shift? shift);

    bool HasPermission(string permission);
}

public sealed class SessionService : ISessionService
{
    public User? CurrentUser { get; private set; }

    public Shift? CurrentShift { get; private set; }

    public bool IsSignedIn => CurrentUser is not null;

    public bool ShiftIsOpen => CurrentShift?.Status == ShiftStatus.Open;

    public string CashierName => CurrentUser?.DisplayName ?? "Guest";

    public UserRole Role => CurrentUser?.Role ?? UserRole.Cashier;

    public event EventHandler? SessionChanged;

    public void SignIn(User user)
    {
        CurrentUser = user;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SignOut()
    {
        CurrentUser = null;
        CurrentShift = null;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetShift(Shift? shift)
    {
        CurrentShift = shift;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool HasPermission(string permission) =>
        CurrentUser is not null && Permissions.IsGranted(CurrentUser.Role, permission);
}
