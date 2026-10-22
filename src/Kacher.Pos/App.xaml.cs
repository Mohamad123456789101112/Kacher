using System.Text;
using System.Windows;
using System.Windows.Threading;
using Kacher.Pos.Data;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Services;
using Kacher.Pos.Services.Hardware;
using Kacher.Pos.Services.Hardware.Scanner;
using Kacher.Pos.Services.Hardware.Transports;
using Kacher.Pos.Services.Pdf;
using Kacher.Pos.Services.Repositories;
using Kacher.Pos.ViewModels;
using Kacher.Pos.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kacher.Pos;

/// <summary>
/// Composition root. The whole application is wired here so there is exactly one place to audit
/// lifetimes — important on a till that runs for months without a restart.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Single-instance guard. Two tills writing the same SQLite file is the classic way to corrupt a
    /// POS database, and the installer checks this mutex before replacing binaries.
    /// </summary>
    private const string InstanceMutexName = "KacherPOS-running";

    private Mutex? _instanceMutex;
    private IServiceProvider? _services;

    public static IServiceProvider Services =>
        ((App)Current)._services ?? throw new InvalidOperationException("Service provider is not ready.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Single instance ──────────────────────────────────────────────────────────────────
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            MessageBox.Show(
                "Kacher POS is already running on this terminal.\n\n" +
                "Switch to the existing window (Alt+Tab) instead of starting a second copy — " +
                "two instances would write to the same database.",
                "Kacher POS",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown(0);
            return;
        }

        // Single-byte code pages (CP437/1256/864 …) are not in .NET by default; ESC/POS needs them.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var startupLog = new AppLog();

        try
        {
            _services = ConfigureServices(startupLog);

            AttachGlobalExceptionHandlers(startupLog);

            var bootstrapper = _services.GetRequiredService<DatabaseBootstrapper>();
            await bootstrapper.InitializeAsync().ConfigureAwait(true);

            // Data safety before anything else: keep at most one snapshot per day, pruned to 30.
            // Runs in the background so a large database never delays the sign-in screen.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _services.GetRequiredService<IBackupService>()
                        .EnsureRecentAsync(TimeSpan.FromHours(24))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    startupLog.Warn($"Startup backup skipped: {ex.Message}");
                }
            });

            var settings = _services.GetRequiredService<IAppSettingsService>();
            await settings.LoadAsync().ConfigureAwait(true);

            var theme = _services.GetRequiredService<IThemeService>();
            await theme.InitializeAsync().ConfigureAwait(true);

            // ── Sign-in ─────────────────────────────────────────────────────────────────────
            var login = _services.GetRequiredService<LoginWindow>();
            var signedIn = login.ShowDialog() == true;

            if (!signedIn)
            {
                Shutdown(0);
                return;
            }

            var main = _services.GetRequiredService<MainWindow>();
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
        }
        catch (Exception ex)
        {
            startupLog.Error("Fatal error during startup.", ex);
            MessageBox.Show(
                $"Kacher POS could not start.\n\n{ex.Message}\n\nSee the log at:\n{DatabaseConnectionFactory.LogDirectory}",
                "Kacher POS",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static IServiceProvider ConfigureServices(IAppLog log)
    {
        var services = new ServiceCollection();

        // ── Infrastructure ──────────────────────────────────────────────────────────────────
        services.AddSingleton(log);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IAppSettingsService, AppSettingsService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IUserNotifier, UserNotifier>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<ISessionService, SessionService>();

        // ── Persistence ─────────────────────────────────────────────────────────────────────
        // DbContextFactory (not AddDbContext) because services are singletons and each operation
        // owns a short-lived context. This is also what makes concurrent reads safe.
        services.AddDbContextFactory<KacherDbContext>(options =>
            options.UseSqlite(DatabaseConnectionFactory.BuildConnectionString())
                   .AddInterceptors(new SqlitePragmaInterceptor()));

        services.AddSingleton<DatabaseBootstrapper>();

        // ── Repositories & domain services ──────────────────────────────────────────────────
        services.AddSingleton<IProductRepository, ProductRepository>();
        services.AddSingleton<IInvoiceRepository, InvoiceRepository>();
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<IHardwareConfigRepository, HardwareConfigRepository>();
        services.AddSingleton<ISaleService, SaleService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IShiftService, ShiftService>();
        services.AddSingleton<IReportService, ReportService>();

        // ── Hardware ────────────────────────────────────────────────────────────────────────
        services.AddSingleton<ThermalTransportFactory>();
        services.AddSingleton<IReceiptTransportFactory>(sp => sp.GetRequiredService<ThermalTransportFactory>());
        services.AddSingleton<IReceiptPrinter, EpsonEscPosPrinterService>();
        services.AddSingleton<IBarcodeScannerService, BarcodeScannerService>();
        services.AddSingleton<IHardwareMonitor, HardwareMonitorService>();
        services.AddSingleton<IPdfReceiptService, PdfReceiptService>();

        // ── View models ─────────────────────────────────────────────────────────────────────
        // Screens are transient: MainViewModel caches the instances it navigates to.
        services.AddTransient<LoginViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<SalesViewModel>();
        services.AddTransient<InvoicesViewModel>();
        services.AddTransient<ProductsViewModel>();
        services.AddTransient<InventoryViewModel>();
        services.AddTransient<ReportsViewModel>();
        services.AddTransient<ShiftViewModel>();
        services.AddTransient<HardwareSettingsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();

        // ── Windows ─────────────────────────────────────────────────────────────────────────
        services.AddTransient<LoginWindow>();
        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    /// <summary>
    /// Ends the current operator session by relaunching the terminal.
    ///
    /// A restart is intentional: it guarantees that no cart, cached view model or in-memory session
    /// state can leak from one cashier to the next, which is what an audit expects at hand-over.
    /// The relaunch is best-effort — if the executable path cannot be resolved we still shut down.
    /// </summary>
    public void RestartForOperatorChange()
    {
        // Hand the single-instance mutex over *before* the child starts, otherwise the new process
        // would see "already running" and exit immediately.
        ReleaseInstanceMutex();

        try
        {
            var processPath = Environment.ProcessPath;

            if (!string.IsNullOrWhiteSpace(processPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = processPath,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            _services?.GetService<IAppLog>()?.Warn($"Could not relaunch for sign-out: {ex.Message}");
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Shutdown(0);
    }

    private void AttachGlobalExceptionHandlers(IAppLog log)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            log.Error("Unhandled dispatcher exception.", args.Exception);

            MessageBox.Show(
                $"Something went wrong:\n\n{args.Exception.Message}\n\nThe sale in progress was not committed.",
                "Kacher POS",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            // Keep the terminal alive: a UI exception must never take the till offline mid-queue.
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            log.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.Error("Unhandled domain exception.", args.ExceptionObject as Exception);
    }

    /// <summary>Releases and drops the single-instance mutex. Idempotent.</summary>
    private void ReleaseInstanceMutex()
    {
        if (_instanceMutex is null)
        {
            return;
        }

        try
        {
            _instanceMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owner (shutdown before startup completed) — nothing to release.
        }

        _instanceMutex.Dispose();
        _instanceMutex = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseInstanceMutex();

        if (_services is { } services)
        {
            try
            {
                services.GetRequiredService<IBarcodeScannerService>().Dispose();
                services.GetRequiredService<IHardwareMonitor>().Dispose();

                // Externally-created singletons (registered by instance) are not disposed by the
                // container, so flush the log writer explicitly.
                (services.GetService<IAppLog>() as IDisposable)?.Dispose();
            }
            catch (Exception)
            {
                // shutdown best effort
            }

            (services as IDisposable)?.Dispose();
        }

        base.OnExit(e);
    }
}
