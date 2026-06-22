using PeakCan.Host.Core;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// One row in the Trace view. Plain DTO: all properties are
/// <c>init</c>-only, populated once at construction by
/// <see cref="TraceViewModel.AppendBatchAsync"/>.
/// <para>
/// The <see cref="DataHex"/> string is pre-formatted here so the WPF
/// DataGrid binding does not need a <c>IValueConverter</c> — keeping the
/// view-model layer free of UI-framework concerns.
/// </para>
/// </summary>
public sealed class TraceEntry
{
    /// <summary>Frame timestamp as reported by the SDK (formatted by <see cref="Timestamp.ToString"/>).</summary>
    public Timestamp Timestamp { get; init; }

    /// <summary>Source channel handle (e.g. <c>0x51</c> for PCAN-USB FD first channel).</summary>
    public ChannelId Channel { get; init; }

    /// <summary>CAN identifier (Standard 11-bit or Extended 29-bit).</summary>
    public CanId Id { get; init; }

    /// <summary>Data length code in bytes. 0–8 for classic CAN, 0–64 for CAN FD.</summary>
    public byte Dlc { get; init; }

    /// <summary>Payload as contiguous uppercase hex bytes (e.g. "DEADBEEF"). Empty string when <see cref="Dlc"/> is 0.</summary>
    public string DataHex { get; init; } = "";

    /// <summary>DBC-decoded signal values; empty until a DBC is loaded (Task 15).</summary>
    public string Decoded { get; init; } = "";

    /// <summary>True iff this row is a hardware-reported bus error frame.</summary>
    public bool IsError { get; init; }

    /// <summary>True iff this row uses the CAN FD frame format (up to 64-byte payloads).</summary>
    public bool IsFd { get; init; }
}
