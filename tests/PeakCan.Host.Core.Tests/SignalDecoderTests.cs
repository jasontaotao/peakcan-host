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

    [Fact]
    public void Length_64_Does_Not_Throw()
    {
        // 64-bit signal at the boundary must decode without throwing.
        var sig = new Signal("S", 0, 64, ByteOrder.LittleEndian, DbcValueType.Unsigned,
            1.0, 0.0, 0, ulong.MaxValue, "u", Array.Empty<string>());
        var data = new byte[8];
        data[0] = 0x01;
        SignalDecoder.Decode(data, sig).Should().Be(1.0);
    }

    [Fact]
    public void Float_Decodes_IEEE_754_Bits()
    {
        // 1.0f as IEEE 754 single = 0x3F800000, little-endian = 00 00 80 3F
        var sig = new Signal("S", 0, 32, ByteOrder.LittleEndian, DbcValueType.Float,
            1.0, 0.0, float.MinValue, float.MaxValue, "u", Array.Empty<string>());
        SignalDecoder.Decode(new byte[] { 0x00, 0x00, 0x80, 0x3F }, sig).Should().Be(1.0);
    }

    [Fact]
    public void Double_Decodes_IEEE_754_Bits()
    {
        // 1.0d as IEEE 754 double = 0x3FF0000000000000, little-endian bytes
        var sig = new Signal("S", 0, 64, ByteOrder.LittleEndian, DbcValueType.Double,
            1.0, 0.0, double.MinValue, double.MaxValue, "u", Array.Empty<string>());
        SignalDecoder.Decode(new byte[]
        {
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F
        }, sig).Should().Be(1.0);
    }

    [Fact]
    public void Signed_16Bit_Negative()
    {
        // 0xFFFE as int16 = -2
        var sig = new Signal("S", 0, 16, ByteOrder.LittleEndian, DbcValueType.Signed,
            1.0, 0.0, short.MinValue, short.MaxValue, "u", Array.Empty<string>());
        SignalDecoder.Decode(new byte[] { 0xFE, 0xFF }, sig).Should().Be(-2.0);
    }

    [Fact]
    public void BigEndian_Signal_At_NonZero_Start()
    {
        // @0 Motorola, start=4, len=4: reads byte 0 bits 4,5,6,7 (MSB-first
        // into the result). 0x5A = 0b01011010 → bit4=1, bit5=0, bit6=1, bit7=0,
        // so the 4-bit value (bit3=bit4, bit0=bit7) is 0b1010 = 10.
        var sig = new Signal("S", 4, 4, ByteOrder.BigEndian, DbcValueType.Unsigned,
            1.0, 0.0, 0, 15, "u", Array.Empty<string>());
        SignalDecoder.Decode(new byte[] { 0x5A }, sig).Should().Be(10.0);
    }

    [Fact]
    public void LittleEndian_Signal_Crosses_Byte_Boundary()
    {
        // start=4, len=12, LE: low nibble of byte 0 (bits 0..3 of result) plus
        // all 8 bits of byte 1 (bits 4..11 of result, byte 1 bit 0 is bit 4).
        // 0xCD=0b11001101, low nibble (bits 4..7 of byte 0 read LSB-first) = 0b1100 = 12.
        // 0xAB=0b10101011 placed at bits 4..11 = 0xAB * 16 = 2736.
        // Total = 12 | 2736 = 2748.
        var sig = new Signal("S", 4, 12, ByteOrder.LittleEndian, DbcValueType.Unsigned,
            1.0, 0.0, 0, 4095, "u", Array.Empty<string>());
        SignalDecoder.Decode(new byte[] { 0xCD, 0xAB }, sig).Should().Be(2748.0);
    }

    [Fact]
    public void BigEndian_Unsigned_8Bit_At_FD_Start_256()
    {
        // CAN FD payloads go up to 64 bytes (= 512 bits), so a Motorola
        // signal can have StartBit > 255. With start=256 the field lives
        // entirely in byte 32. data[32]=0x80 (only the MSB set) should
        // decode to 128.0: in DBC big-endian, start is the MSB of the
        // value, so bit 7 of byte 32 is the MSB of the 8-bit result.
        // The previous Signal.StartBit (byte, max 255) made this impossible
        // to even construct, and (byte)(start + i) inside ReadBigEndian
        // wrapped start=256 to 0 — a silent mis-decode for any signal
        // starting past byte 31.
        var sig = new Signal("S", 256, 8, ByteOrder.BigEndian, DbcValueType.Unsigned,
            1.0, 0.0, 0, 255, "u", Array.Empty<string>());
        var data = new byte[64];
        data[32] = 0x80;
        SignalDecoder.Decode(data, sig).Should().Be(128.0);
    }

    [Fact]
    public void BigEndian_Unsigned_16Bit_At_FD_Start_300()
    {
        // start=300, big-endian, 16 bits: bit 300 is in byte 37 (300/8=37)
        // at bit position 7-(300%8)=3. The 16 bits span bytes 37-39. The
        // full byte-37 bits 3,2,1,0 then byte 38 bits 7,6,5,4,3,2,1,0 then
        // byte 39 bits 7,6,5,4 — read MSB-first into the result. With
        // data[37]=0x0F (low nibble set) and data[38]=0x00 and
        // data[39]=0x00, the value is 0b1111_0000_0000_0000 = 0xF000.
        var sig = new Signal("S", 300, 16, ByteOrder.BigEndian, DbcValueType.Unsigned,
            1.0, 0.0, 0, 65535, "u", Array.Empty<string>());
        var data = new byte[64];
        data[37] = 0x0F;
        SignalDecoder.Decode(data, sig).Should().Be(0xF000);
    }

    [Fact]
    public void LittleEndian_Unsigned_8Bit_At_FD_Start_300()
    {
        // start=300, little-endian, 8 bits: start bit is the LSB of the
        // value. Byte 37 holds the 4 LSBs of the result (at bit positions
        // 4,5,6,7), byte 38 holds the 4 MSBs (at positions 0,1,2,3).
        // data[37]=0xF0 + data[38]=0x0F → result = 0b1111_1111 = 0xFF.
        // The (byte)((start + i) / 8) and (byte)((start + i) % 8) inside
        // ReadLittleEndian wrapped at 256 the same way ReadBigEndian did,
        // silently reading the wrong bytes.
        var sig = new Signal("S", 300, 8, ByteOrder.LittleEndian, DbcValueType.Unsigned,
            1.0, 0.0, 0, 255, "u", Array.Empty<string>());
        var data = new byte[64];
        data[37] = 0xF0;
        data[38] = 0x0F;
        SignalDecoder.Decode(data, sig).Should().Be(0xFF);
    }
}
