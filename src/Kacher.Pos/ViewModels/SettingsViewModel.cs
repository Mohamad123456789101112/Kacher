using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kacher.Pos.Data;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Kacher.Pos.Services;
using Kacher.Pos.Services.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Kacher.Pos.ViewModels;

public sealed class UserRowViewModel
{
    public UserRowViewModel(User user)
    {
        Id = user.Id;
        DisplayName = user.DisplayName;
        UserName = user.UserName;
        Role = user.Role;
        IsActive = user.IsActive;
        LastLogin = user.LastLoginUtc?.ToLocalTime();
    }

    public int Id { get; }
    public string DisplayName { get; }
    public string UserName { get; }
    public UserRole Role { get; }
    public bool IsActive { get; }
    public DateTime? LastLogin { get; }
    public string RoleText => Role.ToString();
    public string LastLoginText => LastLogin?.ToString("dd MMM HH:mm") ?? "never";
}

/// <summary>
/// Application settings: appearance, operators (PIN management), data maintenance and the embedded
/// hardware commissioning panel.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IAppSettingsService _settings;
    private readonly IUserRepository _users;
    private readonly ISessionService _session;
    private readonly IDialogService _dialogs;
    private readonly IThemeService _theme;
    private readonly IAppLog _log;

    public SettingsViewModel(
        IAppSettingsService settings,
        IUserRepository users,
        ISessionService session,
        IDialogService dialogs,
        IThemeService theme,
        HardwareSettingsViewModel hardware,
        IAppLog log,
        IUserNotifier notifier) : base(notifier)
    {
        _settings = settings;
        _users = users;
        _session = session;
        _dialogs = dialogs;
        _theme = theme;
        _log = log;

        Hardware = hardware;
        Title = "Settings";
    }

    /// <summary>Hardware tab content, injected so its state survives tab switches.</summary>
    public HardwareSettingsViewModel Hardware { get; }

    public ObservableCollection<UserRowViewModel> Users { get; } = new();

    public ObservableCollection<UserRole> Roles { get; } = new(Enum.GetValues<UserRole>());

    [ObservableProperty]
    private bool _isDarkTheme = true;

    [ObservableProperty]
    private UserRowViewModel? _selectedUser;

    [ObservableProperty]
    private string _newUserName = string.Empty;

    [ObservableProperty]
    private string _newLoginName = string.Empty;

    [ObservableProperty]
    private string _newPin = string.Empty;

    [ObservableProperty]
    private UserRole _newRole = UserRole.Cashier;

    [ObservableProperty]
    private string _databaseSize = "—";

    [ObservableProperty]
    private string _databasePath = DatabaseConnectionFactory.DatabasePath;

    [ObservableProperty]
    private string _version = "1.0.0";

    [ObservableProperty]
    private int _selectedTab;

    public bool CanManageUsers => _session.HasPermission(Permissions.ManageUsers);

    public string DataFolder => DatabaseConnectionFactory.DataDirectory;

    public string MachineName => Environment.MachineName;

    public string RuntimeInfo => $".NET {Environment.Version} · {Environment.OSVersion.VersionString}";

    public override async Task LoadAsync(CancellationToken ct = default)
    {
        await RunAsync(async token =>
        {
            IsDarkTheme = _theme.IsDark;
            await LoadUsersAsync(token).ConfigureAwait(true);
            RefreshDatabaseInfo();
            await Hardware.LoadAsync(token).ConfigureAwait(true);
        }, "Could not load settings.");
    }

    private async Task LoadUsersAsync(CancellationToken ct = default)
    {
        var users = await _users.GetAllAsync(ct).ConfigureAwait(true);
        Users.Clear();
        foreach (var user in users)
        {
            Users.Add(new UserRowViewModel(user));
        }
    }

    [RelayCommand]
    private async Task ToggleThemeAsync()
    {
        IsDarkTheme = !IsDarkTheme;
        await _theme.ApplyAsync(IsDarkTheme).ConfigureAwait(true);
        Notifier.Info(IsDarkTheme ? "Dark mode enabled." : "Light mode enabled.");
    }

    [RelayCommand]
    private async Task CreateUserAsync()
    {
        if (!CanManageUsers)
        {
            Notifier.Warn("Only an administrator can create operators.");
            return;
        }

        if (string.IsNullOrWhiteSpace(NewUserName) || string.IsNullOrWhiteSpace(NewPin))
        {
            Notifier.Warn("Name and PIN are required.");
            return;
        }

        if (NewPin.Length is < 4 or > 6 || !NewPin.All(char.IsDigit))
        {
            Notifier.Warn("PIN must be 4-6 digits.");
            return;
        }

        var login = string.IsNullOrWhiteSpace(NewLoginName)
            ? NewUserName.Split(' ')[0].ToLowerInvariant()
            : NewLoginName.Trim().ToLowerInvariant();

        await RunAsync(async ct =>
        {
            await _users.CreateAsync(NewUserName.Trim(), login, NewPin, NewRole, ct).ConfigureAwait(true);
            await LoadUsersAsync(ct).ConfigureAwait(true);

            NewUserName = string.Empty;
            NewLoginName = string.Empty;
            NewPin = string.Empty;

            Notifier.Success($"Operator '{login}' created.");
        }, "Could not create the operator.");
    }

    [RelayCommand]
    private async Task ChangePinAsync(UserRowViewModel? row)
    {
        var target = row ?? SelectedUser;

        if (target is null)
        {
            Notifier.Warn("Select an operator first.");
            return;
        }

        if (!CanManageUsers && _session.CurrentUser?.Id != target.Id)
        {
            Notifier.Warn("You can only change your own PIN.");
            return;
        }

        var pin = await _dialogs.PromptTextAsync($"New PIN — {target.DisplayName}", "4-6 digits", null, "Save")
            .ConfigureAwait(true);

        if (pin is null)
        {
            return;
        }

        if (pin.Length is < 4 or > 6 || !pin.All(char.IsDigit))
        {
            Notifier.Warn("PIN must be 4-6 digits.");
            return;
        }

        await RunAsync(async ct =>
        {
            await _users.ChangePinAsync(target.Id, pin, ct).ConfigureAwait(true);
            _log.Info($"PIN changed for {target.UserName} by {_session.CashierName}.");
            Notifier.Success("PIN updated.");
        }, "Could not change the PIN.");
    }

    [RelayCommand]
    private async Task ToggleUserActiveAsync(UserRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!CanManageUsers)
        {
            Notifier.Warn("Only an administrator can disable operators.");
            return;
        }

        if (row.Id == _session.CurrentUser?.Id)
        {
            Notifier.Warn("You cannot disable the account you are signed in with.");
            return;
        }

        await RunAsync(async ct =>
        {
            await _users.SetActiveAsync(row.Id, !row.IsActive, ct).ConfigureAwait(true);
            await LoadUsersAsync(ct).ConfigureAwait(true);
            Notifier.Success($"{row.DisplayName} {(row.IsActive ? "disabled" : "enabled")}.");
        }, "Could not update the operator.");
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        Directory.CreateDirectory(DatabaseConnectionFactory.DataDirectory);

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = DatabaseConnectionFactory.DataDirectory,
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Compacts the SQLite file and rebuilds statistics. Safe while the app runs (WAL allows it) but
    /// best scheduled on a closed drawer — it can take a second on a large database.
    /// </summary>
    [RelayCommand]
    private async Task CompactDatabaseAsync()
    {
        if (!CanManageUsers)
        {
            Notifier.Warn("Only an administrator can compact the database.");
            return;
        }

        if (!await _dialogs.ConfirmAsync("Compact database",
                "Rebuild the local database file and refresh indexes now?", "Compact").ConfigureAwait(true))
        {
            return;
        }

        await RunAsync(async ct =>
        {
            await using var db = DatabaseConnectionFactory.CreateContext();
            await db.Database.ExecuteSqlRawAsync("VACUUM;", ct).ConfigureAwait(true);
            await db.Database.ExecuteSqlRawAsync("ANALYZE;", ct).ConfigureAwait(true);

            RefreshDatabaseInfo();
            Notifier.Success("Database compacted.");
        }, "Could not compact the database.");
    }

    private void RefreshDatabaseInfo()
    {
        try
        {
            var info = new FileInfo(DatabaseConnectionFactory.DatabasePath);
            DatabaseSize = info.Exists ? $"{info.Length / 1024d / 1024d:0.00} MB" : "not created";
        }
        catch (IOException)
        {
            DatabaseSize = "unknown";
        }
    }
}
