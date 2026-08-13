using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PeakCan.Host.App.Services.Scripting;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Scripting;

public sealed class ScriptUtilitiesTests
{
    private readonly ILogger<ScriptUtilities> _logger = Substitute.For<ILogger<ScriptUtilities>>();
    private readonly IScriptOutputSink _sink = Substitute.For<IScriptOutputSink>();

    [Fact]
    public void Log_EmitsInfoLine_ToSink()
    {
        var utils = new ScriptUtilities(_logger, _sink);

        utils.Log("hello");

        _sink.Received(1).EmitOutput(Arg.Is<ScriptOutputLine>(
            l => l.Level == ScriptOutputLevel.Info && l.Message == "hello"));
    }

    [Fact]
    public void Warn_EmitsWarningLine_ToSink()
    {
        var utils = new ScriptUtilities(_logger, _sink);

        utils.Warn("careful");

        _sink.Received(1).EmitOutput(Arg.Is<ScriptOutputLine>(
            l => l.Level == ScriptOutputLevel.Warning && l.Message == "careful"));
    }

    [Fact]
    public void Error_EmitsErrorLine_ToSink()
    {
        var utils = new ScriptUtilities(_logger, _sink);

        utils.Error("boom");

        _sink.Received(1).EmitOutput(Arg.Is<ScriptOutputLine>(
            l => l.Level == ScriptOutputLevel.Error && l.Message == "boom"));
    }

    // ScriptEngine 实现 IScriptOutputSink：ScriptUtilities 把输出交给
    // engine（作为 sink），engine 再把它们路由到 OutputReceived 事件。
    // 该路径是生产实际使用路径，锁定其行为。
    [Fact]
    public void ScriptEngine_AsSink_RoutesOutput_ThroughEngine()
    {
        var engine = new ScriptEngine(
            Substitute.For<ILogger<ScriptEngine>>(), null, null, null);
        ScriptOutputLine? got = null;
        engine.OutputReceived += l => got = l;

        var utils = new ScriptUtilities(_logger, engine);

        utils.Log("hi");

        got.Should().NotBeNull();
        got!.Message.Should().Be("hi");
    }
}
