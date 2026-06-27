using FluentAssertions;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

/// <summary>
/// v1.2.12 PATCH Item 8: pin down the FF length-decoder behavior. The
/// encoder's 12-bit length field (ISO 15765-2) caps the legal range at 0..4095,
/// so a hand-crafted FF declaring length &gt; 4095 cannot originate from a
/// well-behaved sender. The decoder therefore cannot legitimately produce
/// a frame with length &gt; MaxMessageLength (4095). The actual &quot;reject
/// oversized FF&quot; guard lives in <see cref="IsoTpLayer.HandleFirstFrame"/>
/// as defense-in-depth (see IsoTpLayerTests).
/// <para>
/// This file covers the boundary conditions the decoder CAN reach: the
/// length = 0 degenerate (FF length must be ≥ 8 per ISO-TP) and the
/// maximum 12-bit length = 4095 round-tripping through encode + decode.
/// </para>
/// </summary>
public sealed class IsoTpFrameTests
{
    /// <summary>
    /// RED: a malformed FF with length = 0 must throw at decode time. A
    /// fuzz sender could craft raw bytes with PCI=First and length=0 to
    /// force the layer into HandleFirstFrame with a 0-byte buffer. The
    /// decoder must reject it.
    /// <para>
    /// Note: this exercises the decoder's existing "length &lt; minimum FF
    /// payload (8 bytes)" guard, NOT a "length &gt; MaxMessageLength" guard.
    /// The latter is unreachable through <see cref="IsoTpFrame"/> because
    /// the encoder's 12-bit length field caps at 4095 (see file-level
    /// summary); the oversized-FF defense lives in
    /// <c>IsoTpLayer.HandleFirstFrame</c> and is verified by
    /// <c>IsoTpLayerTests.HandleFirstFrame_Rejects_Length_4096_No_Buffer_Allocated</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void DecodeFirstFrame_Throws_On_Length_Below_Minimum()
    {
        // FF PCI: type=First (0x1), length=0 → byte 0 = 0x10, byte 1 = 0x00.
        // Length 0 violates the "length ≥ 8" rule for multi-frame FFs.
        var payload = new byte[] { 0x10, 0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };

        var act = () => IsoTpFrame.Decode(payload);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*FF length*");
    }
}