namespace PeakCan.Host.Core.Replay;

public static partial class BlfParser
{
    // v3.51.0 MINOR: object stream parse loop is inline in BlfParser.ParseAsync.
    // This partial is reserved for future refactor (e.g. moving the
    // LOBJ scan + ObjectHeaderBase parse + dispatch into a dedicated flow file).
}
