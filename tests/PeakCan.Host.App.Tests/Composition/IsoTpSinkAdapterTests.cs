using System.Collections.ObjectModel;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Composition;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.Channel;
using Xunit;

namespace PeakCan.Host.App.Tests.Composition;

/// <summary>
/// Phase 1 P0: <see cref="IsoTpSinkAdapter"/> bridges the Core-layer
/// <see cref="IsoTpLayer"/> (which knows nothing about
/// <see cref="IFrameSink"/>) onto the Infrastructure-layer router fan-out.
/// This is the missing receive wiring — without it the production UDS stack
/// can send requests but never receives ECU responses (verified: no src call
/// site invokes <see cref="IsoTpLayer.ProcessFrame"/> in production code).
/// <para>
/// Contract constraints from <see cref="IFrameSink"/>:
/// <list type="bullet">
///   <item><c>OnFrame</c> MUST NOT throw — the router runs it on the SDK read
///   thread; a throw is forwarded to <c>OnError</c> and (after repeat)
///   auto-detaches the sink.</item>
///   <item><c>OnFrame</c> MUST NOT block.</item>
/// </list>
/// <see cref="IsoTpLayer.ProcessFrame"/> itself is non-blocking, but
/// <see cref="IsoTpFrame.Decode"/> (called at the top of ProcessFrame) throws
/// <see cref="ArgumentException"/> on malformed frames (6 throw sites:
/// empty data, unknown PCI, SF length 0, FF too short, FF length < 8,
/// FC too short). The adapter must therefore narrow-catch
/// <see cref="ArgumentException"/> in <c>OnFrame</c> so a single bad frame
/// does not turn the adapter into a repeatedly-erroring sink that the router
/// auto-detaches after a few frames.
/// </para>
/// </summary>
public class IsoTpSinkAdapterTests
{
    private const uint ReqId = 0x7E0;
    private const uint RespId = 0x7E8;

    private static CanIdConfig DefaultConfig
        => new() { RequestId = ReqId, ResponseId = RespId };

    /// <summary>
    /// Build a real <see cref="IsoTpLayer"/> (sealed, used directly per
    /// the convention in <c>IsoTpLayerTests</c>) and capture its
    /// <see cref="IsoTpLayer.MessageReceived"/> emissions into
    /// <paramref name="received"/>.
    /// </summary>
    private static IsoTpLayer NewLayer(ObservableCollection<byte[]> received)
    {
        var layer = new IsoTpLayer(DefaultConfig, frame => { });
        layer.MessageReceived += msg => received.Add(msg);
        return layer;
    }

    private static CanFrame Frame(uint canId, byte[] data)
        => new(new CanId(canId, FrameFormat.Standard), data, FrameFlags.None, default, default);

    // ---- OnFrame happy path ----

    [Fact]
    public void OnFrame_ValidSingleFrame_ForwardsToIsoTpLayerAndRaisesMessageReceived()
    {
        var received = new ObservableCollection<byte[]>();
        var layer = NewLayer(received);
        var adapter = new IsoTpSinkAdapter(layer);

        // Valid ISO-TP single frame: PCI 0x02 (SF, length 2) + 2 payload bytes.
        adapter.OnFrame(Frame(RespId, new byte[] { 0x02, 0x10, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 }));

        received.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new byte[] { 0x10, 0x03 },
                "the SF payload (2 bytes after the PCI byte) must surface via MessageReceived");
    }

    [Fact]
    public void OnFrame_FrameWithDifferentCanId_DoesNotRaiseMessageReceived()
    {
        // IsoTpLayer internally filters by _config.ResponseId (ReceiveFlow L29).
        // The adapter is a transparent pass-through and must NOT also filter —
        // the filter already lives in the layer, duplicating it would risk
        // drift between adapter and layer CanIdConfig.
        var received = new ObservableCollection<byte[]>();
        var layer = NewLayer(received);
        var adapter = new IsoTpSinkAdapter(layer);

        adapter.OnFrame(Frame(0x7FF, new byte[] { 0x02, 0x10, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 }));

        received.Should().BeEmpty("a frame whose CAN ID != the layer's ResponseId is dropped by the layer");
    }

    // ---- OnFrame malformed-frame resilience (the core P0 fix) ----

    [Theory]
    [InlineData(new byte[] { }, "empty data — Decode throws 'Frame data too short'")]
    [InlineData(new byte[] { 0xFF, 0x00 }, "unknown PCI 0xF — Decode throws 'Unknown PCI'")]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
        "SF length 0 — DecodeSingleFrame throws 'SF length cannot be 0'")]
    [InlineData(new byte[] { 0x30 }, "FC too short (1 byte) — DecodeFlowControl throws 'FC data too short'")]
    public void OnFrame_MalformedFrame_DoesNotThrow(byte[] data, string scenario)
    {
        // IFrameSink.OnFrame MUST NOT throw. IsoTpFrame.Decode throws
        // ArgumentException on all 6 malformed inputs; the adapter must
        // narrow-catch it so a single bad frame does not turn the adapter
        // into a throw-on-every-frame sink that the router auto-detaches.
        var received = new ObservableCollection<byte[]>();
        var layer = NewLayer(received);
        var adapter = new IsoTpSinkAdapter(layer);

        var act = () => adapter.OnFrame(Frame(RespId, data));

        act.Should().NotThrow(scenario);
        received.Should().BeEmpty("a malformed frame yields no complete message");
    }

    [Fact]
    public void OnFrame_MalformedFrame_ThenValidFrame_StillDeliversValidFrame()
    {
        // The narrow-catch must not leave the layer in a poisoned state —
        // the next valid frame must still be reassembled and delivered.
        var received = new ObservableCollection<byte[]>();
        var layer = NewLayer(received);
        var adapter = new IsoTpSinkAdapter(layer);

        adapter.OnFrame(Frame(RespId, new byte[] { 0xFF, 0x00 }));       // malformed: unknown PCI
        adapter.OnFrame(Frame(RespId, new byte[] { 0x02, 0x10, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 })); // valid SF

        received.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new byte[] { 0x10, 0x03 });
    }

    // ---- OnError (sibling-sink failure channel, not our own) ----

    [Fact]
    public void OnError_DoesNotThrowAndDoesNotInvokeProcessFrame()
    {
        // OnError is called by the router when ANOTHER sink in the same
        // fan-out throws; it is informational. It must not throw (router
        // auto-detaches sinks whose OnError throws) and must not touch
        // the IsoTpLayer — the layer has no notion of sibling errors.
        var received = new ObservableCollection<byte[]>();
        var layer = NewLayer(received);
        var adapter = new IsoTpSinkAdapter(layer);

        var act = () => adapter.OnError(new InvalidOperationException("sibling boom"));

        act.Should().NotThrow();
        received.Should().BeEmpty();
    }

    // ---- ctor guards ----

    [Fact]
    public void Ctor_NullIsoTpLayer_Throws()
    {
        var act = () => new IsoTpSinkAdapter(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullLogger_IsAllowedForTestFixtures()
    {
        // Mirrors ChannelRouter's own null-logger tolerance (tests / back-compat).
        var layer = NewLayer(new ObservableCollection<byte[]>());
        var act = () => new IsoTpSinkAdapter(layer, logger: null);
        act.Should().NotThrow();
    }
}
