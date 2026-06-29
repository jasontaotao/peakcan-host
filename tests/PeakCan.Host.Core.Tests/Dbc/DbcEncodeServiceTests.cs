using FluentAssertions;
using PeakCan.Host.Core.Dbc;
using Xunit;
using DbcValueType = PeakCan.Host.Core.Dbc.ValueType;

namespace PeakCan.Host.Core.Tests.Dbc;

public class DbcEncodeServiceTests
{
    private readonly DbcEncodeService _sut = new();

    private static Message MakeMessage(params Signal[] signals) => new(
        Id: 0x100u,
        Name: "TestMsg",
        Dlc: 8,
        Sender: "TestNode",
        Signals: signals,
        IsMultiplexed: false,
        MultiplexorSignalIndex: null);

    private static Signal SignalUnsigned(string name, ushort start, byte length, double factor = 1, double offset = 0, double min = 0, double max = 100)
        => new(name, start, length, ByteOrder.LittleEndian, DbcValueType.Unsigned, factor, offset, min, max, "", Array.Empty<string>());

    private static Signal SignalSigned(string name, ushort start, byte length, double min = -100, double max = 100)
        => new(name, start, length, ByteOrder.LittleEndian, DbcValueType.Signed, 1, 0, min, max, "", Array.Empty<string>());

    private static Signal SignalBigEndian(string name, ushort start, byte length, double min = 0, double max = 100)
        => new(name, start, length, ByteOrder.BigEndian, DbcValueType.Unsigned, 1, 0, min, max, "", Array.Empty<string>());

    private static Signal SignalFloat(string name, ushort start, double min = float.MinValue, double max = float.MaxValue)
        => new(name, start, 32, ByteOrder.LittleEndian, DbcValueType.Float, 1, 0, min, max, "", Array.Empty<string>());

    private static Signal SignalMux(string name, byte length, ushort muxValue = 0, ushort start = 0)
        => new(name, start, length, ByteOrder.LittleEndian, DbcValueType.Unsigned, 1, 0, 0, ushort.MaxValue, "",
               Array.Empty<string>(), IsMultiplexed: true, MultiplexValue: muxValue);

    private static Signal SignalMuxor(string name, byte length)
        => new(name, 0, length, ByteOrder.LittleEndian, DbcValueType.Unsigned, 1, 0, 0, ushort.MaxValue, "",
               Array.Empty<string>(), IsMultiplexor: true);

    /// <summary>
    /// v1.4.0 MINOR DBC encode: simple signal (Factor=1, Offset=0, LittleEndian, Unsigned)
    /// → raw = physical, packed LSB-first at StartBit.
    /// </summary>
    [Fact]
    public void Encode_SimpleSignal_NoScaleNoOffset_LittleEndian()
    {
        // Signal at bit 0, length 8, value 0x42 (66) → byte[0] = 0x42
        var sig = SignalUnsigned("TestSig", start: 0, length: 8);
        var msg = MakeMessage(sig);
        var values = new Dictionary<string, double> { ["TestSig"] = 0x42 };

        var bytes = _sut.Encode(msg, values);

        bytes[0].Should().Be(0x42);
        bytes[1].Should().Be(0x00);
    }

    /// <summary>
    /// v1.4.0 MINOR: signed signal with negative value packs as two's-complement.
    /// </summary>
    [Fact]
    public void Encode_SignedSignal_NegativeValue()
    {
        // 8-bit signed at bit 0, value -1 → 0xFF
        var sig = SignalSigned("SignedSig", start: 0, length: 8);
        var msg = MakeMessage(sig);
        var values = new Dictionary<string, double> { ["SignedSig"] = -1.0 };

        var bytes = _sut.Encode(msg, values);
        bytes[0].Should().Be(0xFF);
    }

    /// <summary>
    /// v1.4.0 MINOR: big-endian signal packs MSB-first at StartBit.
    /// </summary>
    [Fact]
    public void Encode_BigEndianSignal_PacksAtStartBit()
    {
        // 4-bit big-endian (Motorola) at start=12, value 0xA → byte[1] = 0x0A.
        // SignalDecoder.ReadBigEndian reads byte[1] bits 3,2,1,0 (LSB-first
        // within the byte) at start=12, len=4, so writing 0xA into those
        // four bits produces 0x0A (low nibble = 0xA). The brief's expected
        // 0xA0 is a brief-vs-source drift: that pattern would be bit 5 + bit 7
        // set, which the decoder would NOT read at start=12 (it reads bits
        // 0-3 of the byte). The 0x0A round-trips back to 0xA via
        // SignalDecoder.Decode.
        var sig = SignalBigEndian("BigSig", start: 12, length: 4);
        var msg = MakeMessage(sig);
        var values = new Dictionary<string, double> { ["BigSig"] = 0xA };

        var bytes = _sut.Encode(msg, values);
        bytes[0].Should().Be(0x00);
        bytes[1].Should().Be(0x0A);
    }

    /// <summary>
    /// v1.4.0 MINOR: float signal (IEEE-754 single-precision) reinterprets bits.
    /// </summary>
    [Fact]
    public void Encode_FloatSignal_Ieee754Bits()
    {
        // 32-bit float at bit 0, value 1.0f → 0x3F800000 → bytes[0..3]
        var sig = SignalFloat("FloatSig", start: 0);
        var msg = MakeMessage(sig);
        var values = new Dictionary<string, double> { ["FloatSig"] = 1.0f };

        var bytes = _sut.Encode(msg, values);
        var roundtrip = BitConverter.SingleToInt32Bits(1.0f);
        bytes[0].Should().Be((byte)(roundtrip & 0xFF));
        bytes[1].Should().Be((byte)((roundtrip >> 8) & 0xFF));
        bytes[2].Should().Be((byte)((roundtrip >> 16) & 0xFF));
        bytes[3].Should().Be((byte)((roundtrip >> 24) & 0xFF));
    }

