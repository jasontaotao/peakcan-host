// SequenceLibrary/StaticHelpers.partial.cs — W33 T3 (Flow C, 5 LoC)
// Private static helper: DefaultPath resolves
// %APPDATA%\PeakCan.Host\sequences.json. Sister of W22 RecordService
// StaticHelpers + W27 RecentSessionsService StaticHelpers + W29
// SendFrameLibrary StaticHelpers default-path pattern.
//
// W23 STRUCT-FABRICATION LESSON: Environment.GetFolderPath 1-arg +
// Path.Combine 3-arg overload signatures verified during verbatim
// re-extraction from HEAD.
//
// W33 T3 verbatim re-extracted via `git show main:src/.../SequenceLibrary.cs | sed -n '234,238p'`
// per W20 T2 R1 fabrication LESSON (43rd application).

using System.IO;

namespace PeakCan.Host.App.Services.Sequence;

public sealed partial class SequenceLibrary
{
    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PeakCan.Host", "sequences.json");
    }
}
