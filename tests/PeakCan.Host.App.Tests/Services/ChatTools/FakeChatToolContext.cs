using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

/// <summary>
/// In-memory fake <see cref="IChatToolContext"/> for unit-testing chat tools
/// without WPF or a real VM. Records every mutating call so tests can assert
/// invocation order and arguments.
/// </summary>
internal sealed class FakeChatToolContext : IChatToolContext
{
    public double AnchorTimestampSeconds { get; set; } = double.NaN;
    public double BlueAnchorTimestampSeconds { get; set; } = double.NaN;
    public DbcDocument? CurrentDbc { get; set; }
    public List<WatchedSignalRow> WatchedSignals { get; } = new();
    IReadOnlyList<WatchedSignalRow> IChatToolContext.WatchedSignals => WatchedSignals;

    public List<WatchedSignalRow> AddedRows { get; } = new();
    public List<double> RefreshAtAnchorCalls { get; } = new();
    public List<double> RefreshAtAnchorBlueCalls { get; } = new();
    public List<double> SeekCalls { get; } = new();
    public bool SeekResult { get; set; } = true;

    public void AddWatchedSignals(IEnumerable<WatchedSignalRow> rows) => AddedRows.AddRange(rows);
    public void RefreshAtAnchor(double timestampSeconds) => RefreshAtAnchorCalls.Add(timestampSeconds);
    public void RefreshAtAnchorBlue(double timestampSeconds) => RefreshAtAnchorBlueCalls.Add(timestampSeconds);
    public bool Seek(double timestampSeconds)
    {
        SeekCalls.Add(timestampSeconds);
        return SeekResult;
    }
}
