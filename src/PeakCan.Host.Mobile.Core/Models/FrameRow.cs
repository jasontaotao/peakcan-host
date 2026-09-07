using System.Globalization;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Mobile.Core.Models;

/// <summary>
/// Immutable display projection of one <see cref="ReplayFrame"/> for table cells.
/// Display strings are cached because CollectionView may re-read them during
/// layout; formatting on every getter would add avoidable allocation churn.
/// </summary>
public sealed record FrameRow(double Timestamp, uint Id, bool IsExtended, byte Dlc, byte[] Data)
{
    public string TimeText { get; } = Timestamp.ToString("F6", CultureInfo.InvariantCulture);

    public string IdText { get; } = IsExtended
        ? Id.ToString("X8", CultureInfo.InvariantCulture)
        : Id.ToString("X3", CultureInfo.InvariantCulture);

    public string DataText { get; } = BuildDataText(Dlc, Data);

    /// <summary>Project a parsed frame into a display row.</summary>
    public static FrameRow FromReplayFrame(ReplayFrame f) => new(f.Timestamp, f.Id, f.IsExtended, f.Dlc, f.Data);

    private static string BuildDataText(byte dlc, byte[] data)
    {
        var n = Math.Min(dlc, data.Length);
        if (n == 0) return string.Empty;

        var chars = new char[n * 3 - 1];
        for (int i = 0; i < n; i++)
        {
            chars[i * 3] = HexChar(data[i] >> 4);
            chars[i * 3 + 1] = HexChar(data[i] & 0xF);
            if (i + 1 < n) chars[i * 3 + 2] = ' ';
        }

        return new string(chars);
    }

    private static char HexChar(int value) => (char)(value < 10 ? '0' + value : 'A' + value - 10);
}
