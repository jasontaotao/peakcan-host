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

    // 过渡期 back-compat ctor：AppHostBuilder 目前以
    // new ScriptUtilities(logger, engine) 构造（DI 改 Lazy 前），走
    // ScriptEngine → ScriptEngineSink 适配器 → OutputReceived 的实时路径。
    // 该路径是生产实际使用路径，锁定其行为，防适配器回归。
    [Fact]
    public void LegacyEngineCtor_RoutesOutput_ThroughEngine()
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
