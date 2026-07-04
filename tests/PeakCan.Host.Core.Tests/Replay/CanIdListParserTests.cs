using FluentAssertions;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay;

/// <summary>
/// v3.4.4 PATCH: shared CAN-ID allow-list parser tests. Replaces the
/// v3.4.2 <c>CanIdFilterParserTests</c> (deleted with
/// <c>CanIdFilterParser</c>) plus two new Replay-behavior guarantees
/// for the unified <see cref="CanIdListParser"/>. The 4 v3.4.2 tests
/// are preserved here as <c>Parse_*</c> cases — plus 5 net new
/// scenarios (whitespace separators, mixed separators, all-invalid,
/// case-insensitive hex upper/lower, and hex-prefix-only rejection).
/// </summary>
public class CanIdListParserTests
{
    // CA1861: prefer static readonly arrays over inline literal initializers
    // when the same array is passed to multiple Should().BeEquivalentTo(...)
    // assertions across tests. Lifted from the test methods to avoid
    // TreatWarningsAsErrors build failures.
    private static readonly string[] XyzAndZzz = { "xyz", "0xZZZ" };
    private static readonly string[] XyzAndGarbage = { "xyz", "garbage" };
    private static readonly string[] JustZeroX = { "0x" };

    [Fact]
    public void Parse_Empty_ReturnsClearFilter()
    {
        var result = CanIdListParser.Parse("");

        result.AllowList.Should().BeNull();
        result.InvalidTokens.Should().BeEmpty();
        result.HasInvalidTokens.Should().BeFalse();
        result.Should().Be(CanIdParseResult.Empty);
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsClearFilter()
    {
        var result = CanIdListParser.Parse("   \t\n ");

        result.AllowList.Should().BeNull();
        result.InvalidTokens.Should().BeEmpty();
        result.Should().Be(CanIdParseResult.Empty);
    }

    [Fact]
    public void Parse_SingleDecimal_AllowListContains()
    {
        var result = CanIdListParser.Parse("256");

        result.AllowList.Should().NotBeNull();
        result.AllowList.Should().BeEquivalentTo(new HashSet<uint> { 256u });
        result.InvalidTokens.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SingleHex_AllowListContains()
    {
        var result = CanIdListParser.Parse("0x100");

        result.AllowList.Should().NotBeNull();
        result.AllowList.Should().BeEquivalentTo(new HashSet<uint> { 0x100u });
        result.InvalidTokens.Should().BeEmpty();
    }

    [Fact]
    public void Parse_CommaSeparated_MultipleIdsInSet()
    {
        var result = CanIdListParser.Parse("100, 0x200, 300");

        result.AllowList.Should().NotBeNull();
        result.AllowList.Should().BeEquivalentTo(new HashSet<uint> { 100u, 0x200u, 300u });
        result.InvalidTokens.Should().BeEmpty();
    }

    [Fact]
    public void Parse_WhitespaceSeparated_NewAcceptance()
    {
        // v3.4.4 NEW (vs v3.4.2 comma-only): Trace Viewer now accepts
        // whitespace separators in addition to commas — same as Replay
        // already did. No existing test asserted the absence.
        var result = CanIdListParser.Parse("100 200 0x300");

        result.AllowList.Should().NotBeNull();
        result.AllowList.Should().BeEquivalentTo(new HashSet<uint> { 100u, 200u, 0x300u });
        result.InvalidTokens.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MixedSeparators_Flexible()
    {
        // v3.4.4 NEW: any mix of comma + space + tab + newline is accepted.
        var result = CanIdListParser.Parse("100, 200 0x300\n400");

        result.AllowList.Should().NotBeNull();
        result.AllowList.Should().BeEquivalentTo(new HashSet<uint> { 100u, 200u, 0x300u, 400u });
        result.InvalidTokens.Should().BeEmpty();
    }

    [Fact]
    public void Parse_InvalidTokens_CollectedInList_NoIdsDropped()
    {
        // "100" valid, "xyz" + "0xZZZ" invalid → only the valid id is in the set,
        // both invalid tokens land in InvalidTokens. Mirrors the Replay v1.5.0
        // behavior: a single typo doesn't wipe the user's work.
        var result = CanIdListParser.Parse("100, xyz, 0xZZZ");

        result.AllowList.Should().NotBeNull();
        result.AllowList.Should().BeEquivalentTo(new HashSet<uint> { 100u });
        result.InvalidTokens.Should().BeEquivalentTo(XyzAndZzz);
        result.HasInvalidTokens.Should().BeTrue();
    }

    [Fact]
    public void Parse_AllInvalidTokens_EmptyAllowSet_InvalidCollected()
    {
        // All-invalid input → empty set (NOT null — null is reserved for
        // "no filter, all pass"). Replay surfaces this as "all tokens
        // invalid → service emits nothing" per the v1.5.0 tri-state
        // contract.
        var result = CanIdListParser.Parse("xyz, garbage");

        result.AllowList.Should().NotBeNull();
        result.AllowList.Should().BeEmpty();
        result.InvalidTokens.Should().BeEquivalentTo(XyzAndGarbage);
        result.HasInvalidTokens.Should().BeTrue();
    }

    [Fact]
    public void Parse_CaseInsensitiveHex_0xAbCAnd0XABCSameId()
    {
        // v3.4.2 already supported case-insensitive hex; explicitly pin
        // both "0x" and "0X" prefixes (StartWith("0x",
        // OrdinalIgnoreCase)) per [[allowhexspecifier-rejects-0x-prefix]].
        var r1 = CanIdListParser.Parse("0xAbC");
        var r2 = CanIdListParser.Parse("0XABC");

        r1.AllowList.Should().NotBeNull();
        r1.AllowList.Should().BeEquivalentTo(new HashSet<uint> { 0xABCu });
        r2.AllowList.Should().NotBeNull();
        r2.AllowList.Should().BeEquivalentTo(new HashSet<uint> { 0xABCu });
    }

    [Fact]
    public void Parse_HexDigitsOnly_Literal0xPrefixRejectedAsInvalid()
    {
        // "0x" alone (no digits after the prefix) → invalid token. The
        // strip yields empty digits → falls into the invalid branch.
        var result = CanIdListParser.Parse("0x");

        // Empty digits with no valid ids and one invalid → empty set + invalid list.
        // (Tri-state: not null because we have an invalid token.)
        result.AllowList.Should().NotBeNull();
        result.AllowList.Should().BeEmpty();
        result.InvalidTokens.Should().BeEquivalentTo(JustZeroX);
        result.HasInvalidTokens.Should().BeTrue();
    }
}