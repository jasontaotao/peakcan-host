using FluentAssertions;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.App.Services.Xcp;
using PeakCan.Host.Core.Xcp.Record;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Xcp;

/// <summary>
/// S4-T6 脚本触发源测试（spec D6 触发源 v0.2）：脚本出站口 xcp-trigger: 前缀行 → 触发引擎；
/// 非匹配行不触发；前缀大小写不敏感。
/// </summary>
public sealed class XcpScriptTriggerSourceTests
{
    private static XcpTriggerRecordEngine Engine(string dir) =>
        new(new XcpTriggerRecordOptions
        {
            Directory = dir,
            Channels = [new MdfChannelSpec("EngineSpeed", "rpm")],
        });

    private static string NewTempDir() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s6trig_{Guid.NewGuid():N}");

    [Fact]
    public async Task Matching_output_line_triggers_engine()
    {
        var dir = NewTempDir();
        var hub = new ScriptOutputHub();
        using var source = new XcpScriptTriggerSource(hub, Engine(dir));

        hub.EmitOutput(ScriptOutputLine.Info("xcp-trigger: overrun detected"));
        await WaitUntilAsync(() => source.EmittedCount == 1);

        source.EmittedCount.Should().Be(1);
        source.LastError.Should().BeNull();

        var engine = engineRef(source);
        engine.IsCapturing.Should().BeTrue();
        await engine.CloseCaptureAsync();
    }

    [Fact]
    public void Non_matching_output_line_is_ignored()
    {
        var hub = new ScriptOutputHub();
        using var source = new XcpScriptTriggerSource(hub, Engine(NewTempDir()));

        hub.EmitOutput(ScriptOutputLine.Info("hello world"));
        hub.EmitOutput(ScriptOutputLine.Info("trigger without prefix"));

        source.EmittedCount.Should().Be(0);
    }

    [Fact]
    public async Task Prefix_match_is_case_insensitive()
    {
        var hub = new ScriptOutputHub();
        using var source = new XcpScriptTriggerSource(hub, Engine(NewTempDir()));

        hub.EmitOutput(ScriptOutputLine.Info("XCP-TRIGGER: case"));
        await WaitUntilAsync(() => source.EmittedCount == 1);

        await engineRef(source).CloseCaptureAsync();
    }

    private static XcpTriggerRecordEngine engineRef(XcpScriptTriggerSource source)
    {
        var field = typeof(XcpScriptTriggerSource).GetField("_engine",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (XcpTriggerRecordEngine)(field?.GetValue(source)
            ?? throw new System.InvalidOperationException("engine field missing"));
    }

    private static async System.Threading.Tasks.Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await System.Threading.Tasks.Task.Delay(20);
        condition().Should().BeTrue("2 s 内条件未成立");
    }
}
