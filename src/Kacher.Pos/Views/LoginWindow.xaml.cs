using System.Windows;
using System.Windows.Input;
using Kacher.Pos.Models;
using Kacher.Pos.ViewModels;

namespace Kacher.Pos.Views;

/// <summary>
/// Hosts <see cref="LoginView"/>. The window owns dialog lifetime and keyboard routing; the view
/// model owns authentication and never knows it is in a modal dialog.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly LoginViewModel _viewModel;

    public LoginWindow(LoginViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.SignedIn += OnSignedIn;
        Loaded += (_, _) => Focus();
    }

    private void OnSignedIn(object? sender, User user)
    {
        // Close from the dispatcher so the modal loop unwinds cleanly.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            DialogResult = true;
            Close();
        }));
    }

    /// <summary>Physical keyboard support for the PIN pad (numeric row, numpad, Enter, Backspace).</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case >= Key.D0 and <= Key.D9:
                _viewModel.AppendDigitCommand.Execute(((char)('0' + (e.Key - Key.D0))).ToString());
                e.Handled = true;
                break;

            case >= Key.NumPad0 and <= Key.NumPad9:
                _viewModel.AppendDigitCommand.Execute(((char)('0' + (e.Key - Key.NumPad0))).ToString());
                e.Handled = true;
                break;

            case Key.Back:
                _viewModel.BackspaceCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Enter:
                _viewModel.SignInCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Escape:
                _viewModel.ClearCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
