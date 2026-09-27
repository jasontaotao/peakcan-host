using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeakCan.Host.Core.Xcp.Bench;

/// <summary>批次运行模式：readonly = 只读场景（缺写旗标）；full = 含写回场景（已确认 ECU 安全态）。</summary>
public enum BenchRunMode
{
    ReadOnly,
    Full,
}

/// <summary>单项结论状态（spec §3 判据 2：实测/接受/拒绝/另立项/未采 五态）。</summary>
public enum BenchItemStatus
{
    NotCollected,
    Measured,
    Accepted,
    Rejected,
    DeferredToProject,
}

/// <summary>验证矩阵单项：13 项挂账逐项一行（spec §1 全集）。</summary>
/// <param name="Facts">实测事实键值（机读；如 resourceBitmap=0x33、uploadMs=42）。</param>
/// <param name="HumanVerdict">人工判定栏（批次命令只产事实与建议，判定由人回填——spec §4）。</param>
public sealed record BenchItem(
    string ItemId,
    BenchItemStatus Status,
    string Summary,
    IReadOnlyDictionary<string, string>? Facts = null,
    string? HumanVerdict = null);

/// <summary>写回场景还原校验记录（spec §3 判据 3：零遗留——报告含每场景还原证据）。</summary>
public sealed record BenchRestoreRecord(
    string Scenario,
    bool Restored,
    string Detail);

/// <summary>台架批次报告（D4：机读 JSON 进仓 + 人工判定栏）。</summary>
public sealed record BenchReport(
    string GeneratedAtUtc,
    string A2lName,
    BenchRunMode Mode,
    IReadOnlyList<BenchItem> Items,
    IReadOnlyList<BenchRestoreRecord> Restores);

/// <summary>报告 JSON 序列化（camelCase + 缩进 + 字符串枚举，探针同口径）。</summary>
public static class BenchReportJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(BenchReport report) =>
        JsonSerializer.Serialize(report, Options);

    public static BenchReport Deserialize(string json) =>
        JsonSerializer.Deserialize<BenchReport>(json, Options)
        ?? throw new JsonException("BenchReport decoded to null.");
}
