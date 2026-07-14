// src/PeakCan.Host.Core/Replay/AscFormat.cs — v3.49.0 MINOR (T1 of 3)
// Q3: writer + parser 都依赖的 ASC 格式单源。
// Waveform: 静态类提供 WriteHeader/WriteFooter/WriteDataLine (writer 端)
// + TryParseDataLine/TryParseDateHeader/LineIsSectionDelimiter/FormatFlagsCompact (parser 端)。
//
// W23 STRUCT-FABRACTION LESSON: 已验证签名 — StreamWriter.WriteLine 1-arg +
// WriteLine() 0-arg + Convert.ToHexString(ReadOnlySpan<byte>) 1-arg +
// CanFrame.IsFd/IsError/Flags/Channel.Handle/Id.Raw/Dlc/Data 属性 +
// FrameFlags 5 个 bitflag 值 + ChannelId.Handle ushort +
// ReplayFrame(double, uint, byte, byte[], FrameFlags) 5-arg ctor +
// DateTime.ToString("ddd MMM dd HH:mm:ss yyyy") + TimeSpan.TotalSeconds +
// TryParse 4-arg 重载。
//
// 与现有 Writer/Reader 兼容性: 严格保持与
// RecordService/Format.partial.cs 当前输出的 3 行 header
// (`date ...` / `base hex  timestamps absolute` / `no internal events logged`)
// + data line (`{ts:F6} {ch:X2}  {id:X}  {dlc}  {hex}{flags}`) 1:1 等价，
// 保证 round-trip test 通过。

using System.Globalization;
using PeakCan.Host.Core;

namespace PeakCan.Host.Core.Replay;

/// <summary>
/// ASC (CAN bus trace) 格式单源 — writer 和 parser 共用。
/// </summary>
public static class AscFormat
{
    /// <summary>Vector ASC 中空白分隔符的枚举: 空格 + 制表符。</summary>
    public static readonly char[] WhitespaceSeparators = { ' ', '\t' };

    /// <summary>Header line 1 格式: `date {origin}`。</summary>
    public const string HeaderDateFormat = "ddd MMM dd HH:mm:ss yyyy";

    /// <summary>Header line 2 格式: `base hex  timestamps absolute`。</summary>
    public const string HeaderBaseAbsolute = "base hex  timestamps absolute";

    /// <summary>Header line 3 格式: `no internal events logged`。</summary>
    public const string HeaderNoInternalEvents = "no internal events logged";

    /// <summary>Frame flag token 字符串定义，与 FormatFlagsCompact 输出 1:1。</summary>
    public const string FlagFd = "fd";
    public const string FlagBrs = "brs";
    public const string FlagEsi = "esi";
    public const string FlagError = "error";

    /// <summary>
    /// 写入 ASC file 的 header 3 行。Origin 通常使用录制开始时 UTC 时间。
    /// </summary>
    public static void WriteHeader(StreamWriter writer, DateTime origin)
    {
        writer.WriteLine($"date {origin.ToString(HeaderDateFormat, CultureInfo.InvariantCulture)}");
        writer.WriteLine(HeaderBaseAbsolute);
        writer.WriteLine(HeaderNoInternalEvents);
    }

    /// <summary>
    /// 写入 ASC file 末尾的 elapsed-time 注释行 + 前置空行。
    /// 与现有 RecordService 行为一致 (L31-L32)。
    /// </summary>
    public static void WriteFooter(StreamWriter writer, TimeSpan elapsed)
    {
        writer.WriteLine();
        writer.WriteLine($"// {elapsed.TotalSeconds:F3} s");
    }

    /// <summary>
    /// 写入一条 ASC data line: `{ts:F6} {ch:X2}  {id:X}  {dlc}  {hex}{flags}`。
    /// 与现有 RecordService/Format.partial.cs L54-L55 1:1 等价 (含双空格分隔)。
    /// </summary>
    public static void WriteDataLine(StreamWriter writer, CanFrame frame, TimeSpan elapsed)
    {
        var dataHex = Convert.ToHexString(frame.Data.Span);

        var fdFlag = frame.IsFd ? $"  {FlagFd}" : "";
        var brsFlag = (frame.Flags & FrameFlags.BitRateSwitch) != 0 ? $" {FlagBrs}" : "";
        var esiFlag = (frame.Flags & FrameFlags.ErrorStateIndicator) != 0 ? $" {FlagEsi}" : "";
        var errFlag = frame.IsError ? $" {FlagError}" : "";

        writer.WriteLine(
            $"{elapsed.TotalSeconds:F6} {frame.Channel.Handle:X2}  {frame.Id.Raw:X}  {frame.Dlc}  {dataHex}{fdFlag}{brsFlag}{esiFlag}{errFlag}");
    }

    /// <summary>
    /// FrameFlags → 空格分隔的小写 token 字符串。Writer 调用。
    /// </summary>
    public static string FormatFlagsCompact(FrameFlags flags, bool isError)
    {
        var parts = new List<string>(capacity: 4);
        if ((flags & FrameFlags.Fd) != 0) parts.Add(FlagFd);
        if ((flags & FrameFlags.BitRateSwitch) != 0) parts.Add(FlagBrs);
        if ((flags & FrameFlags.ErrorStateIndicator) != 0) parts.Add(FlagEsi);
        if (isError) parts.Add(FlagError);
        return string.Join(" ", parts);
    }

