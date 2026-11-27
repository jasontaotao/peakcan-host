using PeakCan.HIL.Core;
// src/PeakCan.HIL.Core/Replay/AscFormat.cs — v3.49.0 MINOR (T1 of 3)
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
        => writer.WriteLine(FormatDataLine(frame, elapsed.TotalSeconds, frame.Channel.Handle));

    /// <summary>
    /// Format one ASC data line as a string — the single source of the data-line
    /// token grammar, shared by the StreamWriter sink here and the
    /// StringBuilder sinks in Infrastructure (HIL AscFrameSink /
    /// CLI FrameCaptureExporter), so every producer emits exactly what
    /// <see cref="TryParseDataLine"/> accepts. <paramref name="channelNumber"/>
    /// is the ASC channel number (producers may map a native handle to it).
    /// </summary>
    public static string FormatDataLine(CanFrame frame, double elapsedSeconds, int channelNumber)
    {
        var dataHex = Convert.ToHexString(frame.Data.Span);

        var fdFlag = frame.IsFd ? $"  {FlagFd}" : "";
        var brsFlag = (frame.Flags & FrameFlags.BitRateSwitch) != 0 ? $" {FlagBrs}" : "";
        var esiFlag = (frame.Flags & FrameFlags.ErrorStateIndicator) != 0 ? $" {FlagEsi}" : "";
        var errFlag = frame.IsError ? $" {FlagError}" : "";

        return $"{elapsedSeconds:F6} {channelNumber:X2}  {frame.Id.Raw:X}  {frame.Dlc}  {dataHex}{fdFlag}{brsFlag}{esiFlag}{errFlag}";
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
        => TryParseDataLine(line.AsSpan(), out frame, out reason);

    /// <summary>
    /// Span 重载——解析热路径用它：不再为每行 Split 出字符串数组，
    /// 63 万行的日志能省下数百万次短生命期分配。语义与字符串重载完全一致。
    /// </summary>
    public static bool TryParseDataLine(ReadOnlySpan<char> line, out ReplayFrame frame, out string reason)
    {
        frame = default!;
        reason = string.Empty;

        var tokens = new AscLineTokens(line);

        if (!tokens.Next(out var tsToken) || !tokens.Next(out var channelToken)
            || !tokens.Next(out var idToken))
        {
            reason = "expected >=4 tokens (timestamp channel id dlc)";
            return false;
        }

        if (!TryParseAscTimestamp(tsToken, out var ts))
        {
            reason = $"invalid timestamp '{tsToken.ToString()}'";
            return false;
        }

        if (!ushort.TryParse(channelToken, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var channel))
        {
            reason = "invalid channel '" + channelToken.ToString() + "'";
            return false;
        }

        var idRaw = idToken;
        // Vector ASC spec: an `x` suffix marks the ID as 29-bit extended.
        var isExtended = idRaw.Length > 0 && (idRaw[^1] == 'x' || idRaw[^1] == 'X');
        if (isExtended)
            idRaw = idRaw[..^1];
        if (!TryParseHexUInt32(idRaw, out var id))
        {
            reason = $"invalid CAN id '{idToken.ToString()}'";
            return false;
        }
        // 兜底：无 x 后缀但数值 > 0x7FF 也判扩展（兼容无 x 标记的不规范 ASC 文件）。
        if (!isExtended && id > 0x7FFu) isExtended = true;

        var flags = FrameFlags.None;
        bool? isTransmit = null;
        var hasToken = tokens.Next(out var token);

        // 方向/帧型标记预扫描（原实现在这里把 "fd" 也一并吃掉，位置语义交给数据段）。
        while (hasToken)
        {
            if (IsMarkerToken(token, "rx")) isTransmit = false;
            else if (IsMarkerToken(token, "tx")) isTransmit = true;
            else if (IsMarkerToken(token, "fd")) { }
            else if (IsMarkerToken(token, "rtr")) flags |= FrameFlags.Rtr;
            else break;
            hasToken = tokens.Next(out token);
        }

        byte dlc;
        if (hasToken && IsDlcMarkerToken(token))
        {
            if (!tokens.Next(out var dlcToken)
                || !byte.TryParse(dlcToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out dlc))
            {
                reason = "invalid DLC after Vector 'd/l/fd' marker";
                return false;
            }
            // 'l' 和 'fd' 标记都表示 CAN FD 帧
            if (IsMarkerToken(token, "l") || IsMarkerToken(token, "fd"))
                flags |= FrameFlags.Fd;
            hasToken = tokens.Next(out token);
        }
        else if (hasToken)
        {
            if (!byte.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out dlc))
            {
                reason = $"invalid DLC '{token.ToString()}'";
                return false;
            }
            hasToken = tokens.Next(out token);
        }
        else
        {
            // 无方向/帧型标记且 token 已耗尽 = 无数据帧（如 RTR）。
            dlc = 0;
        }

        // 数据字节放栈上，避免每行分配 List；合法 CAN FD 上限 64 字节。
        Span<byte> dataBuffer = stackalloc byte[64];
        var dataCount = 0;

        while (hasToken)
        {
            var t = token;

            if (IsMarkerToken(t, "fd"))
            {
                // 位置消歧：数据未凑满行内声明 DLC 时，"FD" 是数据字节 0xFD。
                if (dataCount >= dlc) { flags |= FrameFlags.Fd; hasToken = tokens.Next(out token); continue; }
            }
            else if (IsMarkerToken(t, "brs")) { flags |= FrameFlags.BitRateSwitch; hasToken = tokens.Next(out token); continue; }
            else if (IsMarkerToken(t, "esi")) { flags |= FrameFlags.ErrorStateIndicator; hasToken = tokens.Next(out token); continue; }
            else if (IsMarkerToken(t, "error")) { flags |= FrameFlags.ErrFrame; hasToken = tokens.Next(out token); continue; }
            else if (IsMarkerToken(t, "rx")) { isTransmit ??= false; hasToken = tokens.Next(out token); continue; }
            else if (IsMarkerToken(t, "tx")) { isTransmit ??= true; hasToken = tokens.Next(out token); continue; }
            else if (IsMarkerToken(t, "rtr")) { flags |= FrameFlags.Rtr; hasToken = tokens.Next(out token); continue; }

            if (t.IndexOf('=') >= 0
                || t.Equals("Length", StringComparison.OrdinalIgnoreCase)
                || t.Equals("BitCount", StringComparison.OrdinalIgnoreCase)
                || t.Equals("ID", StringComparison.OrdinalIgnoreCase))
            {
                break; // 元数据开始，后续不再是数据字节
            }

            if (t.Length == 0)
            {
                reason = "empty data token";
                return false;
            }
            if (t.Length == 1)
            {
                var nibble = HexNibble(t[0]);
                if (nibble < 0)
                {
                    reason = $"invalid single-hex byte '{t.ToString()}'";
                    return false;
                }
                var single = (byte)nibble;
                if (dataCount == dataBuffer.Length)
                {
                    reason = "data too long (max 64 for CAN FD)";
                    return false;
                }
                dataBuffer[dataCount++] = single;
            }
            else if (t.Length % 2 != 0)
            {
                reason = $"odd-length hex token '{t.ToString()}' (length {t.Length})";
                return false;
            }
            else
            {
                for (var j = 0; j < t.Length; j += 2)
                {
                    var hi = HexNibble(t[j]);
                    var lo = HexNibble(t[j + 1]);
                    if (hi < 0 || lo < 0)
                    {
                        reason = $"invalid hex pair in '{t.ToString()}' at offset {j}";
                        return false;
                    }
                    var b = (byte)((hi << 4) | lo);
                    if (dataCount == dataBuffer.Length)
                    {
                        reason = "data too long (max 64 for CAN FD)";
                        return false;
                    }
                    dataBuffer[dataCount++] = b;
                }
            }

            hasToken = tokens.Next(out token);
        }

        frame = new ReplayFrame(ts, id, (byte)dataCount, dataBuffer[..dataCount].ToArray(),
            flags, isExtended, channel, isTransmit);
        return true;
    }

    /// <summary>
    /// ASC 时间戳解析：形如 <c>123.456789</c>。手写逐字符解析比
    /// <see cref="double.TryParse(ReadOnlySpan{char}, NumberStyles, IFormatProvider, out double)"/>
    /// 快一个量级；遇到任何非预期字符就回退到框架解析，保证兼容性。
    /// </summary>
    private static bool TryParseAscTimestamp(ReadOnlySpan<char> t, out double value)
    {
        value = 0;
        if (t.IsEmpty) return false;

        long whole = 0;
        long frac = 0;
        var fracDigits = 0;
        var dot = false;
        var anyDigit = false;

        for (var i = 0; i < t.Length; i++)
        {
            var c = t[i];
            if (c == '.')
            {
                if (dot) return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                dot = true;
                continue;
            }
            if (c < '0' || c > '9')
                return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

            anyDigit = true;
            if (!dot)
            {
                if (whole > 1_000_000_000L)
                    return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                whole = whole * 10 + (c - '0');
            }
            else if (fracDigits < 9)
            {
                frac = frac * 10 + (c - '0');
                fracDigits++;
            }
        }

        if (!anyDigit) return false;
        value = fracDigits == 0 ? whole : whole + frac / Math.Pow(10, fracDigits);
        return true;
    }

    /// <summary>十六进制 nibble；非法返回 -1。热路径手写比 NumberStyles 解析快得多。</summary>
    private static int HexNibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private static bool TryParseHexUInt32(ReadOnlySpan<char> text, out uint value)
    {
        value = 0;
        if (text.Length == 0 || text.Length > 8) return false;
        foreach (var c in text)
        {
            var nibble = HexNibble(c);
            if (nibble < 0) { value = 0; return false; }
            value = (value << 4) | (uint)nibble;
        }
        return true;
    }

    /// <summary>顺序分词器：按空格/Tab 逐个切出 token，不分配、不预分配数组。</summary>
    private ref struct AscLineTokens
    {
        private ReadOnlySpan<char> _rest;

        public AscLineTokens(ReadOnlySpan<char> line) => _rest = line;

        public bool Next(out ReadOnlySpan<char> token)
        {
            var s = _rest;
            var i = 0;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            if (i >= s.Length)
            {
                token = default;
                _rest = default;
                return false;
            }
            var start = i;
            while (i < s.Length && s[i] != ' ' && s[i] != '\t') i++;
            token = s.Slice(start, i - start);
            _rest = s[i..];
            return true;
        }
    }

    private static bool IsMarkerToken(ReadOnlySpan<char> token, string marker)
        => token.Equals(marker, StringComparison.OrdinalIgnoreCase);

    private static bool IsDlcMarkerToken(ReadOnlySpan<char> token)
        => IsMarkerToken(token, "d") || IsMarkerToken(token, "l") || IsMarkerToken(token, "fd");

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
    public static bool LineIsSectionDelimiter(string line) => LineIsSectionDelimiter(line.AsSpan());

    /// <inheritdoc cref="LineIsSectionDelimiter(string)"/>
    public static bool LineIsSectionDelimiter(ReadOnlySpan<char> line)
    {
        return line.StartsWith("begin triggerblock", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("end triggerblock", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("begin measurementblock", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("end measurementblock", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("start of measurement", StringComparison.OrdinalIgnoreCase);
    }
}

