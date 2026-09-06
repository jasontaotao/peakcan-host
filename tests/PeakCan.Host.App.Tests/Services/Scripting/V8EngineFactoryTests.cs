using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.Infrastructure.Channel;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Scripting;

/// <summary>
/// P2-1 真拆类（2026-09-06）：<see cref="V8EngineFactory"/> 从
/// ScriptEngine/CreateEngineFlow partial 升级为独立类后的直接单测。
/// 沙箱构造（安全关键）首次获得不经 RunAsync 的独立验证面：
/// console 对象注入、can/dbc 受限面、utilities 注入、资源上限换算。
/// </summary>
public class V8EngineFactoryTests
{
    private static V8EngineFactory MakeFactory(
        CanApi? canApi = null,
        DbcApi? dbcApi = null,
        ScriptUtilities? utilities = null,
        ScriptEngineOptions? options = null)
        => new(options ?? ScriptEngineOptions.Default, canApi, dbcApi, utilities);

    [Fact]
    public void Ctor_Null_Options_Throws()
    {
        var act = () => new V8EngineFactory(null!, null, null, null);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Create_Injects_Console_Object()
    {
        using var engine = MakeFactory().Create(default);

        engine.Evaluate("typeof console").Should().Be("object");
        engine.Evaluate("typeof console.log").Should().Be("function");
        engine.Evaluate("typeof console.warn").Should().Be("function");
        engine.Evaluate("typeof console.error").Should().Be("function");
    }

    [Fact]
    public void Create_WithoutApis_Global_Undefined()
    {
        using var engine = MakeFactory().Create(default);

        engine.Evaluate("typeof can").Should().Be("undefined");
        engine.Evaluate("typeof dbc").Should().Be("undefined");
        engine.Evaluate("typeof log").Should().Be("undefined");
        engine.Evaluate("typeof delay").Should().Be("undefined");
    }

    [Fact]
    public void Create_WithApis_Injects_Restricted_Surface()
    {
        var canApi = new CanApi(
            NullLogger<CanApi>.Instance,
            new SendService(NullLogger<SendService>.Instance),
            new ChannelRouter());
        var dbcApi = new DbcApi(NullLogger<DbcApi>.Instance, new FakeDbcService());
        using var engine = MakeFactory(canApi: canApi, dbcApi: dbcApi).Create(default);

        engine.Evaluate("typeof can").Should().Be("object");
        engine.Evaluate("typeof dbc").Should().Be("object");
    }

    [Fact]
    public void Create_WithUtilities_Injects_Helper_Functions()
    {
        var utilities = new ScriptUtilities(
            NullLogger<ScriptUtilities>.Instance, new ScriptOutputHub());
        using var engine = MakeFactory(utilities: utilities).Create(default);

        engine.Evaluate("typeof log").Should().Be("function");
        engine.Evaluate("typeof warn").Should().Be("function");
        engine.Evaluate("typeof error").Should().Be("function");
        engine.Evaluate("typeof delay").Should().Be("function");
        engine.Evaluate("typeof hex").Should().Be("function");
        engine.Evaluate("typeof toHex").Should().Be("function");
    }

    [Fact]
    public void Create_Applies_Resource_Caps_From_Options()
    {
        // MaxRuntimeHeapSize 单位是字节（MB → ×1024×1024）。
        var options = new ScriptEngineOptions { MaxHeapSizeMB = 32, MaxNewSpaceSizeMB = 8, MaxOldSpaceSizeMB = 24 };
        using var engine = MakeFactory(options: options).Create(default);

        ((long)engine.MaxRuntimeHeapSize).Should().Be(32L * 1024 * 1024);
    }

    private sealed class FakeDbcService : DbcService
    {
        public FakeDbcService() : base(NullLogger<DbcService>.Instance) { }
        public override Task LoadAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }
}
