using System.Windows;
using Kacher.Pos.ViewModels;

namespace Kacher.Pos.Views.Dialogs;

public partial class MessageDialog : Window
{
    public MessageDialog(MessageDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Keyboard-first: focus the input when there is one, otherwise the confirm button.
        if (DataContext is MessageDialogViewModel { ShowInput: true })
        {
            InputBox.Focus();
            InputBox.SelectAll();
        }
    }

    private void OnCloseRequested(object? sender, bool result)
    {
        DialogResult = result;
        Close();
    }

    /// <summary>Drag anywhere on the surface — the dialog has no title bar.</summary>
    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove throws when the button was already released.
            }
        }
    }
}
