namespace Kacher.Pos.Infrastructure;

/// <summary>
/// Window-level operations requested by view models. The shell (MainWindow) subscribes and performs
/// them, so no view model ever holds a reference to a Window.
/// </summary>
public interface IShellService
{
    event EventHandler? MinimizeRequested;
    event EventHandler? ToggleMaximizeRequested;
    event EventHandler? CloseRequested;
    event EventHandler? SignOutRequested;

    void Minimize();
    void ToggleMaximize();
    void Close();
    void SignOut();
}

public sealed class ShellService : IShellService
{
    public event EventHandler? MinimizeRequested;
    public event EventHandler? ToggleMaximizeRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? SignOutRequested;

    public void Minimize() => MinimizeRequested?.Invoke(this, EventArgs.Empty);

    public void ToggleMaximize() => ToggleMaximizeRequested?.Invoke(this, EventArgs.Empty);

    public void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void SignOut() => SignOutRequested?.Invoke(this, EventArgs.Empty);
}
