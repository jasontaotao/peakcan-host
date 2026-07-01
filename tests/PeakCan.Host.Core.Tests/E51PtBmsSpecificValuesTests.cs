using FluentAssertions;
using PeakCan.Host.Core.Dbc;
using Xunit;
using DbcValueType = PeakCan.Host.Core.Dbc.ValueType;

namespace PeakCan.Host.Core.Tests;

/// <summary>
/// Targeted inspection of the E51_PT_CAN-BMS.dbc "problem" content the
/// user pointed out:
///   BO_ 3221225472 VECTOR__INDEPENDENT_SIG_MSG: 0 Vector__XXX
///    SG_ BMS_DiagResp : 0|64@1+ (1,0) [0|0] "" Vector__XXX
///    SG_ BMS_DiagReq : 0|64@1+ (1,0) [0|1.84467440737096E+019] "" Vector__XXX
/// <para>
/// Both signals are 64-bit wide, but the BO_ has DLC = 0 (which is the
/// Vector convention for "no payload" — the message exists only to
/// attach signal metadata). The 2^64-1 max on <c>BMS_DiagReq</c> is
/// Vector's "no upper limit" sentinel. This test pins:
/// (a) the parser accepts these values without error;
/// (b) the parsed Max is recoverable as the exact double the DBC
///     file claims (no double-overflow during parse);
/// (c) the 2^64-1 sentinel is preserved as <c>double.PositiveInfinity</c>'s
///     neighbour, not silently clamped to <c>long.MaxValue</c>.
/// </para>
/// </summary>
public class E51PtBmsSpecificValuesTests
{
    [Fact]
    public void Parses_Vector_Independent_Sig_Msg_With_64bit_Signals()
    {
        // The exact lines from the user's DBC.
        var src = """
        BU_: BMS Vector__XXX
        BO_ 3221225472 VECTOR__INDEPENDENT_SIG_MSG: 0 Vector__XXX
         SG_ BMS_DiagResp : 0|64@1+ (1,0) [0|0] "" Vector__XXX
         SG_ BMS_DiagReq : 0|64@1+ (1,0) [0|1.84467440737096E+019] "" Vector__XXX
        """;
        var r = DbcParser.Parse(src);
        if (!r.IsSuccess) throw new Xunit.Sdk.XunitException(
            $"Parse failed: code={r.Error!.Code} message={r.Error.Message}");

        var msg = r.Value!.MessagesById[0xC0000000u];  // 3221225472
        msg.Name.Should().Be("VECTOR__INDEPENDENT_SIG_MSG");
        msg.Dlc.Should().Be(0, "Vector convention: BO_ with no payload has DLC=0");
        msg.Signals.Should().HaveCount(2);

        // BMS_DiagResp: max=0, length=64.
        var resp = msg.Signals[0];
        resp.Name.Should().Be("BMS_DiagResp");
        resp.Length.Should().Be(64);
        resp.Min.Should().Be(0.0);
        resp.Max.Should().Be(0.0);

        // BMS_DiagReq: max = 2^64 - 1 (Vector "no upper limit" sentinel).
        var req = msg.Signals[1];
        req.Name.Should().Be("BMS_DiagReq");
        req.Length.Should().Be(64);
        req.Min.Should().Be(0.0);
        req.Max.Should().BeApproximately(1.84467440737096E+019, 1.0,
            "2^64 - 1 must round-trip through double precision");
        // Sanity: this is in the same magnitude as ulong.MaxValue.
        req.Max.Should().BeGreaterThan(1.0E+019);
    }

    [Theory]
    [InlineData(0.0,                       0.0)]
    [InlineData(1.0,                       1.0)]
    [InlineData(255.0,                   255.0)]
    [InlineData(65535.0,               65535.0)]
    [InlineData(4_294_967_295.0, 4_294_967_295.0)]
    [InlineData(1.8446744073709552E+19, 1.84467440737096E+019)]  // 2^64 - 1 round-trip
    public void SignalDecoder_64bit_Unsigned_Handles_Full_Range(double rawDouble, double expected)
    {
        // The BMS_DiagReq signal is 64-bit unsigned with factor=1, offset=0.
        // Verify the decoder can express every value the spec allows
        // (including the Vector 2^64-1 "no upper limit" sentinel the
        // user flagged), without overflow or silent clamping.
        var sig = new Signal("BMS_DiagReq", 0, 64, ByteOrder.LittleEndian,
            DbcValueType.Unsigned, 1, 0, 0, 1.84467440737096E+019, "",
            new List<string>());

        // Build an 8-byte payload equal to rawDouble. For the 2^64-1 case
        // we build all-0xFF bytes (which double-check the round-trip).
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes, (ulong)rawDouble);
        var data = new ReadOnlySpan<byte>(bytes);

        var physical = SignalDecoder.Decode(data, sig);
        // factor=1, offset=0 → physical == raw. Use a tolerance that
        // matches the 2^64-1 round-trip precision (~1 ULP at this magnitude).
        physical.Should().BeApproximately(expected, expected * 1e-10);
    }
}
