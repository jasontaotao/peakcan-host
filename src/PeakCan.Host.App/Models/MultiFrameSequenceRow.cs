using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.Models;

/// <summary>
/// v2.1.0 MINOR: one row in the multi-frame send window's DataGrid.
/// Holds a single CAN frame definition editable in place. The XAML
/// DataGrid binds TwoWay to the string/int/bool properties; the
/// <see cref="BuildAsync"/> method converts the edited state into a
/// concrete <see cref="CanFrame"/> for the send pipeline.
///
/// All properties are observable so the DataGrid reflects edits
/// immediately and the underlying CanFrame stays in sync.
/// </summary>
public sealed partial class MultiFrameSequenceRow : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IdHexDisplay))]
    private ushort _id;

    [ObservableProperty]
    private string _dataHex = "";

    [ObservableProperty] private bool _isExtended;
    [ObservableProperty] private bool _isFd;
    [ObservableProperty] private bool _isRtr;
    [ObservableProperty] private bool _isBitRateSwitch;
    [ObservableProperty] private bool _isErrorStateIndicator;

    /// <summary>Convenience: <c>"0x{Id:X}"</c> for the DataGrid display.</summary>
    public string IdHexDisplay => $"0x{Id:X}";

    /// <summary>
    /// Build a <see cref="CanFrame"/> from the current row state. Throws
    /// <see cref="FormatException"/> on invalid hex data — caller
    /// (typically the VM) is expected to surface the error to the user
    /// via the status panel.
    /// </summary>
    public CanFrame Build()
    {
        var bytes = ParseHex(DataHex);
        var raw = IsExtended ? (uint)Id : Id;
        // RTR is only valid for classic CAN; caller-side validation
        // mirrors SendViewModel.SendAsync (RTR + FD combo rejected).
        var canId = new CanId(raw, IsExtended ? FrameFormat.Extended : FrameFormat.Standard);
        var flags = FrameFlags.None;
        if (IsFd) flags |= FrameFlags.Fd;
        if (IsRtr) flags |= FrameFlags.Rtr;
        if (IsBitRateSwitch) flags |= FrameFlags.BitRateSwitch;
        if (IsErrorStateIndicator) flags |= FrameFlags.ErrorStateIndicator;
        return new CanFrame(canId, bytes, flags, ChannelId.None, default);
    }

    /// <summary>
    /// v2.1.0 MINOR helper: parse hex string into bytes. Accepts
    /// optional spaces and dashes as separators (mirrors
    /// <c>SendViewModel.ParseHex</c>); pads odd-length input with a
    /// leading zero. Empty input → empty bytes (zero DLC).
    /// </summary>
    private static byte[] ParseHex(string s)
    {
        var stripped = (s ?? "").Replace(" ", string.Empty).Replace("-", string.Empty);
        if (stripped.Length == 0) return Array.Empty<byte>();
        if ((stripped.Length & 1) == 1) stripped = "0" + stripped;
        var bytes = new byte[stripped.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(stripped.Substring(i * 2, 2), 16);
        }
        return bytes;
    }
}