namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// v12 Step 0: DTOs for chat tool context queries. Returned by
/// <see cref="IChatToolContext.GetTraceInfo"/> and
/// <see cref="IChatToolContext.GetDbcInfo"/>.
/// </summary>

/// <summary>Trace session metadata snapshot for the AI assistant.</summary>
public sealed record TraceInfo(
    double TotalDuration,
    int SourceCount,
    bool DbcLoaded,
    string? DbcPath,
    double CurrentTimestamp,
    DateTime? WallClockOrigin,
    IReadOnlyList<TraceSourceInfo> Sources);

/// <summary>One loaded trace source within the session.</summary>
public sealed record TraceSourceInfo(
    string SourceId,
    string DisplayName,
    string Path,
    int FrameCount,
    string? CanIdFilter);

/// <summary>DBC document summary for the AI assistant.</summary>
public sealed record DbcInfo(
    string? Version,
    int MessageCount,
    int SignalCount,
    IReadOnlyList<string> Nodes,
    string? SourcePath);
