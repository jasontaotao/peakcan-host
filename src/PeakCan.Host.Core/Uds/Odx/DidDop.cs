using System.Globalization;
using System.Xml.Linq;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// Extracts a <see cref="DidDefinition"/> from an ODX
/// <c>DOP-BASE</c> XML element. The expected element shape is:
/// <code>
/// <DOP-BASE ID="DOP.0xF190" SHORT-NAME="VIN_DOP">
///   <DIAG-CODED-TYPE BASE-DATA-TYPE="A_ASCIISTRING" xsi:type="MIN-MAX-LENGTH-TYPE">
///     <MAX-LENGTH>17</MAX-LENGTH>
///   </DIAG-CODED-TYPE>
/// </DOP-BASE>
/// </code>
/// The 2-byte DID id is parsed from the <c>ID</c> attribute (e.g.,
/// <c>"DOP.0xF190"</c> → <c>0xF190</c>).
/// </summary>
/// <remarks>
/// v3.49.0 MINOR: T1.1 — 在解析 DID id+name 的基础上，进一步解析
/// <c>DIAG-CODED-TYPE</c> 的 <c>BASE-DATA-TYPE</c>（权威属性名，
/// 真实 OEM .odx-d 均使用此名；历史 <c>BASE-TYPE=</c> 作为兼容回退）
/// 与 <c>BIT-LENGTH</c>/<c>MIN-LENGTH</c>/<c>MAX-LENGTH</c>，产出
/// 单个 <see cref="DidField"/> 填充 <see cref="DidDefinition.Fields"/>。
/// 缺 DIAG-CODED-TYPE 时 <c>Fields</c> 仍为空（DID 已知但无类型元数据），
/// 不影响与既有行为的兼容。
/// </remarks>
public static class DidDop
{
    /// <summary>XML Schema Instance 命名空间，用于读取 <c>xsi:type</c>。</summary>
    private static readonly XNamespace XsiNs = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>Try to map a DOP-BASE element to a DidDefinition.</summary>
    /// <param name="dop">DOP-BASE XML element.</param>
    /// <param name="warning">Output warning (or null on success).</param>
    /// <returns>DidDefinition or null if input was structurally invalid.</returns>
    public static DidDefinition? TryMap(XElement dop, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(dop);
        var idAttr = (string?)dop.Attribute("ID");
        if (idAttr is null || !TryParseDidFromId(idAttr, out var did))
        {
            warning = $"DOP-BASE missing or invalid ID attribute: '{idAttr}'.";
            return null;
        }
        var shortName = (string?)dop.Attribute("SHORT-NAME") ?? string.Empty;

        // T1.1: parse DIAG-CODED-TYPE into a DidField (may be absent).
        var field = TryParseField(dop, shortName);

        // DidDefinition is readonly record struct (Id, Name, Description,
        // LengthBytes, Writable). DOP-BASE exposes id + name; length/writable
        // default to safe-conservative values. LengthBytes now derived from
        // the parsed field's bit-length when available (else 0).
        var lengthBytes = field is { } f ? (f.BitLength + 7) / 8 : 0;
        var result = new DidDefinition(
            Id: did,
            Name: shortName,
            Description: shortName,
            LengthBytes: lengthBytes,
            Writable: false);

        if (field is { } parsedField)
            result = result with { Fields = new[] { parsedField } };

        warning = null;
        return result;
    }

    /// <summary>
    /// Parse a <c>DIAG-CODED-TYPE</c> child of a DOP-BASE into a
    /// <see cref="DidField"/>. Returns null when the DOP has no
    /// DIAG-CODED-TYPE (DID known but no type metadata — defaults to
    /// empty Fields, no warning).
    /// T1.2: 同时解析 COMPU-METHOD 与 UNIT（含 UNIT-REF 跨文档引用解析）。
    /// </summary>
    private static DidField? TryParseField(XElement dop, string shortName)
    {
        var ns = dop.Name.Namespace;
        var dct = dop.Element(ns + "DIAG-CODED-TYPE");
        if (dct is null) return null;

        var baseType = ParseBaseType(dct);
        var bitLength = ResolveBitLength(dct, ns);
        var compu = CompuMethodParser.TryParseCompu(dop, ns);

        // UNIT-REF 指向同文档的 <UNIT>；从 dop.Document 自建一次性索引。
        var unitById = IndexUnitsInDocument(dop.Document, ns);
        var unit = CompuMethodParser.TryParseUnit(dop, ns, unitById);

        return new DidField(
            Name: shortName,
            BitLength: bitLength,
            ByteOffset: 0,
            BaseType: baseType,
            Compu: compu,
            Unit: unit);
    }

