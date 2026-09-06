namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>
/// Offline signal history (pull model). Used by diff engine.
/// </summary>
public interface ISignalHistory
{
    /// <summary>Get signal samples in [startTime, endTime] time window.</summary>
    IReadOnlyList<(double Timestamp, double Value)> GetSignalSamples(
        string name, double startTime, double endTime);

    /// <summary>All known signal names.</summary>
    IReadOnlyList<string> KnownSignals { get; }
}
