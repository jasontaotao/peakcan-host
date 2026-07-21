using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.Core.Uds.Database;

/// <summary>
/// Definition of a single UDS Data Identifier (DID). Populated from
/// built-in defaults and/or a user JSON file at
/// <c>%APPDATA%\PeakCan.Host\uds-dids.json</c>.
/// </summary>
/// <param name="Id">2-byte DID (e.g. 0xF190 for VIN).</param>
/// <param name="Name">Short human-readable name.</param>
/// <param name="Description">Longer description for UI tooltip / details panel.</param>
/// <param name="LengthBytes">Expected byte length of the DID payload.</param>
/// <param name="Writable">Whether <c>WriteDataByIdentifier (0x2E)</c> is supported.</param>
/// <remarks>
/// v3.49.0 MINOR: <see cref="Fields"/> 承载 ODX 解析得到的字段类型表
/// （<see cref="DidField"/>），用于在 UDS View 解码 DID 原始字节为
/// 有意义的物理值/枚举文本。缺省为空数组，保持与既有调用的 5 参构造
/// 完全兼容；仅 ODX 导入线会填充该属性。System.Text.Json 反序列化
/// 缺省即空数组，旧 JSON 文件无需变更。
/// </remarks>
public sealed record DidDefinition(
    ushort Id,
    string Name,
    string Description,
    int LengthBytes,
    bool Writable)
{
    /// <summary>
    /// ODX 解析得到的字段类型表。复合 DID 含多个字段；
    /// 标量 DID 通常含 0 或 1 项。缺省空（与既有 5 参构造兼容）。
    /// 仅 ODX 导入线填充。
    /// </summary>
    public IReadOnlyList<DidField> Fields { get; init; } = Array.Empty<DidField>();

    /// <summary>
    /// Human-readable form with the DID rendered as hex (e.g. "DidDefinition
    /// 0xF190 (VIN, 17 bytes)"). The default record ToString renders
    /// <see cref="Id"/> as decimal, which is misleading for 16-bit UDS DIDs.
    /// </summary>
    public override string ToString() =>
        $"DidDefinition 0x{Id:X4} ({Name}, {LengthBytes} bytes)";
}
