using System.IO;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.Uds;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Uds;

/// <summary>
/// <see cref="UdsTransportSettingsStore"/> 持久化契约。
/// <para>
/// 与 <c>LayoutStateStore</c> 的「异常只记日志」不同：诊断参数保存失败**必须**
/// 让调用方知道。用户以为改好了、实际没落盘，下次启动还是旧地址——这种「谎报
/// 成功」比直接报错更难查。
/// </para>
/// </summary>
public sealed class UdsTransportSettingsStoreTests : IDisposable
{
    private static readonly uint[] ConcurrentRequestIds = { 0x714u, 0x715u };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"uds-store-{Guid.NewGuid():N}");

    private string Path0 => Path.Combine(_dir, "uds-transport.json");

    private UdsTransportSettingsStore NewStore(string? path = null)
        => new(NullLogger<UdsTransportSettingsStore>.Instance, path ?? Path0);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_Returns_Defaults_When_File_Missing()
    {
        var store = NewStore();

        store.Load();

        store.Current.Should().Be(UdsTransportSettings.Default);
        store.Current.RequestId.Should().Be(0x7E0);
        store.Current.ResponseId.Should().Be(0x7E8);
        store.Current.IsExtendedFrame.Should().BeFalse();
    }

    [Fact]
    public void Save_Then_Load_RoundTrips_All_Fields()
    {
        var settings = new UdsTransportSettings(
            RequestId: 0x714,
            ResponseId: 0x760,
            FunctionalId: 0x7DF,
            IsExtendedFrame: true,
            P2Ms: 120,
            P2StarMs: 8000,
            NBsMs: 1500,
            NCrMs: 2000);

        NewStore().Save(settings);
        var reloaded = NewStore();
        reloaded.Load();

        reloaded.Current.Should().Be(settings);
    }

    [Fact]
    public void Load_Falls_Back_To_Defaults_On_Corrupt_Json_And_Preserves_The_Bad_File()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path0, "{ this is not json");

        var store = NewStore();
        store.Load();

        store.Current.Should().Be(UdsTransportSettings.Default);
        File.Exists(Path0 + ".bad").Should().BeTrue(
            "损坏文件要留档便于诊断，不能直接覆盖掉");
    }

    [Fact]
    public void Save_Returns_False_When_The_Path_Is_Unwritable()
    {
        // 用一个存在的「目录」当文件路径 → 写入必失败。
        Directory.CreateDirectory(Path0);
        var store = NewStore();

        var ok = store.Save(UdsTransportSettings.Default);

        ok.Should().BeFalse("保存失败必须如实返回，不能谎报成功");
    }

    [Fact]
    public void Save_Leaves_No_Temp_File_Behind_On_Success()
    {
        var store = NewStore();
        store.Save(UdsTransportSettings.Default);

        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Save_Updates_Current_So_Callers_See_The_New_Value_Immediately()
    {
        var store = NewStore();
        var settings = UdsTransportSettings.Default with { RequestId = 0x714 };

        store.Save(settings);

        store.Current.RequestId.Should().Be(0x714);
    }

    [Fact]
    public void Save_Does_Not_Update_Current_When_The_Write_Fails()
    {
        Directory.CreateDirectory(Path0);
        var store = NewStore();
        var settings = UdsTransportSettings.Default with { RequestId = 0x714 };

        var ok = store.Save(settings);

        ok.Should().BeFalse();
        store.Current.RequestId.Should().Be(0x7E0,
            "落盘失败时内存态也不能改——否则界面显示新值、重启后又变回去");
    }

    [Fact]
    public void Save_Writes_A_Versioned_Envelope()
    {
        var store = NewStore();
        store.Save(UdsTransportSettings.Default with { RequestId = 0x714 });

        using var doc = JsonDocument.Parse(File.ReadAllText(Path0));
        doc.RootElement.TryGetProperty("version", out var version).Should().BeTrue();
        version.GetString().Should().StartWith("uds-transport/");
    }

    [Fact]
    public void Load_Tolerates_A_Missing_Version_Field()
    {
        // 手写配置或早期格式没有 version —— 不该因此整份丢弃。
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path0, """
            { "settings": { "requestId": 3056, "responseId": 1888 } }
            """);

        var store = NewStore();
        store.Load();

        store.Current.RequestId.Should().Be(3056, "0xBF0");
        store.Current.ResponseId.Should().Be(1888, "0x760");
    }

    [Fact]
    public void Save_Uses_A_Unique_Temp_Name_So_Two_Instances_Do_Not_Clobber()
    {
        // 两个 Store 指向同一路径并发保存：唯一的 tmp 名保证互相不覆盖。
        // 关键断言是「两次保存都成功」——共用一个 tmp 名时至少有一次会失败
        // （File.Move 的源被对方改掉），只查最终文件内容区分不出这一点。
        var a = NewStore();
        var b = NewStore();
        var results = new bool[2];

        Parallel.Invoke(
            () => results[0] = a.Save(UdsTransportSettings.Default with { RequestId = 0x714 }),
            () => results[1] = b.Save(UdsTransportSettings.Default with { RequestId = 0x715 }));

        results.Should().AllSatisfy(ok => ok.Should().BeTrue(
            "唯一 tmp 名保证并发保存互不干扰"));
        File.Exists(Path0).Should().BeTrue();
        var reloaded = NewStore();
        reloaded.Load();
        reloaded.Current.RequestId.Should().BeOneOf(ConcurrentRequestIds,
            "并发保存的结果应是某一次的完整值，不能是两者拼接的残片");
    }

    // ---- 语义校验 ----

    [Fact]
    public void Load_Rejects_Zero_ResponseId_From_A_Hand_Edited_File()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path0, """{ "settings": { "requestId": 3056, "responseId": 0 } }""");

        var store = NewStore();
        store.Load();

        store.Current.ResponseId.Should().Be(0x7E8,
            "responseId=0 会把每个响应都过滤掉——症状是「改完配置就彻底失联」");
    }

    [Fact]
    public void Load_Rejects_Identical_Request_And_Response_Id()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path0, """{ "settings": { "requestId": 1808, "responseId": 1808 } }""");

        var store = NewStore();
        store.Load();

        store.Current.RequestId.Should().NotBe(store.Current.ResponseId,
            "收发同一 ID 会让自己的请求被当成响应");
    }

    [Fact]
    public void Load_Rejects_An_Unknown_Major_Schema_Version()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path0, """
            { "version": "uds-transport/v2", "settings": { "requestId": 3056, "responseId": 1888 } }
            """);

        var store = NewStore();
        store.Load();

        store.Current.RequestId.Should().Be(0x7E0,
            "不认识的主版本不能硬读——字段语义可能已经变了");
    }

    [Fact]
    public void Load_Accepts_A_Minor_Schema_Version()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path0, """
            { "version": "uds-transport/v1.3", "settings": { "requestId": 3056, "responseId": 1888 } }
            """);

        var store = NewStore();
        store.Load();

        store.Current.RequestId.Should().Be(3056, "子版本升级不改字段语义，应当接受");
    }
}
