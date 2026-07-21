namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// 单个 DID 字段的解码结果。承载原始编码值与物理值
/// （经 <see cref="CompuMethod"/> 物理换算/枚举文本翻译后），
/// 用于 UDS View 渲染。
/// </summary>
/// <param name="Name">字段名（来自 <see cref="DidField.Name"/>）。</param>
/// <param name="RawValue">原始字节/数值的字符串展示
/// （hex 串或整数，调试与诊断用途）。</param>
/// <param name="PhysicalValue">解码后的物理值字符串
/// （物理数值 + 单位，或枚举文本，或字符串）。</param>
/// <param name="Unit">物理单位（<see cref="DidUnit.DisplayName"/>，
/// 无单元为 <c>string.Empty</c>）。</param>
public sealed record DecodedField(
    string Name,
    string RawValue,
    string PhysicalValue,
    string Unit);
