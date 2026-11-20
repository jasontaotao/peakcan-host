using System.Text.Json.Nodes;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// 三个 NM 工具共用的输出根：统一公共字段与 <c>truncated</c> 数组形状
/// （spec §8.1 / §5.3）。工具只在返回值上追加各自字段，禁止各自另起根对象。
/// </summary>
internal static class NmToolOutput
{
    /// <summary>
    /// 已填好公共字段的根对象：<c>source_id</c>、<c>channel</c>、<c>dbc_path</c>、
    /// <c>dbc_hash</c>、<c>semantics_available=false</c>、<c>unknowns</c>、
    /// <c>assumptions</c>，以及空数组 <c>truncated</c>。
    /// </summary>
    internal static JsonObject Root(NmToolSnapshot snapshot) => new()
    {
        ["source_id"] = snapshot.SourceId,
        ["channel"] = snapshot.Channel,
        ["dbc_path"] = snapshot.DbcPath,
        ["dbc_hash"] = snapshot.DbcHash,
        ["semantics_available"] = false,
        ["unknowns"] = new JsonArray(
            snapshot.Report.Unknowns.Select(u => (JsonNode)u).ToArray()),
        ["assumptions"] = new JsonArray(
            snapshot.Report.Assumptions.Select(a => (JsonNode)a).ToArray()),
        ["truncated"] = new JsonArray(),
    };

    /// <summary>记录某个 section 被截断：<c>{section, returned, total}</c>。</summary>
    internal static void AddTruncation(
        JsonObject root, string section, int returned, int total)
        => ((JsonArray)root["truncated"]!).Add(new JsonObject
        {
            ["section"] = section,
            ["returned"] = returned,
            ["total"] = total,
        });
}
