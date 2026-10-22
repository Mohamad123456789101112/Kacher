using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Kacher.Pos.Services;
using Kacher.Pos.Services.Repositories;

namespace Kacher.Pos.ViewModels;

/// <summary>
/// PIN sign-in. The PIN never leaves this view model as plaintext except through the repository call,
/// and the keypad is the only way to enter it — no TextBox means no clipboard, no IME, no autocomplete.
/// </summary>
public sealed partial class LoginViewModel : ViewModelBase
{
    private const int MinPinLength = 4;
    private const int MaxPinLength = 6;

    private readonly IUserRepository _users;
    private readonly ISessionService _session;
    private readonly IShiftService _shifts;

    [ObservableProperty]
    private string _pin = string.Empty;

    [ObservableProperty]
    private string _cashierHint = "Enter your PIN to begin";

    [ObservableProperty]
    private string? _lastUserName;

    /// <summary>Raised once authentication succeeded; the window handles navigation.</summary>
    public event EventHandler<User>? SignedIn;

    public LoginViewModel(
        IUserRepository users,
        ISessionService session,
        IShiftService shifts,
        IUserNotifier notifier) : base(notifier)
    {
        _users = users;
        _session = session;
        _shifts = shifts;
        Title = "Sign in";
    }

    /// <summary>Masked display: one bullet per entered digit, padded to four slots.</summary>
    public string MaskedPin
    {
        get
        {
            var filled = new string('●', Pin.Length);
            var remaining = Math.Max(0, MinPinLength - Pin.Length);
            return filled + new string('○', remaining);
        }
    }

    public bool CanSubmit => Pin.Length >= MinPinLength;

    partial void OnPinChanged(string value)
    {
        OnPropertyChanged(nameof(MaskedPin));
        OnPropertyChanged(nameof(CanSubmit));
        ErrorMessage = null;
    }

    [RelayCommand]
    private void AppendDigit(string? digit)
    {
        if (string.IsNullOrEmpty(digit) || Pin.Length >= MaxPinLength)
        {
            return;
        }

        // Only digits — the keypad also feeds the physical keyboard through this command.
        if (!digit.All(char.IsDigit))
        {
            return;
        }

        Pin += digit;

        // Auto-submit at the maximum length for a zero-tap sign-in.
        if (Pin.Length == MaxPinLength)
        {
            _ = SignInAsync();
        }
    }

    [RelayCommand]
    private void Backspace()
    {
        if (Pin.Length > 0)
        {
            Pin = Pin[..^1];
        }
    }

    [RelayCommand]
    private void Clear()
    {
        Pin = string.Empty;
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (Pin.Length < MinPinLength)
        {
            ErrorMessage = "PIN must be at least 4 digits.";
            return;
        }

        await RunAsync(async ct =>
        {
            var user = await _users.AuthenticateAsync(Pin, ct).ConfigureAwait(true);

            if (user is null)
            {
                Pin = string.Empty;
                ErrorMessage = "Incorrect PIN. Please try again.";
                CashierHint = "Access denied";
                return;
            }

            _session.SignIn(user);

            // Restore an in-progress shift (e.g. a cashier handover mid-day) so the sale is attached
            // to the correct drawer session instead of starting a new one.
            var openShift = await _shifts.GetOpenShiftAsync(ct).ConfigureAwait(true);
            _session.SetShift(openShift);

            LastUserName = user.DisplayName;
            CashierHint = openShift is null
                ? $"{user.DisplayName} · no open shift"
                : $"{user.DisplayName} · shift {openShift.ShiftNumber} open";

            Pin = string.Empty;
            Notifier.Success($"Welcome, {user.DisplayName}");
            SignedIn?.Invoke(this, user);
        }, "Sign-in failed.").ConfigureAwait(true);
    }

    [RelayCommand]
    private void SignInFromKeyboard() => _ = SignInAsync();
}
