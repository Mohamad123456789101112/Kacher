using CommunityToolkit.Mvvm.ComponentModel;
using Kacher.Pos.Infrastructure;

namespace Kacher.Pos.ViewModels;

/// <summary>
/// Shared plumbing for screen view models: busy flag, error surface and a guarded async runner.
///
/// The <see cref="RunAsync"/> helper is the single place where view-model work meets the UI thread:
/// it never throws (so an unobserved task exception cannot kill the app), it always releases the
/// busy flag, and it reports failures through <see cref="IUserNotifier"/> instead of a MessageBox.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _title = string.Empty;

    protected IUserNotifier Notifier { get; }

    protected ViewModelBase(IUserNotifier notifier) => Notifier = notifier;

    /// <summary>Called once when the shell navigates to the view.</summary>
    public virtual Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Called when the shell navigates away — cancels timers/polling.</summary>
    public virtual Task UnloadAsync() => Task.CompletedTask;

    protected async Task RunAsync(Func<CancellationToken, Task> action, string? failureMessage = null)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            await action(CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // user navigated away / cancelled: not an error
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Notifier.Error(failureMessage ?? ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
