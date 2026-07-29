namespace PeakCan.Host.Core.HIL.Diff;

/// <summary>
/// Diff engine interface. Compare two frame sequences at configured granularity.
/// </summary>
public interface IDiffEngine
{
    /// <summary>
    /// Compare two frame sequences.
    /// Memory constraint: inputs are fully loaded IReadOnlyList.
    /// Current implementation assumes trace ≤ 1M frames (~128MB double-buffer).
    /// Future: IAsyncEnumerable for streaming diff.
    /// Config.Validate() must be called before diffing to catch invalid configurations.
    /// </summary>
    DiffResult Diff(IReadOnlyList<CanFrame> golden, IReadOnlyList<CanFrame> actual, DiffConfig config);
}
