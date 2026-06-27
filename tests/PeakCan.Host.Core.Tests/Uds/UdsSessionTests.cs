using FluentAssertions;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

/// <summary>
/// Tests for <see cref="UdsSession"/> S3 keepalive timer error handling
/// (v1.2.12 PATCH Item 9).
/// <para>
/// Bug: <c>UdsSession._s3Timer</c> callback used a bare <c>catch { }</c>,
/// silently swallowing every <c>TesterPresent</c> failure. Bus-off or
/// ECU-disconnect caused the diagnostic session to lapse with no log,
/// no counter, and no diagnostic signal.
/// </para>
/// <para>
/// Fix: typed catch (<c>OperationCanceledException</c> for shutdown,
/// general <c>Exception</c> for failures), <c>ILogger&lt;UdsSession&gt;</c>
/// Warning emission, <c>Interlocked</c>-backed failure counter exposed
/// as <see cref="UdsSession.S3FailureCount"/>.
/// </para>
/// <para>
/// <b>How failures are induced in tests:</b> a real <see cref="UdsClient"/>
/// is wired to a <see cref="IsoTpLayer"/> whose <c>sendFrame</c> callback
/// synchronously injects a UDS negative response
/// (<c>0x7F 0x3E 0x12</c> = subFunctionNotSupported) via
/// <see cref="IsoTpLayer.ProcessFrame"/>. The client's
/// <c>OnMessageReceived</c> converts this to
/// <see cref="UdsNegativeResponseException"/>, which surfaces from
/// <c>TesterPresentAsync</c> and is caught by the S3 timer callback.
/// </para>
/// </summary>
public sealed class UdsSessionTests
{
    private const uint ReqId = 0x7E0;
    private const uint RespId = 0x7E8;
    // UDS negative response: 0x7F (NRC SID) + 0x3E (TesterPresent) + 0x12 (subFunctionNotSupported)
    private static readonly byte[] NegativeResponse = { 0x7F, 0x3E, 0x12 };

    /// <summary>
    /// Build a UdsClient whose TesterPresent always fails with a UDS
    /// negative response (subFunctionNotSupported). The send callback
    /// synchronously injects the negative response back into the ISO-TP
    /// layer so the in-flight request completes with an exception.
    /// </summary>
    private static UdsClient NewFailingClient()
    {
        // Pre-encode the negative response as a single-frame ISO-TP CAN frame
        // (PCI byte 0x03 + 3 data bytes + 4 padding bytes = 8 bytes).
        var encodedNrc = new IsoTpFrame(IsoTpFrameType.Single, data: NegativeResponse).Encode();

        IsoTpLayer? isoRef = null;
        var iso = new IsoTpLayer(
            new CanIdConfig { RequestId = ReqId, ResponseId = RespId },
            sendFrame: (CanFrame _) =>
            {
                // Synchronously inject the negative response into the layer
                // so the in-flight request completes with an exception.
                if (isoRef is not null)
                {
                    var respFrame = new CanFrame(
                        new CanId(RespId, FrameFormat.Standard),
                        encodedNrc,
                        FrameFlags.None,
                        default,
                        default);
                    isoRef.ProcessFrame(respFrame);
                }
                return Task.CompletedTask;
            });
        isoRef = iso;
        return new UdsClient(iso);
    }

    /// <summary>
    /// Minimal hand-rolled logger spy — counts how many times each level
    /// was used. Same pattern as
    /// <c>IsoTpLayerTests.CountingLogger&lt;T&gt;</c>; avoids pulling
    /// NSubstitute into Core.Tests (which currently has no such reference).
    /// </summary>
    private sealed class CountingLogger<T> : ILogger<T>
    {
        public int ErrorCount { get; private set; }
        public int WarnCount { get; private set; }
        public List<(LogLevel Level, EventId EventId, Exception? Exception)> Records { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Records.Add((logLevel, eventId, exception));
            if (logLevel == LogLevel.Error) ErrorCount++;
            if (logLevel == LogLevel.Warning) WarnCount++;
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    [Fact]
    public async Task S3_KeepAlive_Logs_Failure_And_Increments_Count()
    {
        using var client = NewFailingClient();
        var logger = new CountingLogger<UdsSession>();
        using var session = new UdsSession(logger);

        session.StartS3KeepAlive(client, TimeSpan.FromMilliseconds(50));

        // Wait long enough for at least one tick (interval=50ms, first
        // tick fires at 50ms after start).
        await Task.Delay(500);

        session.S3FailureCount.Should().BeGreaterThan(0,
            "TesterPresent failures must increment the counter");
        logger.WarnCount.Should().BeGreaterThan(0,
            "TesterPresent failures must be logged at Warning level");
        logger.Records.Should().Contain(r =>
            r.Level == LogLevel.Warning && r.Exception != null,
            "the logger must receive a Warning record with the thrown exception");
    }

    [Fact]
    public async Task S3_KeepAlive_Does_Not_Swallow_Silently()
    {
        using var client = NewFailingClient();
        using var session = new UdsSession(new CountingLogger<UdsSession>());

        session.StartS3KeepAlive(client, TimeSpan.FromMilliseconds(50));

        await Task.Delay(500);

        session.S3FailureCount.Should().BeGreaterThan(0,
            "TesterPresent failures must surface via the counter, not be silently swallowed");
    }

    [Fact]
    public void StopS3KeepAlive_Resets_Failure_Count()
    {
        using var client = NewFailingClient();
        using var session = new UdsSession(new CountingLogger<UdsSession>());

        session.StartS3KeepAlive(client, TimeSpan.FromMilliseconds(50));
        SpinWait.SpinUntil(() => session.S3FailureCount > 0, TimeSpan.FromSeconds(3));
        session.StopS3KeepAlive();

        session.S3FailureCount.Should().Be(0,
            "StopS3KeepAlive must reset the failure counter so a fresh window starts on next StartS3KeepAlive");
    }
}
