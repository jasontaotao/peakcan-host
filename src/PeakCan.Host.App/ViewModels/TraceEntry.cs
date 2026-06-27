using System.ComponentModel;
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
/// <para>
/// <b>v0.9.2:</b> <see cref="IsHighlighted"/> is mutable for
/// highlight-on-match functionality. Fires
/// <see cref="PropertyChanged"/> when toggled.
/// </para>
/// </summary>
public sealed class TraceEntry : INotifyPropertyChanged
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
    public string Decoded
    {
        get => _decoded;
        set
        {
            if (_decoded != value)
            {
                _decoded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Decoded)));
            }
        }
    }
    private string _decoded = "";

    /// <summary>True iff this row is a hardware-reported bus error frame.</summary>
    public bool IsError { get; init; }

    /// <summary>True iff this row uses the CAN FD frame format (up to 64-byte payloads).</summary>
    public bool IsFd { get; init; }

    /// <summary>True iff this row is an RTR (Remote Transmission Request) frame.</summary>
    public bool IsRtr { get; init; }

    /// <summary>Frame type display string: "ERR", "RTR", "FD", or "" (standard data frame).</summary>
    public string FrameType => IsError ? "ERR" : IsRtr ? "RTR" : IsFd ? "FD" : "";

    /// <summary>
    /// Whether this row is highlighted (e.g. matching a highlight filter).
    /// Used by the view to apply a background color.
    /// </summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set
        {
            if (_isHighlighted != value)
            {
                _isHighlighted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
            }
        }
    }
    private bool _isHighlighted;

    /// <summary>Fires when <see cref="IsHighlighted"/> changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
}