    /// <summary>
    /// 文档级 <c><UNIT></c> 索引（ID → 元素），供 UNIT-REF 解析。
    /// 文档不存在时返回 null（TryParseUnit 会安全回退）。
    /// </summary>
    private static IReadOnlyDictionary<string, XElement>? IndexUnitsInDocument(
        XDocument? doc, XNamespace ns)
    {
        if (doc is null) return null;
        var dict = new Dictionary<string, XElement>();
        foreach (var u in doc.Descendants(ns + "UNIT"))
        {
            var id = (string?)u.Attribute("ID");
            if (id is not null) dict[id] = u;
        }
        return dict.Count > 0 ? dict : null;
    }

    /// <summary>
    /// Resolve the FIELD bit length. ODX has three relevant
    /// DIAG-CODED-TYPE shapes (per <c>xsi:type</c>):
    ///   - STANDARD-LENGTH-TYPE: <c><BIT-LENGTH></c>
    ///   - MIN-MAX-LENGTH-TYPE : <c><MAX-LENGTH></c> (byte count; ×8)
    ///   - (CODED-CONST / others): 无位长信息 → 0
    /// 字符串类型 (A_ASCIISTRING / A_UNICODE2STRING) 多以 MIN-MAX-LENGTH-TYPE
    /// 承载，MAX-LENGTH 即字节长度上限。
    /// </summary>
    private static int ResolveBitLength(XElement dct, XNamespace ns)
    {
        var bitEl = dct.Element(ns + "BIT-LENGTH");
        if (bitEl is not null &&
            int.TryParse(bitEl.Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var bits) && bits > 0)
            return bits;

        var maxEl = dct.Element(ns + "MAX-LENGTH");
        if (maxEl is not null &&
            int.TryParse(maxEl.Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var maxBytes) && maxBytes > 0)
            return maxBytes * 8;

        return 0;
    }

    /// <summary>
    /// Map ODX <c>BASE-DATA-TYPE</c> 字符串到 <see cref="DidBaseType"/>。
    /// 优先读 <c>BASE-DATA-TYPE</c>（真实 OEM 属性名），回退兼容老式
    /// <c>BASE-TYPE</c>（仅历史夹具使用）。未知值 → <see cref="DidBaseType.Unknown"/>。
    /// </summary>
    private static DidBaseType ParseBaseType(XElement dct)
    {
        var raw = (string?)dct.Attribute("BASE-DATA-TYPE")
                  ?? (string?)dct.Attribute("BASE-TYPE");
        return raw switch
        {
            "A_UINT32"          => DidBaseType.UInt32,
            "A_INT32"           => DidBaseType.Int32,
            "A_FLOAT64"         => DidBaseType.Float64,
            "A_ASCIISTRING"     => DidBaseType.AsciiString,
            "A_UNICODE2STRING"  => DidBaseType.Unicode2String,
            "A_BYTEFIELD"       => DidBaseType.ByteField,
            _                   => DidBaseType.Unknown,
        };
    }

    /// <summary>
    /// Parse a 2-byte DID from a DOP id like "DOP.0xF190" or
    /// "DOP.61840" (decimal fallback).
    /// </summary>
    private static bool TryParseDidFromId(string idAttr, out ushort did)
    {
        did = 0;
        var idx = idAttr.LastIndexOf('.');
        if (idx < 0 || idx >= idAttr.Length - 1) return false;
        var hexPart = idAttr[(idx + 1)..];
        if (hexPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hexPart = hexPart[2..];
        return ushort.TryParse(hexPart, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out did);
    }
}
