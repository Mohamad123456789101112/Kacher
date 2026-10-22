using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Services.Hardware.Scanner;
using Kacher.Pos.ViewModels;
using MaterialDesignThemes.Wpf;

namespace Kacher.Pos.Views;

/// <summary>
/// The shell window.
///
/// Responsibilities that belong to the window (not the view model):
/// * borderless chrome: drag, double-click maximise, work-area clamping;
/// * keyboard wedge capture — a hardware scanner is a keyboard, so this is the only place that can
///   see its keystrokes regardless of which control has focus;
/// * toast hosting for <see cref="IUserNotifier"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IBarcodeScannerService _scanner;
    private readonly IUserNotifier _notifier;
    private readonly DispatcherTimer _scanFlushTimer;

    public MainWindow(
        MainViewModel viewModel,
        IBarcodeScannerService scanner,
        IUserNotifier notifier,
        IShellService shell)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _scanner = scanner;
        _notifier = notifier;

        DataContext = viewModel;

        shell.MinimizeRequested += (_, _) => WindowState = WindowState.Minimized;
        shell.ToggleMaximizeRequested += (_, _) => ToggleMaximize();
        shell.CloseRequested += (_, _) => Close();
        shell.SignOutRequested += OnSignOutRequested;

        Toast.MessageQueue = new SnackbarMessageQueue(TimeSpan.FromSeconds(3.5));
        _notifier.NotificationRaised += OnNotificationRaised;

        // Scanners without a trailing Enter are resolved by "no keystroke for N ms".
        _scanFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _scanFlushTimer.Tick += (_, _) => _scanner.FlushIfIdle();
        _scanFlushTimer.Start();

        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ClampToWorkArea();

        try
        {
            await _viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            // A shell that fails to finish loading must still leave a usable window.
            _notifier.Error($"The terminal could not finish loading: {ex.Message}");
        }
    }

    // ── Chrome ───────────────────────────────────────────────────────────────────────────────

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released before DragMove started — harmless.
        }
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        // A borderless maximised window covers the taskbar unless we clamp to the work area.
        ClampToWorkArea();
    }

    private void ClampToWorkArea()
    {
        if (WindowState == WindowState.Maximized)
        {
            MaxHeight = SystemParameters.WorkArea.Height + 8;
            MaxWidth = SystemParameters.WorkArea.Width + 8;
        }
        else
        {
            MaxHeight = double.PositiveInfinity;
            MaxWidth = double.PositiveInfinity;
        }
    }

    // ── Keyboard-wedge barcode capture ───────────────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Shell shortcuts always win.
        if (e.Key == Key.F1)
        {
            _viewModel.OpenDrawerCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F2)
        {
            FocusScannerField();
            e.Handled = true;
            return;
        }

        // Text inputs own their keystrokes: the scan field handles Enter itself and normal forms
        // must not be polluted by the wedge detector.
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox)
        {
            return;
        }

        var character = MapToCharacter(e);
        if (character is not null)
        {
            _scanner.FeedKey(character.Value);
            return;
        }

        if (e.Key is Key.Enter or Key.Tab)
        {
            _scanner.FeedTerminator();
        }
    }

    private static char? MapToCharacter(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is >= Key.D0 and <= Key.D9)
        {
            return (char)('0' + (key - Key.D0));
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return (char)('0' + (key - Key.NumPad0));
        }

        if (key is >= Key.A and <= Key.Z)
        {
            return (char)('A' + (key - Key.A));
        }

        return key switch
        {
            Key.OemMinus or Key.Subtract => '-',
            Key.OemPlus => '+',
            Key.Decimal or Key.OemPeriod => '.',
            _ => null
        };
    }

    private void FocusScannerField()
    {
        // Find the visual descendant TextBox tagged as the scan field and focus it.
        if (FindScannerBox(this) is { } box)
        {
            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
        }
    }

    private static TextBox? FindScannerBox(DependencyObject root)
    {
        if (root is TextBox { Tag: "BarcodeInput" } match)
        {
            return match;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScannerBox(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // ── Notifications / session ──────────────────────────────────────────────────────────────

    private void OnNotificationRaised(object? sender, UserNotification notification)
    {
        // The Snackbar itself marshals to the dispatcher, but guard anyway for I/O thread callers.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnNotificationRaised(sender, notification)));
            return;
        }

        Toast.MessageQueue?.Enqueue(notification.Message);
    }

    private void OnSignOutRequested(object? sender, EventArgs e)
    {
        // A hard restart is deliberate: it guarantees no cart, cache or session state leaks from one
        // cashier to the next, which is exactly what an audit would expect at shift hand-over.
        ((App)Application.Current).RestartForOperatorChange();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _scanFlushTimer.Stop();
        _notifier.NotificationRaised -= OnNotificationRaised;
        _viewModel.Dispose();
    }
}
