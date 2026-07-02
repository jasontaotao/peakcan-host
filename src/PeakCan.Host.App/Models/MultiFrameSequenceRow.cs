using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Models;

/// <summary>
/// v2.1.0 MINOR: one row in the multi-frame send window's DataGrid.
/// v2.1.1 PATCH: extended with <see cref="Kind"/> so a single window
/// can dispatch both raw CAN frames (manually entered ID/Data/flags)
/// AND DBC messages (selected from a loaded DBC document, with
/// per-signal engineering values).
///
/// <para>
/// All properties are observable so the DataGrid reflects edits
/// immediately and the underlying <see cref="CanFrame"/> stays in
/// sync. <see cref="Build"/> dispatches on Kind: raw rows build
/// directly from the entered fields; DBC rows require an external
/// <see cref="DbcEncodeService"/> (passed in by the caller — see
/// <c>SequenceSendService.SendAsync</c>) to encode the message.
/// </para>
/// </summary>
public sealed partial class MultiFrameSequenceRow : ObservableObject
{
    /// <summary>Row payload kind.</summary>
    public enum Kind
    {
        /// <summary>Raw CAN frame: ID/DataHex/Ext/FD/RTR/BRS/ESI directly editable.</summary>
        Raw = 0,
        /// <summary>DBC message: MessageName + per-signal engineering values.</summary>
        Dbc = 1,
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRaw))]
    [NotifyPropertyChangedFor(nameof(IsDbc))]
    private Kind _rowKind = Kind.Raw;

    public bool IsRaw => RowKind == Kind.Raw;
    public bool IsDbc => RowKind == Kind.Dbc;

    // ----- Raw fields -----
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

    // ----- DBC fields (v2.1.1) -----

    /// <summary>
    /// Name of the DBC message to send. Must match a
    /// <see cref="Message.Name"/> in the currently loaded DBC document.
    /// Looked up by the SequenceSendService against
    /// <c>DbcService.Current?.Messages</c>.
    /// </summary>
    [ObservableProperty] private string _dbcMessageName = "";

    /// <summary>
    /// Per-signal engineering values to encode. Key = signal name,
    /// value = engineering value (matches the existing
    /// <see cref="DbcEncodeService"/> contract).
    ///
    /// <para>
    /// Backing storage is an <see cref="ObservableCollection{T}"/> of
    /// (Name, Value) pairs so the DataGrid editor can add / remove
    /// rows. The service translates this into a
    /// <see cref="Dictionary{TKey, TValue}"/> at encode time.
    /// </para>
    /// </summary>
    public ObservableCollection<DbcSignalValue> DbcSignalValues { get; } = new();

    partial void OnRowKindChanged(Kind value)
    {
        // Sensible defaults when switching kinds — saves the user
        // from blank fields they have to fill in by hand.
        if (value == Kind.Dbc && string.IsNullOrEmpty(DbcMessageName))
        {
            DbcSignalValues.Clear();
        }
    }

    /// <summary>
    /// Build a raw <see cref="CanFrame"/> from this row's raw
    /// fields. Only valid when <see cref="Kind"/> == <see cref="Kind.Raw"/>.
    /// Throws <see cref="FormatException"/> on invalid hex data and
    /// <see cref="ArgumentOutOfRangeException"/> on invalid ID —
    /// caller surfaces these to the user via the status panel.
    /// </summary>
    public CanFrame Build()
    {
        var bytes = ParseHex(DataHex);
        var raw = IsExtended ? (uint)Id : Id;
        var canId = new CanId(raw, IsExtended ? FrameFormat.Extended : FrameFormat.Standard);
        var flags = FrameFlags.None;
        if (IsFd) flags |= FrameFlags.Fd;
        if (IsRtr) flags |= FrameFlags.Rtr;
        if (IsBitRateSwitch) flags |= FrameFlags.BitRateSwitch;
        if (IsErrorStateIndicator) flags |= FrameFlags.ErrorStateIndicator;
        return new CanFrame(canId, bytes, flags, ChannelId.None, default);
    }

    /// <summary>
    /// v2.1.1 PATCH: parse hex string into bytes. Accepts optional
    /// spaces and dashes as separators (mirrors
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

/// <summary>
/// v2.1.1 PATCH: one (signal name, engineering value) pair in a
/// <see cref="MultiFrameSequenceRow.DbcSignalValues"/> collection.
/// Observable so the DataGrid's two-way binding keeps the value
/// up to date as the user types.
/// </summary>
public sealed partial class DbcSignalValue : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private double? _value;
}