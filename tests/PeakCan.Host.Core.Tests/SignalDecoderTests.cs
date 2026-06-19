using FluentAssertions;
using PeakCan.Host.Core.Dbc;
using Xunit;
using DbcValueType = PeakCan.Host.Core.Dbc.ValueType;

namespace PeakCan.Host.Core.Tests;

/// <summary>
/// Task 7: verifies that <see cref="SignalDecoder"/> correctly extracts the
/// raw bit field, applies byte-ordering, sign-extension, and engineering
/// factor/offset. DBC's little-endian convention grows toward higher byte
/// indices; big-endian starts at the MSB of byte 0.
/// </summary>
public class SignalDecoderTests
{
    private static Signal U16Little(int start = 0) => new(
        "S", (byte)start, 16, ByteOrder.LittleEndian, DbcValueType.Unsigned,
        1.0, 0.0, 0, 65535, "u", Array.Empty<string>());

    [Fact]
    public void LittleEndian_Unsigned_16Bit_At_Start()
    {
        byte[] data = { 0xCD, 0xAB };
        SignalDecoder.Decode(data, U16Little()).Should().Be(0xABCD);
    }

    [Fact]
    public void LittleEndian_With_Factor_And_Offset()
    {
        var sig = new Signal("S", 0, 16, ByteOrder.LittleEndian, DbcValueType.Unsigned,
            0.1, 5.0, 0, 0, "u", Array.Empty<string>());
        byte[] data = { 0x10, 0x00 };  // raw = 16
        // physical = 16 * 0.1 + 5.0 = 6.6
        SignalDecoder.Decode(data, sig).Should().BeApproximately(6.6, 1e-9);
    }

    [Fact]
    public void Zero_Length_Signal_Returns_Zero()
    {
        var sig = new Signal("S", 0, 0, ByteOrder.LittleEndian, DbcValueType.Unsigned,
            1.0, 0.0, 0, 0, "", Array.Empty<string>());
        SignalDecoder.Decode(new byte[] { 0xFF }, sig).Should().Be(0.0);
    }

    [Fact]
    public void BigEndian_Unsigned_16Bit_At_Start()
    {
        // @0 = Motorola / big-endian: 0xABCD on the wire = 0xABCD in the value.
        var sig = new Signal("S", 0, 16, ByteOrder.BigEndian, DbcValueType.Unsigned,
            1.0, 0.0, 0, 65535, "u", Array.Empty<string>());
        byte[] data = { 0xAB, 0xCD };
        SignalDecoder.Decode(data, sig).Should().Be(0xABCD);
    }

    [Fact]
    public void LittleEndian_Signed_8Bit_Negative()
    {
        // 0xFF as int8 = -1
        var sig = new Signal("S", 0, 8, ByteOrder.LittleEndian, DbcValueType.Signed,
            1.0, 0.0, -128, 127, "u", Array.Empty<string>());
        SignalDecoder.Decode(new byte[] { 0xFF }, sig).Should().Be(-1.0);
    }

    [Fact]
    public void Length_Above_64_Throws()
    {
        var sig = new Signal("S", 0, 65, ByteOrder.LittleEndian, DbcValueType.Unsigned,
            1.0, 0.0, 0, 0, "u", Array.Empty<string>());
        var act = () => SignalDecoder.Decode(new byte[16], sig);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
