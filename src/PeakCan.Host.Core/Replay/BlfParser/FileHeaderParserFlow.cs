namespace PeakCan.Host.Core.Replay;

public static partial class BlfParser
{
    // v3.51.0 MINOR: file header parsing is inline in BlfParser.ParseAsync.
    // This partial is reserved for future refactor (e.g. moving header
    // read + FileStatistics decode into a dedicated flow file).
}
