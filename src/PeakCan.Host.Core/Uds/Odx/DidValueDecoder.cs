using System.Globalization;
using System.Text;

namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// v3.49.0 MINOR: T3.1 — 把 UDS ReadDataByIdentifier 原始字节按
/// <see cref="DidField"/> 字段表解码为 <see cref="DecodedField"/>，
/// 供 UDS View 显示物理值/枚举文本/字符串。
///
/// 三个层次依次落地：
///   1) BaseType 决定切片与编码解析 (ASCII/UNICODE2 字符串, UInt32/Int32
///      整数, Float64, ByteField/Unknown hex 透传)
///   2) CompuMethod 决定物理换算 (IDENTICAL 原样, LINEAR A*raw+B,
///      TEXTTABLE 查表)
///   3) Unit 决定物理单位后缀 (<see cref="DidUnit.DisplayName"/> 非空时拼接)
///
/// 设计原则: 非致命优先 —— payload 短于字段、未知类型、表无命中均不抛异常,
/// 回退到 raw hex / 整数展示, 保持 UDS View 在瑕疵数据下也稳定可用。
/// </summary>
public static class DidValueDecoder
{
    /// <summary>
    /// 解码 <paramref name="payload"/> 为字段表对应的结果列表。
    /// 无字段表时整体 hex 一行展示。
    /// </summary>
    public static IReadOnlyList<DecodedField> Decode(
        byte[] payload, IReadOnlyList<DidField> fields)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (fields is null || fields.Count == 0)
        {
            return new[] { new DecodedField(
                Name: "(raw)",
                RawValue: BytesToHex(payload),
                PhysicalValue: BytesToHex(payload),
                Unit: string.Empty) };
        }

