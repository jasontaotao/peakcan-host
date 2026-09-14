using System.Text.Json;
using PeakCan.HIL.Core.HIL.Security;
using PeakCan.HIL.Core.HIL.Serialization;

namespace PeakCan.Host.Infrastructure.Channel.SecOc;

/// <summary>
/// 从 suite JSON 提取 <c>security</c> 块（spec §8 Phase 4）。
/// <para>
/// headless 通道组装（<c>HeadlessHostBuilder.Build</c>）发生在 suite 完整反序列化之前，
/// 故这里单独探取 security 子对象（只映射 <see cref="SecOcBlock"/>，不做 TestSuite 全模型反序列化）。
/// </para>
/// </summary>
public static class SecOcBlockReader
{
    /// <returns>suite 无 <c>security</c> 字段 / 字段为 null / 路径缺失时为 null（单通道无保护，向后兼容）。</returns>
    public static SecOcBlock? TryRead(string? suitePath)
    {
        if (string.IsNullOrWhiteSpace(suitePath) || !File.Exists(suitePath))
            return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(suitePath));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Suite '{suitePath}' is not valid JSON (while reading the security block): {ex.Message}", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetSecurityProperty(doc.RootElement, out var security) ||
                security.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            // 块存在但畸形时抛出（不静默降级为"无保护"——安全配置错误必须可见）。
            try
            {
                return security.Deserialize<SecOcBlock>(HILJsonOptions.Default);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Suite '{suitePath}' security block is malformed: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// 顶层 <c>security</c> 大小写不敏感查找。第三方手写 <c>"Security"</c> 不得被静默忽略
    /// （否则会以"无保护"运行，spec Rev9）。JSON 序列化本身是 camelCase，正常路径全小写。
    /// </summary>
    private static bool TryGetSecurityProperty(JsonElement root, out JsonElement security)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, "security", StringComparison.OrdinalIgnoreCase))
            {
                security = prop.Value;
                return true;
            }
        }
        security = default;
        return false;
    }
}
