using System.Globalization;
using System.Xml.Linq;

namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// 解析 ODX <c>COMPU-METHOD</c> 与 <c>UNIT</c>/<c>UNIT-REF</c>，产出
/// <see cref="CompuMethod"/> 与 <see cref="DidUnit"/>，供
/// <see cref="DidDop"/> / <see cref="RequestBasedMappers"/> 共用。
/// </summary>
/// <remarks>
/// v3.49.0 MINOR: T1.2。覆盖真实 OEM .odx-d 文件出现的三个 CATEGORY：
///   - IDENTICAL  : 物理=原始（无换算）
///   - LINEAR     : 一阶有理多项式 physical = (V0 + V1*raw) / D0
///                  COMPU-RATIONAL-COEFFS 下 COMPU-NUMERATOR 含若干 <V>
///                  系数（按 raw 升幂），COMPU-DENOMINATOR 缺省=1。
///   - TEXTTABLE  : 每条 COMPU-SCALE 的 LOWER/UPPER-LIMIT + COMPU-CONST/VT
///                  文本标签；取 LOWER-LIMIT 作 raw 键（区间端点相等的
///                  典型场景；对区间则取下界作为代表）。
/// 非致命偏差（未知 CATEGORY、缺系数）返回 null 而非抛异常，保持 ODX
/// 解析管线 "非致命偏差产出 warning" 契约。
/// </remarks>
public static class CompuMethodParser
{
    /// <summary>
    /// 在 DOP-BASE / DATA-OBJECT-PROP 元素下解析 <c>COMPU-METHOD</c>。
    /// 返回 null 表示无 COMPU-METHOD 子元素（raw 值原样展示）。
    /// </summary>
    public static CompuMethod? TryParseCompu(XElement host, XNamespace ns)
    {
        var cm = host.Element(ns + "COMPU-METHOD");
        if (cm is null) return null;

        var cat = (string?)cm.Element(ns + "CATEGORY");
        if (string.IsNullOrEmpty(cat)) return null;

        return cat switch
        {
            "IDENTICAL"  => CompuMethod.Identical,
            "LINEAR"     => ParseLinear(cm, ns),
            "TEXTTABLE"  => ParseTexttable(cm, ns),
            _            => null, // 未知类别（COMPUCODE/TAB-INTP 等）—— 本期不支持
        };
    }

    /// <summary>
    /// 在 DOP-BASE / DATA-OBJECT-PROP 元素下解析 UNIT：
    /// 优先读内嵌 <c><UNIT></c>，否则读 <c><UNIT-REF ID-REF></c>
    /// 并在 <paramref name="unitById"/> 索引中解析。两者皆无返回 null。
    /// </summary>
    public static DidUnit? TryParseUnit(
        XElement host, XNamespace ns,
        IReadOnlyDictionary<string, XElement>? unitById = null)
    {
        var inline = host.Element(ns + "UNIT");
        if (inline is not null)
            return BuildUnit(inline, ns);

        var refEl = host.Element(ns + "UNIT-REF");
        var refId = (string?)refEl?.Attribute("ID-REF");
        if (refId is not null && unitById is not null &&
            unitById.TryGetValue(refId, out var unitEl))
            return BuildUnit(unitEl, ns);

        return null;
    }

    private static CompuMethod? ParseLinear(XElement cm, XNamespace ns)
    {
        // COMPU-INTERNAL-TO-PHYS > COMPU-SCALES > COMPU-SCALE >
        // COMPU-RATIONAL-COEFFS > {COMPU-NUMERATOR, COMPU-DENOMINATOR}
        var coeffs = cm.Descendants(ns + "COMPU-RATIONAL-COEFFS").FirstOrDefault();
        if (coeffs is null) return null;

        var num = ReadCoeffs(coeffs.Element(ns + "COMPU-NUMERATOR"), ns);
        var den = ReadCoeffs(coeffs.Element(ns + "COMPU-DENOMINATOR"), ns);

        // 一阶有理多项式：physical = (V0 + V1*raw) / D0
        // numerator 至少 2 项才能定义线性；缺则视作 IDENTICAL 安全回退。
        if (num is not { Count: >= 2 })
            return CompuMethod.Identical;

        double d0 = (den is { Count: >= 1 } && den[0] != 0) ? den[0] : 1.0;
        double a = num[1] / d0;
        double b = num[0] / d0;
        return CompuMethod.LinearOf(a, b);
    }

    private static CompuMethod? ParseTexttable(XElement cm, XNamespace ns)
    {
        var table = new Dictionary<long, string>();
        foreach (var scale in cm.Descendants(ns + "COMPU-SCALE"))
        {
            var lowerEl = scale.Element(ns + "LOWER-LIMIT");
            var vtEl = scale.Descendants(ns + "VT").FirstOrDefault();
            if (lowerEl is null || vtEl is null) continue;
            if (!long.TryParse(lowerEl.Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var key)) continue;
            var label = vtEl.Value;
            if (!string.IsNullOrEmpty(label))
                table[key] = label;
        }
        if (table.Count == 0) return null;
        return CompuMethod.TexttableOf(table);
    }

    private static List<double>? ReadCoeffs(XElement? container, XNamespace ns)
    {
        if (container is null) return null;
        var list = new List<double>();
        foreach (var v in container.Elements(ns + "V"))
        {
            if (double.TryParse(v.Value, NumberStyles.Float | NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var n))
                list.Add(n);
        }
        return list;
    }

    private static DidUnit BuildUnit(XElement unitEl, XNamespace ns)
    {
        var shortName = (string?)unitEl.Element(ns + "SHORT-NAME") ?? string.Empty;
        // DISPLAY-NAME 可能是属性（真实 _379: <DISPLAY-NAME>ms</DISPLAY-NAME> 是元素）
        // 或元素；优先元素，回退属性，最后回退 SHORT-NAME。
        var display = (string?)unitEl.Element(ns + "DISPLAY-NAME")
                      ?? (string?)unitEl.Attribute("DISPLAY-NAME")
                      ?? shortName;
        return new DidUnit(shortName, display);
    }
}
