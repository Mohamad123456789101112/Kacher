using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kacher.Pos.ViewModels;

/// <summary>Backs the single reusable modal used for confirmations and short numeric prompts.</summary>
public sealed partial class MessageDialogViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "Kacher POS";
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string _confirmText = "OK";
    [ObservableProperty] private string _cancelText = "Cancel";
    [ObservableProperty] private bool _showCancel;
    [ObservableProperty] private bool _showInput;
    [ObservableProperty] private string _inputLabel = string.Empty;
    [ObservableProperty] private string _inputValue = string.Empty;
    [ObservableProperty] private bool _isNumericInput;
    [ObservableProperty] private string? _validationError;

    /// <summary>Set by the view so Enter/Escape and the buttons can close the window.</summary>
    public bool? Result { get; private set; }

    public event EventHandler<bool>? CloseRequested;

    [RelayCommand]
    private void Confirm()
    {
        if (ShowInput && IsNumericInput)
        {
            if (!decimal.TryParse(InputValue, out var value) || value < 0)
            {
                ValidationError = "Enter a valid non-negative amount.";
                return;
            }

            // Normalise so callers always parse a culture-invariant decimal.
            InputValue = value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (ShowInput && string.IsNullOrWhiteSpace(InputValue))
        {
            ValidationError = "A value is required.";
            return;
        }

        ValidationError = null;
        Result = true;
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = false;
        CloseRequested?.Invoke(this, false);
    }
}
