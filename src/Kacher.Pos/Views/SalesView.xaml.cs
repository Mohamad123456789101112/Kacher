using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Kacher.Pos.Views;

public partial class SalesView : UserControl
{
    public SalesView()
    {
        InitializeComponent();

        // The scan field owns focus so a keyboard-wedge scanner "types" straight into it.
        // DispatcherPriority.Input defers until the visual tree is ready.
        Loaded += (_, _) => Dispatcher.BeginInvoke(
            new Action(FocusScanBox), DispatcherPriority.Input);

        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                FocusScanBox();
            }
        };
    }

    /// <summary>Puts the caret back in the scanner field (used after a sale and by the F2 shortcut).</summary>
    public void FocusScanBox()
    {
        if (ScanBox is null)
        {
            return;
        }

        ScanBox.Focus();
        ScanBox.CaretIndex = ScanBox.Text?.Length ?? 0;
    }
}
