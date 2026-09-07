using System.Globalization;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Mobile.Core.Models;

/// <summary>Display projection of one <see cref="ReplayFrame"/> for the table cell.</summary>
public sealed record FrameRow(double Timestamp, uint Id, bool IsExtended, byte Dlc, byte[] Data)
{
    public string TimeText => Timestamp.ToString("F6", CultureInfo.InvariantCulture);
    public string IdText => IsExtended ? Id.ToString("X8", CultureInfo.InvariantCulture)
                                       : Id.ToString("X3", CultureInfo.InvariantCulture);

    public string DataText
    {
        get
        {
            int n = Math.Min(Dlc, Data.Length);
            if (n == 0) return string.Empty;
            var chars = new char[n * 3 - 1];
            for (int i = 0; i < n; i++)
            {
                byte b = Data[i];
                chars[i * 3] = HexChar(b >> 4);
                chars[i * 3 + 1] = HexChar(b & 0xF);
                if (i < n - 1) chars[i * 3 + 2] = ' ';
            }
            return new string(chars);
        }
    }

    /// <summary>Project a parsed <see cref="ReplayFrame"/> into a display row.</summary>
    public static FrameRow FromReplayFrame(ReplayFrame f) => new(f.Timestamp, f.Id, f.IsExtended, f.Dlc, f.Data);

    private static char HexChar(int v) => (char)(v < 10 ? '0' + v : 'A' + v - 10);
}
