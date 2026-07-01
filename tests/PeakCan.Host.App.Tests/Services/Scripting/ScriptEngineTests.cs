using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Scripting;

/// <summary>
/// Unit tests for <see cref="ScriptEngine"/>.
/// </summary>
public sealed class ScriptEngineTests : IDisposable
{
    private readonly ILogger<ScriptEngine> _logger = Substitute.For<ILogger<ScriptEngine>>();
    private readonly ScriptEngine _engine;

    public ScriptEngineTests()
    {
        // Create a ScriptEngine with null dependencies for testing.
        // The engine will only use basic JS execution, not the can/dbc APIs.
        _engine = new ScriptEngine(_logger, null, null, null);
    }

    [Fact]
    public async Task RunAsync_SimpleScript_ReturnsSuccess()
    {
        // Arrange
        var script = "var x = 1 + 2;";

        // Act
        var result = await _engine.RunAsync(script);

        // Assert
        Assert.True(result.Success);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task RunAsync_ScriptWithSyntaxError_ReturnsFailure()
    {
        // Arrange
        var script = "function {";

        // Act
        var result = await _engine.RunAsync(script);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(ScriptErrorType.Runtime, result.ErrorType);
    }

    [Fact]
    public async Task RunAsync_ScriptWithRuntimeError_ReturnsFailure()
    {
        // Arrange
        var script = "throw new Error('Test error');";

        // Act
        var result = await _engine.RunAsync(script);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Test error", result.Error);
    }

    [Fact]
    public async Task RunAsync_ScriptWithTimeout_ReturnsTimeoutError()
    {
        // Arrange
        var script = "while(true) {}";
        var timeout = TimeSpan.FromMilliseconds(100);

        // Act
        var result = await _engine.RunAsync(script, timeout: timeout);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(ScriptErrorType.Timeout, result.ErrorType);
    }

    [Fact]
    public async Task RunAsync_ScriptWithCancellation_ReturnsTimeoutError()
    {
        // Arrange
        var script = "while(true) {}";
        var cts = new CancellationTokenSource();
        cts.CancelAfter(100);

        // Act
        var result = await _engine.RunAsync(script, ct: cts.Token);

        // Assert
        Assert.False(result.Success);
        // Note: Cancellation triggers timeout because the engine interrupts
        // the script when the CTS fires, which looks like a timeout to V8.
        Assert.Equal(ScriptErrorType.Timeout, result.ErrorType);
    }

    [Fact]
    public void IsRunning_ReturnsFalseWhenNoScriptRunning()
    {
        // Assert
        Assert.False(_engine.IsRunning);
    }

    [Fact]
    public void Stop_DoesNotThrowWhenNoScriptRunning()
    {
        // Act & Assert
        _engine.Stop();
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        // Act & Assert
        _engine.Dispose();
    }

    /// <summary>
    /// v1.7.0 MINOR Item 2: declarative verification that IScriptCanApi
    /// does not expose <c>Dispose</c> to scripts.
    /// </summary>
    [Fact]
    public void IScriptCanApi_Omits_Dispose_Method()
    {
        // Arrange / Act
        var disposeMethod = typeof(IScriptCanApi).GetMethod(
            "Dispose",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        // Assert
        Assert.Null(
            disposeMethod);
    }

    /// <summary>
    /// v1.7.0 MINOR Item 2: declarative verification that IScriptDbcApi
    /// does not expose <c>Dispose</c> to scripts.
    /// </summary>
    [Fact]
    public void IScriptDbcApi_Omits_Dispose_Method()
    {
        // Arrange / Act
        var disposeMethod = typeof(IScriptDbcApi).GetMethod(
            "Dispose",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        // Assert
        Assert.Null(
            disposeMethod);
    }

    /// <summary>
    /// v1.7.3 PATCH Item 1: verify that exceeding the soft heap monitor
    /// cap surfaces as <see cref="ScriptErrorType.ResourceLimit"/> rather
    /// than the generic <see cref="ScriptErrorType.Runtime"/>. Closes
    /// v1.7.1 PATCH review MEDIUM #1 (heap-cap discrimination).
    /// <para>
    /// Test design: tiny <c>ScriptEngineOptions.MaxHeapSizeMB</c>
    /// (1 MB) vs large generation caps (64 MB each). The soft heap
    /// monitor fires before the hard generation cap, surfacing as a
    /// catchable <c>ScriptEngineException</c> with a message containing
    /// "heap" / "allocation" / "limit" / "memory". The <c>when</c>
    /// filter discriminates these into ResourceLimit.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_When_HeapCap_Exceeded_Returns_ResourceLimit()
    {
        // Arrange — heap monitor cap (1 MB) << generation caps (64 MB)
        // so the soft monitor fires before the hard generation cap.
        var options = new ScriptEngineOptions(
            MaxHeapSizeMB: 1,
            MaxNewSpaceSizeMB: 64,
            MaxOldSpaceSizeMB: 64);
        var engine = new ScriptEngine(
            _logger, null, null, null, options);

        // Act — single 2 MB JS allocation (well over the 1 MB heap
        // monitor cap). HeapSizeViolationPolicy.Interrupt (default)
        // interrupts script execution and throws a managed exception
        // caught by the IsResourceLimit when filter.
        var result = await engine.RunAsync(
            "var a = 'x'.repeat(2 * 1024 * 1024);",
            TimeSpan.FromSeconds(10));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorType.Should().Be(ScriptErrorType.ResourceLimit);
    }

    /// <summary>
    /// v1.7.1 PATCH Item 1: declarative verification that IScriptCanApi
    /// exposes ergonomic <c>IsConnected</c> property and
    /// <c>Send(CanFrame)</c> overload (additive on top of the
    /// method-based surface shipped in v1.7.0).
    /// </summary>
    [Fact]
    public void IScriptCanApi_Exposes_IsConnected_Property_And_Send_Overload()
    {
        // Arrange / Act
        var prop = typeof(IScriptCanApi).GetProperty("IsConnected");
        var sendOverload = typeof(IScriptCanApi).GetMethod(
            "Send",
            new[] { typeof(CanFrame) });

        // Assert
        Assert.NotNull(
            prop);
        Assert.Equal(typeof(bool), prop!.PropertyType);
        Assert.NotNull(
            sendOverload);
    }

    /// <summary>
    /// v1.7.1 PATCH Item 2: onInit() throwing flips ScriptResult.Success
    /// to false (was previously logged but ignored — script appeared
    /// successful even when onInit had thrown).
    /// </summary>
    [Fact]
    public async Task RunAsync_OnInit_Throws_Sets_Success_False()
    {
        // Arrange
        var engine = new ScriptEngine(
            Substitute.For<ILogger<ScriptEngine>>(), null, null, null);
        var script = "function onInit() { throw new Error('init-fail'); }";

        // Act
        var result = await engine.RunAsync(script);

        // Assert
        Assert.False(
            result.Success);
        Assert.Equal(ScriptErrorType.Runtime, result.ErrorType);
        Assert.NotNull(result.Error);
    }

    public void Dispose()
    {
        _engine.Dispose();
    }
}
