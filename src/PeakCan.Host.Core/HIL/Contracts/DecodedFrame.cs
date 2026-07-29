namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>
/// Decoded frame with signal snapshot.
/// Signals dict contains ONLY signals from the current frame's matched message.
/// Key format: "MessageName.SignalName".
/// If frame matches no DBC message, Signals is empty.
/// </summary>
public sealed record DecodedFrame(
    CanFrame Frame,
    IReadOnlyDictionary<string, double> Signals);
