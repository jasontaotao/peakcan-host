namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// v12 Step 0: a named group of watched signals for organizing
/// diagnostic findings (e.g. "欠压分析" containing voltage + fault +
/// power signals). Groups are independent of <see cref="WatchedSignalRow"/>
/// membership - a signal can be in the watch list but not in any group.
/// <para>
/// Immutable record; mutations (add/remove signals, set notes) replace
/// the entire record in the owning <c>ObservableCollection</c> via
/// <c>with</c> expressions, keeping INPC simple.
/// </para>
/// <para>
/// Persisted via <c>TraceSessionBundleDto.Groups</c> (Step 7).
/// </para>
/// </summary>
public sealed record WatchedSignalGroup(
    string Id,
    string Name,
    string? Notes,
    IReadOnlyList<string> SignalKeys);
