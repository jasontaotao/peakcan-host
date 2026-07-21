namespace PeakCan.Host.Core.Uds.Odx;

// ODX 2.x DATA-OBJECT-PROBLEM 表达一个 DID 字段的类型与物理值映射。
// 本组类型承载 DID 数据类型识别的三个层次（参见 ASAM ISO 22901）：
//   1) DidBaseType — DIAG-CODED-TYPE 的 BASE-DATA-TYPE 基础编码类型
//   2) CompuMethod — COMPU-METHOD 物理/标量换算（IDENTICAL / LINEAR / TEXTTABLE）
//   3) Unit        — 物理单位
// 既有 DidDefinition 只保留标量 LengthBytes，无法表达复合 DID 的多字段、
// 也无法让 UI 解码原始字节为有意义的物理值；本组类型补齐该缺口。

/// <summary>
/// ODX <c>BASE-DATA-TYPE</c> 的编码类型枚举。取值覆盖真实 OEM
/// <c>.odx-d</c> 文件（Vector CANdelaStudio）实际使用的属性值，
/// 例如 <c>A_UINT32</c>（最常见）、<c>A_ASCIISTRING</c>、
/// <c>A_UNICODE2STRING</c>、<c>A_BYTEFIELD</c>、<c>A_FLOAT64</c>、
/// <c>A_INT32</c>。未知值降级为 <see cref="Unknown"/>，不抛异常
/// （ODX 解析管线保持"非致命偏差产出 warning"契约）。
/// </summary>
public enum DidBaseType
{
    /// <summary>无符号整数（ODX <c>A_UINT32</c>）。</summary>
    UInt32,
    /// <summary>有符号整数（ODX <c>A_INT32</c>）。</summary>
    Int32,
    /// <summary>双精度浮点（ODX <c>A_FLOAT64</c>）。</summary>
    Float64,
    /// <summary>ASCII 字节字符串（ODX <c>A_ASCIISTRING</c>）。</summary>
    AsciiString,
    /// <summary>UTF-16BE 字符串（ODX <c>A_UNICODE2STRING</c>）。</summary>
    Unicode2String,
    /// <summary>原始字节域（ODX <c>A_BYTEFIELD</c>），按 hex 透传展示。</summary>
    ByteField,
    /// <summary>无法识别的 BASE-DATA-TYPE；解码器原样 hex 输出。</summary>
    Unknown,
}

/// <summary>
/// COMPU-METHOD 的换算类别，取值对应真实 <c>.odx-d</c>
/// 中 <c><CATEGORY></c> 元素实际枚举：
/// <c>IDENTICAL</c> / <c>LINEAR</c> / <c>TEXTTABLE</c>。
/// （<c>COMPUCODE</c> / <c>TAB-INTP</c> 等高级类别暂不纳入本期。）
/// </summary>
public enum CompuCategory
{
    /// <summary>物理值与原始值相同（无换算）。</summary>
    Identical,
    /// <summary>线性换算：physical = A * raw + B。</summary>
    Linear,
    /// <summary>枚举文本表：raw → 文本标签查找。</summary>
    Texttable,
}

/// <summary>
/// ODX <c>COMPU-METHOD</c>：原始编码值到物理值的映射。
/// 线性换算为 <c>physical = A * raw + B</c>（ODX
/// <c>COMPU-INTERNAL-TO-PHYS/COMPU-RATIONAL-COEFFS</c>）；
/// 文本表为 <c>raw → label</c> 字典（ODX <c>COMPU-SCALE/VT</c>）。
/// </summary>
public sealed record CompuMethod(
    CompuCategory Category,
    double LinearA,
    double LinearB,
    IReadOnlyDictionary<long, string> TextTable)
{
    /// <summary>无换算（IDENTICAL）的便捷工厂。</summary>
    public static CompuMethod Identical { get; } = new(
        CompuCategory.Identical, 1.0, 0.0,
        new Dictionary<long, string>());

    /// <summary>
    /// 线性换算工厂：physical = A * raw + B。
    /// </summary>
    public static CompuMethod LinearOf(double a, double b) => new(
        CompuCategory.Linear, a, b,
        new Dictionary<long, string>());

    /// <summary>
    /// 文本表工厂。
    /// </summary>
    public static CompuMethod TexttableOf(IReadOnlyDictionary<long, string> table) => new(
        CompuCategory.Texttable, 1.0, 0.0, table);
}

/// <summary>ODX 物理单位（<c>UNIT</c> 元素）。
/// 命名为 <see cref="DidUnit"/> 以避开既有 <c>PeakCan.Host.Core.Unit</c>
/// 类型，避免跨命名空间歧义。</summary>
public sealed record DidUnit(
    string ShortName,
    string DisplayName)
{
    /// <summary>空单位（无单位信息时的占位）。</summary>
    public static DidUnit Empty { get; } = new(string.Empty, string.Empty);

    /// <summary>是否为空单位（用于 UI 决定是否展示单位列）。</summary>
    public bool IsEmpty => string.IsNullOrEmpty(ShortName);
}

/// <summary>
/// 单个 DID 字段的类型描述。复合 DID 可含多个 <see cref="DidField"/>；
/// 标量 DID 通常只含一个。该结构由 ODX <c>DATA-OBJECT-PROP</c> /
/// <c>DOP-BASE</c> 解析产出，供 <c>DidValueDecoder</c> 解码原始字节。
/// </summary>
/// <param name="Name">字段名（DOP 的 SHORT-NAME）。</param>
/// <param name="BitLength">位长度（ODX <c>BIT-LENGTH</c>）。</param>
/// <param name="ByteOffset">在 DID payload 内的字节偏移
/// （来自 ODX <c>BYTE-POSITION</c>，或缺则按字段顺序累计）。</param>
/// <param name="BaseType">基础编码类型。</param>
/// <param name="Compu">物理换算；无则 <c>null</c>（编码值原样展示）。</param>
/// <param name="Unit">物理单位；无则 <c>null</c>。</param>
public sealed record DidField(
    string Name,
    int BitLength,
    int ByteOffset,
    DidBaseType BaseType,
    CompuMethod? Compu,
    DidUnit? Unit);
