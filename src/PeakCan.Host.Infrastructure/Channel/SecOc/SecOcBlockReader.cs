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

    // 缺口 1b（2026-09-17）：per-channel security 块。

    /// <summary>
    /// 读取 suite <c>channels[]</c> 每项的 <c>security</c> 子块，按 channel <c>name</c>
    /// 键控返回（channel 级块优先于顶层块）。channels 数组缺失 / 无 security 项 →
    /// 空字典（该 run 回落顶层块 / --secoc-config）。channel 级块畸形时 fail-loud
    /// （安全配置错误必须可见，禁止静默降级为无保护）。
    /// </summary>
    public static IReadOnlyDictionary<string, SecOcBlock> TryReadPerChannel(string? suitePath)
    {
        var result = new Dictionary<string, SecOcBlock>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(suitePath) || !File.Exists(suitePath))
            return result;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(suitePath));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Suite '{suitePath}' is not valid JSON (while reading per-channel security blocks): {ex.Message}", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(doc.RootElement, "channels", out var channels) ||
                channels.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var ch in channels.EnumerateArray())
            {
                if (ch.ValueKind != JsonValueKind.Object ||
                    !TryGetProperty(ch, "name", out var nameEl) ||
                    nameEl.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(nameEl.GetString()))
                {
                    continue;
                }
                var name = nameEl.GetString()!;
                if (!TryGetSecurityProperty(ch, out var sec) || sec.ValueKind == JsonValueKind.Null)
                    continue;

                SecOcBlock block;
                try
                {
                    block = sec.Deserialize<SecOcBlock>(HILJsonOptions.Default)
                        ?? throw new InvalidOperationException(
                            $"Suite '{suitePath}' channel '{name}' security block is null.");
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException(
                        $"Suite '{suitePath}' channel '{name}' security block is malformed: {ex.Message}", ex);
                }
                result[name] = block;
            }
        }
        return result;
    }

    /// <summary>大小写不敏感属性查找（参照 <see cref="TryGetSecurityProperty"/>）。</summary>
    private static bool TryGetProperty(JsonElement obj, string propertyName, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