        var result = new List<DecodedField>(fields.Count);
        foreach (var f in fields)
            result.Add(DecodeOne(payload, f));
        return result;
    }

    private static DecodedField DecodeOne(byte[] payload, DidField f)
    {
        var unitDisplay = f.Unit?.DisplayName ?? string.Empty;
        var byteLen = f.BitLength > 0 ? f.BitLength / 8 : 0;
        var slice = SliceOrEmpty(payload, f.ByteOffset, byteLen);

        switch (f.BaseType)
        {
            case DidBaseType.AsciiString:
                return new DecodedField(f.Name, BytesToHex(slice),
                    DecodeAscii(slice), unitDisplay);

            case DidBaseType.Unicode2String:
                return new DecodedField(f.Name, BytesToHex(slice),
                    DecodeUnicode2(slice), unitDisplay);

            case DidBaseType.Float64:
                return new DecodedField(f.Name, BytesToHex(slice),
                    DecodeFloat64(slice, f.Compu, unitDisplay), unitDisplay);

            case DidBaseType.UInt32:
            case DidBaseType.Int32:
                return DecodeInt(payload, f, slice, unitDisplay, signed: f.BaseType == DidBaseType.Int32);

            case DidBaseType.ByteField:
            case DidBaseType.Unknown:
            default:
                // 无 CompuMethod 概念，纯 hex 透传；若有 Unit 仍附上。
                var hex = BytesToHex(slice);
                return new DecodedField(f.Name, hex,
                    unitDisplay.Length > 0 ? $"{hex} {unitDisplay}" : hex, unitDisplay);
        }
    }

    /// <summary>整数字段解码：切片读 BE raw → CompuMethod 换算 → 单位附缀。</summary>
    private static DecodedField DecodeInt(byte[] payload, DidField f,
        byte[] slice, string unitDisplay, bool signed)
    {
        long raw = ReadBeInt(slice, f.BitLength, signed);
        var rawHex = slice.Length > 0
            ? "0x" + Convert.ToHexString(slice).ToUpperInvariant()
            : raw.ToString(CultureInfo.InvariantCulture);

        var physical = ApplyCompu(f, raw, unitDisplay);
        return new DecodedField(f.Name, rawHex, physical, unitDisplay);
    }

    /// <summary>
    /// 把 <see cref="CompuMethod"/> 应用到 raw 值。IDENTICAL/LINEAR 计算
    /// 数值并附单位；TEXTTABLE 查表，未命中回退到 raw 数值。
    /// </summary>
    private static string ApplyCompu(DidField f, long raw, string unitDisplay)
    {
        var compu = f.Compu;
        if (compu is null)
        {
            // 无换算：数值 + 单位
            return FormatPhysical((double)raw, unitDisplay, integer: true);
        }

        return compu.Category switch
        {
            CompuCategory.Identical => FormatPhysical((double)raw, unitDisplay, integer: true),
            CompuCategory.Linear    => FormatPhysical(compu.LinearA * raw + compu.LinearB, unitDisplay, integer: false),
            CompuCategory.Texttable => compu.TextTable.TryGetValue(raw, out var label)
                ? label
                : $"{raw} (label not in table)",
            _ => FormatPhysical((double)raw, unitDisplay, integer: true),
        };
    }

    private static string DecodeFloat64(byte[] slice, CompuMethod? compu, string unitDisplay)
    {
        if (slice.Length < 8) return BytesToHex(slice);
        // ODX A_FLOAT64 = IEEE754 BE。BitConverter 是 LE，故反序字节再转。
        var leBytes = new byte[]
        {
            slice[7], slice[6], slice[5], slice[4],
            slice[3], slice[2], slice[1], slice[0],
        };
        var value = BitConverter.ToDouble(leBytes, 0);
        var physical = compu?.Category == CompuCategory.Linear
            ? compu.LinearA * value + compu.LinearB
            : value;
        return FormatPhysical(physical, unitDisplay, integer: false);
    }

    private static string DecodeAscii(byte[] slice)
    {
        // 去 trailing 0 (END-OF-PDU) 后 ASCII 解码。
        var end = slice.Length;
        while (end > 0 && slice[end - 1] == 0) end--;
        return Encoding.ASCII.GetString(slice, 0, end);
    }

    private static string DecodeUnicode2(byte[] slice)
    {
        // UTF-16BE；偶字节对，去 trailing 0x0000。
        var end = slice.Length;
        while (end >= 2 && slice[end - 1] == 0 && slice[end - 2] == 0) end -= 2;
        if (end <= 0) return string.Empty;
        // Encoding.BigEndianUnicode 异步安全; 不足偶数长度则截到偶。
        if (end % 2 != 0) end--;
        return Encoding.BigEndianUnicode.GetString(slice, 0, end);
    }

    /// <summary>
    /// 按 offset / length 安全切片；payload 不足则返回能取到的子集 (不抛异常)。
    /// </summary>
    private static byte[] SliceOrEmpty(byte[] payload, int offset, int length)
    {
        if (length <= 0 && offset >= payload.Length) return Array.Empty<byte>();
        var start = Math.Max(0, offset);
        if (start >= payload.Length) return Array.Empty<byte>();
        var avail = payload.Length - start;
        var take = length > 0 ? Math.Min(length, avail) : avail;
        var dst = new byte[take];
        Array.Copy(payload, start, dst, 0, take);
        return dst;
    }

    /// <summary>读取 BE 整数 (≤64 bit)。signed=true 时按补码解释。</summary>
    private static long ReadBeInt(byte[] slice, int bitLength, bool signed)
    {
        var n = slice.Length;
        if (n == 0) return 0;
        ulong v = 0;
        for (int i = 0; i < n; i++)
            v = (v << 8) | slice[i];

        // 位长可能小于字节长 ×8（位字段）；按位长掩码。
        if (bitLength > 0 && bitLength < n * 8)
        {
            ulong mask = (1UL << bitLength) - 1;
            v &= mask;
        }

        if (signed && bitLength > 0)
        {
            // 符号位在 bitLength-1；若置位则补码扩展为负。
            long sv = (long)v;
            if (((sv >> (bitLength - 1)) & 1) == 1)
                sv -= (1L << bitLength);
            return sv;
        }
        return (long)v;
    }

    /// <summary>
    /// 物理值格式化：整数场景去掉小数 (e.g. "10"); 浮点场景去掉尾随 0
    /// (e.g. "10" 而非 "10.0000"); 单位非空时直接拼接 (无空格)。
    /// </summary>
    private static string FormatPhysical(double value, string unitDisplay, bool integer)
    {
        string s = integer
            ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.######", CultureInfo.InvariantCulture);
        return unitDisplay.Length > 0 ? $"{s}{unitDisplay}" : s;
    }

    private static string BytesToHex(byte[] b)
    {
        if (b is null || b.Length == 0) return string.Empty;
        var sb = new StringBuilder(b.Length * 3);
        for (int i = 0; i < b.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(b[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