    /// <summary>
    /// v1.4.0 MINOR: scale + offset inverse transform.
    /// </summary>
    [Fact]
    public void Encode_ScaleAndOffset_InverseTransform()
    {
        // Factor=0.1, Offset=-40 → raw = (physical - (-40)) / 0.1 = (physical + 40) * 10
        // physical = 60 → raw = (60+40)*10 = 1000
        var sig = SignalUnsigned("ScaledSig", start: 0, length: 16, factor: 0.1, offset: -40);
        var msg = MakeMessage(sig);
        var values = new Dictionary<string, double> { ["ScaledSig"] = 60.0 };

        var bytes = _sut.Encode(msg, values);
        // 1000 = 0x03E8 → little-endian → bytes[0]=0xE8, bytes[1]=0x03
        bytes[0].Should().Be(0xE8);
        bytes[1].Should().Be(0x03);
    }

    /// <summary>
    /// v1.4.0 MINOR: multiplexed message packs mux value + selected signal.
    /// </summary>
    [Fact]
    public void Encode_MultiplexedMessage_PacksMuxValue()
    {
        var muxor = SignalMuxor("Mux", length: 4);
        // Muxor at bits 0-3 of byte 0; SigA at byte 1 (bits 8-15); SigB at byte 1.
        var sigA = SignalMux("SigA", length: 8, muxValue: 0, start: 8);
        var sigB = SignalMux("SigB", length: 8, muxValue: 1, start: 8);
        var msg = new Message(
            Id: 0x100u, Name: "MuxMsg", Dlc: 8, Sender: "Test",
            Signals: new[] { muxor, sigA, sigB },
            IsMultiplexed: true,
            MultiplexorSignalIndex: 0);
        var values = new Dictionary<string, double>
        {
            ["Mux"] = 1,  // select mux group 1
            ["SigB"] = 0x55
        };

        var bytes = _sut.Encode(msg, values);
        // Mux at bit 0-3 (little-endian) = 1 → byte[0] = 0x01
        // SigB at bit 8-15 (little-endian) = 0x55 → byte[1] = 0x55
        bytes[0].Should().Be(0x01);
        bytes[1].Should().Be(0x55);
    }

    /// <summary>
    /// v1.4.0 MINOR: signal out of [Min, Max] range throws.
    /// </summary>
    [Fact]
    public void Encode_SignalOutOfRange_Throws()
    {
        var sig = SignalUnsigned("RangedSig", start: 0, length: 8, min: 0, max: 100);
        var msg = MakeMessage(sig);
        var values = new Dictionary<string, double> { ["RangedSig"] = 200.0 };

        Func<byte[]> act = () => _sut.Encode(msg, values);
        act.Should().Throw<DbcSignalValueOutOfRangeException>()
            .Which.SignalName.Should().Be("RangedSig");
    }

    /// <summary>
    /// v1.4.0 MINOR: unknown signal name in values throws.
    /// </summary>
    [Fact]
    public void Encode_UnknownSignal_Throws()
    {
        var sig = SignalUnsigned("KnownSig", start: 0, length: 8);
        var msg = MakeMessage(sig);
        var values = new Dictionary<string, double> { ["UnknownSig"] = 42.0 };

        Func<byte[]> act = () => _sut.Encode(msg, values);
        act.Should().Throw<DbcSignalNotFoundException>()
            .Which.SignalName.Should().Be("UnknownSig");
    }

    /// <summary>
    /// v1.4.0 MINOR: multiplexed message missing mux value throws.
    /// </summary>
    [Fact]
    public void Encode_MissingMultiplexor_Throws()
    {
        var muxor = SignalMuxor("Mux", length: 4);
        var sigA = SignalMux("SigA", length: 8, muxValue: 0);
        var msg = new Message(
            Id: 0x100u, Name: "MuxMsg", Dlc: 8, Sender: "Test",
            Signals: new[] { muxor, sigA },
            IsMultiplexed: true,
            MultiplexorSignalIndex: 0);
        var values = new Dictionary<string, double> { ["SigA"] = 42.0 };  // no Mux

        Func<byte[]> act = () => _sut.Encode(msg, values);
        act.Should().Throw<DbcMultiplexorRequiredException>()
            .Which.MessageName.Should().Be("MuxMsg");
    }

    /// <summary>
    /// v1.4.0 MINOR ROUND-TRIP (v1.3.1 lesson): encode(physical) → decode → equals original.
    /// Catches asymmetric encoding bugs that pure forward tests miss.
    /// </summary>
    [Fact]
    public void Encode_Decode_Roundtrip_PreservesValues()
    {
        var sig = SignalUnsigned("RoundtripSig", start: 0, length: 16, factor: 0.1, offset: -40, min: 0, max: 1000);
        var msg = MakeMessage(sig);
        var values = new Dictionary<string, double> { ["RoundtripSig"] = 73.5 };

        var bytes = _sut.Encode(msg, values);
        var decoded = SignalDecoder.Decode(bytes, sig);

        // Allow tiny rounding error (0.1 factor × 0.5 raw step = 0.05 engineering)
        decoded.Should().BeApproximately(73.5, 0.05);
    }
}
