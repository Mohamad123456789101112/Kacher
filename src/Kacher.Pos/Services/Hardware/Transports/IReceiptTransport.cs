namespace Kacher.Pos.Services.Hardware.Transports;

/// <summary>
/// A byte pipe to a thermal printer. Implementations own exactly one concern — moving bytes — so
/// the ESC/POS document builder never needs to know whether the printer is on the network, on a COM
/// port or behind the Windows spooler.
/// </summary>
public interface IReceiptTransport
{
    /// <summary>Human readable transport description shown in diagnostics and the settings screen.</summary>
    string Description { get; }

    /// <summary>
    /// Sends one complete job. Implementations must be safe to call sequentially from a background
    /// thread and must honour <paramref name="ct"/> while connecting/writing.
    /// </summary>
    Task SendAsync(byte[] payload, CancellationToken ct = default);

    /// <summary>Cheap reachability check used by the hardware monitor and the commissioning screen.</summary>
    Task<TransportProbe> ProbeAsync(CancellationToken ct = default);
}

/// <summary>Result of a transport check. <see cref="Detail"/> is shown verbatim to the operator.</summary>
public sealed record TransportProbe(bool Reachable, string Detail)
{
    public static TransportProbe Ok(string detail) => new(true, detail);

    public static TransportProbe Fail(string detail) => new(false, detail);
}

/// <summary>Thrown when a transport cannot deliver a job. Wrapped by the printer service.</summary>
public sealed class TransportException : Exception
{
    public TransportException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
