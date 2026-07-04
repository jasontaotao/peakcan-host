using FluentAssertions;
using PeakCan.Host.App.Services.Trace;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// v3.4.2 PATCH: pins the global Trace Viewer "Filter CAN IDs" string
/// parser contract. Empty input → null (no filter). Decimal and
/// 0x-hex (case-insensitive) supported. Invalid entries silently skipped.
/// </summary>
public class CanIdFilterParserTests
{
    [Fact]
    public void Parse_Empty_ReturnsNull()
    {
        var result = CanIdFilterParser.Parse("");

        result.Should().BeNull();
    }

    [Fact]
    public void Parse_SingleDecimal_ReturnsHashSet()
    {
        var result = CanIdFilterParser.Parse("256");

        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new HashSet<uint> { 256u });
    }

    [Fact]
    public void Parse_SingleHex_ReturnsHashSet()
    {
        var result = CanIdFilterParser.Parse("0x100");

        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new HashSet<uint> { 0x100u });
    }

    [Fact]
    public void Parse_MultipleMixedWithInvalid_SkipsInvalidKeepsValid()
    {
        // "xyz" and "##" are not valid integers → silently dropped.
        // "0xABC" and "0xabc" must yield the same ID (case-insensitive hex).
        var result = CanIdFilterParser.Parse("100, xyz, 0xABC, 200, ##");

        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new HashSet<uint> { 100u, 0xABCu, 200u });
    }
}