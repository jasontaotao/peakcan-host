// SendFrameLibrary/StaticHelpers.partial.cs — W29 T3 (Flow C, 5 LoC)
// Private static helper: DefaultPath resolves
// %APPDATA%\PeakCan.Host\send-library.json. Sister of W22
// RecordService StaticHelpers + W27 RecentSessionsService
// StaticHelpers + W28 DbcService StaticHelpers default-path pattern.
//
// W23 STRUCT-FABRICATION LESSON: verify Environment.GetFolderPath
// 1-arg + Path.Combine 2-arg signatures (verified during verbatim
// re-extraction from HEAD).
//
// W29 T3 verbatim re-extracted via `git show HEAD:src/.../SendFrameLibrary.cs | sed -n '108,112p'`
// per W20 T2 R1 fabrication LESSON (34th application).

using System.IO;

namespace PeakCan.Host.App.Services;

public sealed partial class SendFrameLibrary
{
    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PeakCan.Host", "send-library.json");
    }
}
