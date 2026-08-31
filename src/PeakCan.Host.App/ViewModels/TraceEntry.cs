using System.ComponentModel;
using PeakCan.HIL.Core;

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
/// <b>v0.9.2 / 2026-08-31:</b> 多色高亮由 <see cref="HighlightColorIndex"/>
/// (int，-1=无高亮) 承载（原 <c>IsHighlighted</c> bool 升级）。Fires
/// <see cref="PropertyChanged"/> when changed.
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

    /// <summary>
    /// Original payload byte copy (入列时 <c>f.Data.ToArray()</c> 拷贝)。payload
    /// 模式过滤与"高亮规则变更后全量重算"都需要——仅 <see cref="DataHex"/> 字符串
    /// 不够用。不可变（<c>init</c>-only），谓词只读。
    /// </summary>
    public byte[] Data { get; init; } = Array.Empty<byte>();

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
    /// 多色高亮索引（0..5 = 调色板某色，-1 = 无高亮）。高亮规则求值
    /// （<c>EvaluateHighlight</c>）为每行计算，视图据此上底色。INPC 语义同
    /// <see cref="Decoded"/>（仅值变化触发，避免 DataGrid 无谓重绘）。
    /// </summary>
    public int HighlightColorIndex
    {
        get => _highlightColorIndex;
        set
        {
            if (_highlightColorIndex != value)
            {
                _highlightColorIndex = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HighlightColorIndex)));
            }
        }
    }
    private int _highlightColorIndex = -1;

    /// <summary>Fires when a mutable property (e.g. <see cref="Decoded"/>/<see cref="HighlightColorIndex"/>) changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
}
