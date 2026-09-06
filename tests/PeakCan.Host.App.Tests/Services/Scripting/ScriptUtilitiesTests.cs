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

    // P1-2（2026-09-06，Lazy<T> 清零）：生产路径 = ScriptUtilities → ScriptOutputHub →
    // ScriptEngine（ctor 订阅转发）→ OutputReceived。锁定该链路行为。
    [Fact]
    public void ScriptEngine_AsSink_RoutesOutput_ThroughEngine()
    {
        var hub = new ScriptOutputHub();
        var engine = new ScriptEngine(
            Substitute.For<ILogger<ScriptEngine>>(), null, null, null,
            ScriptEngineOptions.Default, hub);
        ScriptOutputLine? got = null;
        engine.OutputReceived += l => got = l;

        var utils = new ScriptUtilities(_logger, hub);

        utils.Log("hi");

        got.Should().NotBeNull();
        got!.Message.Should().Be("hi");
    }
}
