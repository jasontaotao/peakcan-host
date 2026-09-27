using System.Text.Json;
using PeakCan.Host.Core.Xcp.Bench;
using Xunit;

namespace PeakCan.Host.Core.Tests.Xcp.Bench;

/// <summary>S7-T1：台架批次报告模型 + JSON 序列化（spec D4：机读 JSON 进仓，camelCase 探针同口径）。</summary>
public class BenchReportTests
{
    private static BenchReport SampleReport() => new(
        GeneratedAtUtc: "2026-09-27T08:00:00Z",
        A2lName: "App_merge_INCA.a2l",
        Mode: BenchRunMode.ReadOnly,
        Items:
        [
            new BenchItem("A-1", BenchItemStatus.Measured, "能力位图 0x33",
                Facts: new Dictionary<string, string> { ["resourceBitmap"] = "0x33" }),
            new BenchItem("C-1", BenchItemStatus.NotCollected, "广播写语义：本次未采（缺写旗标）"),
        ],
        Restores:
        [
            new BenchRestoreRecord("B2_mta_fragmentation", Restored: true, "原值 0x1A 已还原，回读一致"),
        ]);

    [Fact]
    public void Serialize_uses_camelCase_and_string_enums()
    {
        var json = BenchReportJson.Serialize(SampleReport());

        Assert.Contains("\"itemId\"", json);
        Assert.Contains("\"generatedAtUtc\"", json);
        // 枚举以字符串落盘（探针 JsonStringEnumConverter 默认 PascalCase 同口径），不是数字。
        Assert.Contains("\"NotCollected\"", json);
        Assert.Contains("\"ReadOnly\"", json);
    }

    [Fact]
    public void RoundTrip_preserves_items_and_restores()
    {
        var original = SampleReport();
        var json = BenchReportJson.Serialize(original);
        var decoded = BenchReportJson.Deserialize(json);

        Assert.Equal(original.GeneratedAtUtc, decoded.GeneratedAtUtc);
        Assert.Equal(original.Mode, decoded.Mode);
        Assert.Equal(2, decoded.Items.Count);
        Assert.Equal("A-1", decoded.Items[0].ItemId);
        Assert.Equal("0x33", decoded.Items[0].Facts!["resourceBitmap"]);
        Assert.Single(decoded.Restores);
        Assert.True(decoded.Restores[0].Restored);
    }

    [Fact]
    public void Deserialize_invalidJson_throws()
    {
        Assert.ThrowsAny<JsonException>(() => BenchReportJson.Deserialize("{ not json"));
    }
}