    /// <summary>
    /// 解析 1 行 ASC data line。语义与 DataLineParserFlow.cs L17-L171 1:1 一致:
    /// Vector convention 'x' ID 后缀 + Rx/Tx 方向预扫描 + 'd'/'l' DLC marker +
    /// 1-char single-hex + N*N 2-char hex + 奇数长度 malformed + Length/BitCount/ID metadata 终止。
    /// </summary>
    public static bool TryParseDataLine(string line, out ReplayFrame frame, out string reason)
    {
        frame = default!;
        reason = string.Empty;
        var tokens = line.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4)
        {
            reason = $"expected >=4 tokens (timestamp channel id dlc), got {tokens.Length}";
            return false;
        }

        if (!double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ts))
        {
            reason = $"invalid timestamp '{tokens[0]}'";
            return false;
        }

        var idToken = tokens[2];
        if (idToken.EndsWith("x", StringComparison.OrdinalIgnoreCase))
        {
            idToken = idToken.Substring(0, idToken.Length - 1);
        }
        if (!uint.TryParse(idToken, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
        {
            reason = $"invalid CAN id '{tokens[2]}'";
            return false;
        }

        int dataStartIndex = 4;
        byte dlc;
        FrameFlags flags = FrameFlags.None;
        int scan = 3;
        while (scan < tokens.Length &&
               (tokens[scan].Equals("rx", StringComparison.OrdinalIgnoreCase) ||
                tokens[scan].Equals("tx", StringComparison.OrdinalIgnoreCase)))
        {
            scan++;
        }
        if (scan + 1 < tokens.Length &&
            (tokens[scan].Equals("d", StringComparison.OrdinalIgnoreCase) ||
             tokens[scan].Equals("l", StringComparison.OrdinalIgnoreCase)))
        {
            if (!byte.TryParse(tokens[scan + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out dlc))
            {
                reason = $"invalid DLC after Vector 'd/l' marker '{tokens[scan + 1]}'";
                return false;
            }
            if (tokens[scan].Equals("l", StringComparison.OrdinalIgnoreCase))
            {
                flags |= FrameFlags.Fd;
            }
            dataStartIndex = scan + 2;
        }
        else if (!byte.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out dlc))
        {
            reason = $"invalid DLC '{tokens[3]}'";
            return false;
        }

        var data = new List<byte>(capacity: Math.Max((int)dlc, 8));
        for (int i = dataStartIndex; i < tokens.Length; i++)
        {
            var t = tokens[i];
            switch (t.ToLowerInvariant())
            {
                case "fd":
                    flags |= FrameFlags.Fd; continue;
                case "brs":
                    flags |= FrameFlags.BitRateSwitch; continue;
                case "esi":
                    flags |= FrameFlags.ErrorStateIndicator; continue;
                case "error":
                    flags |= FrameFlags.ErrFrame; continue;
                case "rx": continue;
                case "tx": continue;
                default:
                    if (t.Contains('=') ||
                        t.Equals("Length", StringComparison.OrdinalIgnoreCase) ||
                        t.Equals("BitCount", StringComparison.OrdinalIgnoreCase) ||
                        t.Equals("ID", StringComparison.OrdinalIgnoreCase))
                    {
                        goto EndDataBytes;
                    }
                    break;
            }
            if (t.Length == 0)
            {
                reason = "empty data token";
                return false;
            }
            if (t.Length == 1)
            {
                if (!byte.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    reason = $"invalid single-hex byte '{t}'";
                    return false;
                }
                data.Add(b);
            }
            else if (t.Length % 2 != 0)
            {
                reason = $"odd-length hex token '{t}' (length {t.Length})";
                return false;
            }
            else
            {
                for (int j = 0; j < t.Length; j += 2)
                {
                    if (!byte.TryParse(t.AsSpan(j, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    {
                        reason = $"invalid hex pair in '{t}' at offset {j}";
                        return false;
                    }
                    data.Add(b);
                }
            }
        }
        EndDataBytes:;

        if (data.Count != dlc)
        {
            reason = $"byte count {data.Count} != declared DLC {dlc}";
            return false;
        }

        frame = new ReplayFrame(ts, id, dlc, data.ToArray(), flags);
        return true;
    }

    /// <summary>
    /// 解析 ASC `date Wed Jul 1 08:32:01.000 2026` header。Vector 24h 或 12h 格式都支持。
    /// </summary>
    public static DateTime? TryParseDateHeader(string line)
    {
        var parts = line.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6) return null;
        var ddmm24h = $"{parts[1]} {parts[2]} {parts[3]} {parts[4]} {parts[^1]}";
        if (DateTime.TryParseExact(ddmm24h, "ddd MMM d HH:mm:ss.fff yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt24))
            return dt24;
        if (parts.Length >= 7)
        {
            var ddmm12h = $"{parts[1]} {parts[2]} {parts[3]} {parts[4]} {parts[5]} {parts[^1]}";
            if (DateTime.TryParseExact(ddmm12h, "ddd MMM d hh:mm:ss.fff tt yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt12))
                return dt12;
        }
        return null;
    }

    /// <summary>
    /// 检测当前行是否为 Vector CANoe 的 section 分隔符 (Begin/End TriggerBlock 等 6 种)。
    /// </summary>
    public static bool LineIsSectionDelimiter(string line)
    {
        return line.StartsWith("begin triggerblock", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("end triggerblock", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("begin measurementblock", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("end measurementblock", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("start of measurement", StringComparison.OrdinalIgnoreCase);
    }
}
