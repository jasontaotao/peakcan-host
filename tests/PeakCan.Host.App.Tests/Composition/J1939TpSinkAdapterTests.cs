using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.App.Composition;
using Xunit;

namespace PeakCan.Host.App.Tests.Composition;

/// <summary>
/// <see cref="J1939TpSinkAdapter"/> bridges the Core-layer
/// <see cref="J1939TpLayer"/> (which knows nothing about
/// <see cref="PeakCan.Host.Infrastructure.Channel.IFrameSink"/>) onto the
/// Infrastructure-layer router fan-out, mirroring
/// <see cref="IsoTpSinkAdapter"/>.
/// <para>
/// Contract constraint from <c>IFrameSink</c>: <c>OnFrame</c> MUST NOT
/// throw — the router runs it on the SDK read thread; a throw is forwarded
/// to <c>OnError</c> and (after repeat) auto-detaches the sink.
/// <see cref="J1939TpLayer.ProcessFrame"/> throws
/// <see cref="ArgumentException"/> on malformed TP.CM/TP.DT data
/// (<c>TpCmMessage.Decode</c>/<c>TpDtMessage.Decode</c> contract), so the
/// adapter narrow-catches exactly that exception type.
/// </para>
/// </summary>
public class J1939TpSinkAdapterTests
{
    /// <summary>
    /// Offline J1939TP layer for adapter tests: <c>J1939TpOptions.Offline</c>
    /// skips the watchdog start (no timer leaks in unit tests) and forbids
    /// all active sends; the send delegate is a no-op returning success.
    /// The logger ctor position is <c>null</c> (nullable parameter, mirrors
    /// the adapter's own null-logger tolerance).
    /// </summary>
    private static J1939TpLayer OfflineLayer() => new(
        (_, _) => ValueTask.FromResult(Result<Unit>.Ok(default)),
        J1939TpOptions.Offline,
        null,
        new Microsoft.Extensions.Time.Testing.FakeTimeProvider());

    [Fact]
    public void Malformed_Tp_Frame_Does_Not_Throw()
    {
        var layer = OfflineLayer();
        var adapter = new J1939TpSinkAdapter(layer, NullLogger<J1939TpSinkAdapter>.Instance);
        var frame = new CanFrame(
            new CanId(J1939Id.Compose(6, 0x00EC00, 0xF4, 0xFF), FrameFormat.Extended),
            new byte[] { 0x99, 0, 0, 0, 0, 0, 2, 0 },   // 未知控制字节
            FrameFlags.None, ChannelId.None, default);

        var act = () => adapter.OnFrame(frame);

        act.Should().NotThrow();     // sink 契约：永不抛（窄捕获 ArgumentException）
    }

    [Fact]
    public void Standard_Frame_Passes_Through_Silently()
    {
        var adapter = new J1939TpSinkAdapter(OfflineLayer(), NullLogger<J1939TpSinkAdapter>.Instance);

        var act = () => adapter.OnFrame(new CanFrame(
            new CanId(0x123, FrameFormat.Standard), new byte[] { 1, 2 }, FrameFlags.None, ChannelId.None, default));

        act.Should().NotThrow();
    }
}
