using System.Windows;
using Kacher.Pos.ViewModels;
using Kacher.Pos.Views.Dialogs;

namespace Kacher.Pos.Services;

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message, string confirmText = "Confirm", string cancelText = "Cancel");

    /// <summary>Numeric text prompt. Returns null when cancelled.</summary>
    Task<string?> PromptAmountAsync(string title, string label, decimal initialValue = 0m, string confirmText = "OK");

    Task<string?> PromptTextAsync(string title, string label, string? initialValue = null, string confirmText = "OK");
}

/// <summary>
/// Thin wrapper over the modal window so view models stay free of WPF types and remain unit-testable
/// with a fake implementation.
/// </summary>
public sealed class DialogService : IDialogService
{
    public async Task ShowMessageAsync(string title, string message)
    {
        var vm = new MessageDialogViewModel
        {
            Title = title,
            Message = message,
            ConfirmText = "Got it",
            ShowCancel = false
        };

        await ShowAsync(vm).ConfigureAwait(true);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "Confirm", string cancelText = "Cancel")
    {
        var vm = new MessageDialogViewModel
        {
            Title = title,
            Message = message,
            ConfirmText = confirmText,
            CancelText = cancelText,
            ShowCancel = true
        };

        await ShowAsync(vm).ConfigureAwait(true);
        return vm.Result == true;
    }

    public async Task<string?> PromptAmountAsync(string title, string label, decimal initialValue = 0m, string confirmText = "OK")
    {
        var vm = new MessageDialogViewModel
        {
            Title = title,
            Message = "Enter the amount below.",
            InputLabel = label,
            InputValue = initialValue == 0 ? string.Empty : initialValue.ToString("0.00"),
            ShowInput = true,
            IsNumericInput = true,
            ShowCancel = true,
            ConfirmText = confirmText
        };

        await ShowAsync(vm).ConfigureAwait(true);
        return vm.Result == true ? vm.InputValue : null;
    }

    public async Task<string?> PromptTextAsync(string title, string label, string? initialValue = null, string confirmText = "OK")
    {
        var vm = new MessageDialogViewModel
        {
            Title = title,
            Message = string.Empty,
            InputLabel = label,
            InputValue = initialValue ?? string.Empty,
            ShowInput = true,
            IsNumericInput = false,
            ShowCancel = true,
            ConfirmText = confirmText
        };

        await ShowAsync(vm).ConfigureAwait(true);
        return vm.Result == true ? vm.InputValue : null;
    }

    private static Task ShowAsync(MessageDialogViewModel vm)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null)
        {
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(() =>
        {
            var dialog = new MessageDialog(vm)
            {
                Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                       ?? Application.Current.MainWindow
            };

            dialog.ShowDialog();
        }).Task;
    }
}
