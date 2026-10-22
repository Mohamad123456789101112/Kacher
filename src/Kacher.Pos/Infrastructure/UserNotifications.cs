using System.Windows;

namespace Kacher.Pos.Infrastructure;

public enum NotificationKind
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record UserNotification(string Message, NotificationKind Kind, DateTime Timestamp)
{
    public TimeSpan Duration => Kind == NotificationKind.Error ? TimeSpan.FromSeconds(7) : TimeSpan.FromSeconds(3.5);
}

/// <summary>
/// Decouples "show a toast" from the view. The shell subscribes and feeds a MaterialDesign Snackbar,
/// so view models never reference a Window.
/// </summary>
public interface IUserNotifier
{
    event EventHandler<UserNotification>? NotificationRaised;

    void Info(string message);

    void Success(string message);

    void Warn(string message);

    void Error(string message);
}

public sealed class UserNotifier : IUserNotifier
{
    private readonly IAppLog _log;

    public UserNotifier(IAppLog log) => _log = log;

    public event EventHandler<UserNotification>? NotificationRaised;

    public void Info(string message) => Publish(message, NotificationKind.Info);

    public void Success(string message) => Publish(message, NotificationKind.Success);

    public void Warn(string message) => Publish(message, NotificationKind.Warning);

    public void Error(string message) => Publish(message, NotificationKind.Error);

    private void Publish(string message, NotificationKind kind)
    {
        if (kind is NotificationKind.Error or NotificationKind.Warning)
        {
            _log.Warn($"[{kind}] {message}");
        }
        else
        {
            _log.Info(message);
        }

        var notification = new UserNotification(message, kind, DateTime.Now);

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => NotificationRaised?.Invoke(this, notification)));
            return;
        }

        NotificationRaised?.Invoke(this, notification);
    }
}
