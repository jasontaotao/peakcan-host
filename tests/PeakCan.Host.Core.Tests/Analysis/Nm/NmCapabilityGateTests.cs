using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmCapabilityGateTests
{
    // CA1861: 常量数组不内联，避免每次构造都分配。
    private static readonly string[] CannmReasons = ["MsgType=CANNM"];

    private static ReplayFrame Frame(double t, uint id, bool ext, params byte[] data)
        => new(t, id, (byte)data.Length, data, FrameFlags.None, ext);

    private static NmProfile Profile(bool withLayout) => new(
        NmProfileSource.Dbc,
        new[]
        {
            new NmMessageDef("0x18FFFAF4", true, "BMS_NWM", "BMS", 8, true,
                CannmReasons, 500, 2000, true),
        },
        new NmTimings(CycleMs: 500, TimeoutMs: 2000),
        new[] { new NmNodeDef("BMS", 0xF4, null, true) },
        Array.Empty<NmConfigConflict>(),
        Array.Empty<string>(),
        new NmProvenance(),
        Layout: withLayout ? new NmLayout(0, 1, 2, 0) : null,
        Features: withLayout ? new NmFeatures(PnEnabled: false) : null,
        // 「完整 profile」必须声明 PNI 位角色：PnCheck 的判据现在包含角色存在性，
        // 没有角色时 CheckCbv 不执行，能力矩阵也不应声称可用。
        StandardBitRoles: withLayout
            ? new[] { new NmBitRole(1, 6, "PartialNetworkInfo") }
            : null);

    private static ReplayFrame[] Frames()
        => Enumerable.Range(0, 20)
            .Select(i => Frame(i * 0.5, 0x18FFFAF4, true, 0xF4, 0x00, 0, 0, 0, 0, 0, 0))
            .ToArray();

    [Fact]
    public void Evaluate_CompleteProfile_EnablesSemanticCapabilities()
    {
        var (caps, missing) = NmCapabilityGate.Evaluate(Profile(true), Frames(), null);

        Assert.True(caps.Single(c => c.Capability == NmCapability.IdentifyMessages).Available);
        Assert.True(caps.Single(c => c.Capability == NmCapability.Timing).Available);
        Assert.True(caps.Single(c => c.Capability == NmCapability.NidCheck).Available);
        Assert.True(caps.Single(c => c.Capability == NmCapability.CbvDecode).Available);
        Assert.True(caps.Single(c => c.Capability == NmCapability.UserData).Available);
        Assert.Empty(missing);
    }

    [Fact]
    public void Evaluate_NoLayout_ReportsMissingNidAndCbvWithSuggestions()
    {
        var (caps, missing) = NmCapabilityGate.Evaluate(Profile(false), Frames(), null);

        Assert.False(caps.Single(c => c.Capability == NmCapability.NidCheck).Available);
        var nid = Assert.Single(missing, m => m.InputKey == "nid_byte");
        Assert.Equal(NmCapability.NidCheck, nid.Capability);
        Assert.False(string.IsNullOrWhiteSpace(nid.Why));
        Assert.False(string.IsNullOrWhiteSpace(nid.Suggestion));
        Assert.Contains(missing, m => m.InputKey == "cbv_byte");
    }

    [Fact]
    public void Evaluate_NoNmMessages_ReportsIdentifyMissingAndNoGuess()
    {
        var profile = Profile(true) with
        {
            Messages = new[]
            {
                new NmMessageDef("0x18FFFAF4", true, "BMS_NWM", "BMS", 8, false, Array.Empty<string>()),
            },
        };
        var (caps, missing) = NmCapabilityGate.Evaluate(profile, Frames(), null);

        Assert.False(caps.Single(c => c.Capability == NmCapability.IdentifyMessages).Available);
        var identify = Assert.Single(missing, m => m.InputKey == "nm_message_selection");
        Assert.Contains("NWM", identify.Suggestion);
    }

    [Fact]
    public void SuggestNidByte_FindsConstantByteEqualToIdLowByte()
    {
        var frames = Enumerable.Range(0, 10)
            .Select(i => Frame(i * 0.5, 0x18FFFAF4, true, 0xF4, 0x00, 1, 2, 3, 4, 5, 6))
            .ToArray();

        var suggestion = NmSuggestionEngine.SuggestNidByte(frames, 0x18FFFAF4, isExtended: true, stationAddress: 0xF4);

        Assert.Equal((byte?)0, suggestion.Byte);
        Assert.Contains("0xF4", suggestion.Evidence);
    }

    [Fact]
    public void SuggestCycleMs_UsesMedianAndRequiresFiveSamples()
    {
        var frames = Enumerable.Range(0, 6)
            .Select(i => Frame(i * 0.5, 0x18FFFAF4, true, 0xF4))
            .ToArray();

        Assert.Equal(500, NmSuggestionEngine.SuggestCycleMs(frames, 0x18FFFAF4, true));

        var tooFew = frames.Take(4).ToArray();
        Assert.Null(NmSuggestionEngine.SuggestCycleMs(tooFew, 0x18FFFAF4, true));
    }

    [Fact]
    public void SuggestNidByte_ShortFrames_DoesNotThrowAndReturnsNull()
    {
        // 全部帧只有 2 字节：逐字节扫描必须在长度守卫之后取值，不能越界。
        var frames = Enumerable.Range(0, 10)
            .Select(i => Frame(i * 0.5, 0x18FFFAF4, true, 0x11, 0x22))
            .ToArray();

        var suggestion = NmSuggestionEngine.SuggestNidByte(frames, 0x18FFFAF4, isExtended: true, stationAddress: 0xF4);

        Assert.Null(suggestion.Byte);
    }

    [Fact]
    public void SuggestNidByte_UsesModeAndReportsRatio()
    {
        // 8/10 帧 byte0=0xF4，其余 2 帧为 0x27；站地址 0xF4。
        var frames = Enumerable.Range(0, 10)
            .Select(i => Frame(i * 0.5, 0x18FFFAF4, true, i < 8 ? (byte)0xF4 : (byte)0x27, 0x00))
            .ToArray();

        var suggestion = NmSuggestionEngine.SuggestNidByte(frames, 0x18FFFAF4, isExtended: true, stationAddress: 0xF4);

        Assert.Equal((byte?)0, suggestion.Byte);
        Assert.Contains("8/10", suggestion.Evidence);
        Assert.Contains("80%", suggestion.Evidence);
    }

    [Fact]
    public void Evaluate_TimingFromProfileTimings_IsAvailable()
    {
        var profile = Profile(true) with
        {
            Messages = new[]
            {
                new NmMessageDef("0x18FFFAF4", true, "BMS_NWM", "BMS", 8, true, Array.Empty<string>()),
            },
            Timings = new NmTimings(CycleMs: 500),
        };

        var (caps, _) = NmCapabilityGate.Evaluate(profile, Frames(), null);

        Assert.True(caps.Single(c => c.Capability == NmCapability.Timing).Available);
    }

    [Fact]
    public void Evaluate_NidConfiguredWithoutStationAddress_ReportsNodeMappingMissing()
    {
        var profile = Profile(true) with
        {
            Nodes = new[] { new NmNodeDef("BMS", null, null, true) },
        };

        var (caps, missing) = NmCapabilityGate.Evaluate(profile, Frames(), null);

        Assert.False(caps.Single(c => c.Capability == NmCapability.NidCheck).Available);
        Assert.Contains(missing, m => m.InputKey == "nm_node_mapping");
    }

    // ---------------- §6.6.4 建议接线 ----------------

    [Fact]
    public void Evaluate_MissingTimeout_SuggestsThreeTimesCycleAsAssumption()
    {
        var profile = Profile(false) with
        {
            Messages = new[]
            {
                new NmMessageDef("0x18FFFAF4", true, "BMS_NWM", "BMS", 8, true,
                    CannmReasons, 500, null, true),
            },
            Timings = new NmTimings(CycleMs: 500),
        };

        var (_, missing) = NmCapabilityGate.Evaluate(profile, Frames(), null);

        var timeout = Assert.Single(missing, m => m.InputKey == "timeout_ms");
        Assert.Equal("assumption", timeout.SuggestionSource);
        Assert.Contains("1500", timeout.Suggestion);
        Assert.Contains("3×cycle", timeout.Suggestion);
    }

    [Fact]
    public void SuggestNidByteCandidates_ReturnsTopTwoWithEvidence()
    {
        var frames = Enumerable.Range(0, 10)
            .Select(i => Frame(i * 0.5, 0x18FFFAF4, true, 0xF4, 0xF4, 0, 0, 0, 0, 0, 0))
            .ToArray();

        var candidates = NmSuggestionEngine.SuggestNidByteCandidates(
            frames, 0x18FFFAF4, isExtended: true, stationAddress: 0xF4);

        Assert.Equal(2, candidates.Count);
        Assert.Equal((byte)0, candidates[0].Byte);
        Assert.Equal((byte)1, candidates[1].Byte);
        Assert.All(candidates, c => Assert.Contains("0xF4", c.Evidence));
    }

    [Fact]
    public void SuggestCbvByte_PicksFewestDistinctValuesThenMostBitFlips()
    {
        // byte0 恒定（无控制位信息）；byte1 只有两种取值且反复翻转 → CBV 候选。
        var frames = Enumerable.Range(0, 10)
            .Select(i => Frame(i * 0.5, 0x18FFFAF4, true,
                0xF4, (byte)(i % 2 == 0 ? 0x00 : 0x10), 0, 0))
            .ToArray();

        var suggestion = NmSuggestionEngine.SuggestCbvByte(frames, 0x18FFFAF4, true);

        Assert.Equal((byte?)1, suggestion.Byte);
        Assert.Contains("位翻转", suggestion.Evidence);
    }

    [Fact]
    public void SuggestUserData_TailAllZero_SuggestsZeroLength()
    {
        var frames = Enumerable.Range(0, 6)
            .Select(i => Frame(i * 0.5, 0x18FFFAF4, true, 0xF4, 0x00, 0, 0, 0, 0, 0, 0))
            .ToArray();

        var suggestion = NmSuggestionEngine.SuggestUserData(frames, 0x18FFFAF4, true);

        Assert.Equal((byte?)2, suggestion.Offset);
        Assert.Equal(0, suggestion.Length);
        Assert.Contains("length 0", suggestion.Evidence);
    }

    [Fact]
    public void SuggestUserData_NonZeroTail_ReportsRangeEvidence()
    {
        var frames = Enumerable.Range(0, 6)
            .Select(i => Frame(i * 0.5, 0x18FFFAF4, true, 0xF4, 0x00, 0, 0, 0, 0x12, 0, 0))
            .ToArray();

        var suggestion = NmSuggestionEngine.SuggestUserData(frames, 0x18FFFAF4, true);

        Assert.Equal((byte?)2, suggestion.Offset);
        Assert.Equal(6, suggestion.Length);
        Assert.Contains("非零", suggestion.Evidence);
    }

    [Fact]
    public void SuggestNmMessageCandidatesByObservedCycle_RequiresEightBytesAndFiveIntervals()
    {
        var stable = Enumerable.Range(0, 6)
            .Select(i => Frame(i * 0.5, 0x600, false, 0, 0, 0, 0, 0, 0, 0, 0))
            .ToArray();

        var candidates = NmSuggestionEngine.SuggestNmMessageCandidatesByObservedCycle(stable);

        Assert.Contains(candidates, c => c.Contains("0x600") && c.Contains("500ms"));

        var tooFew = stable.Take(4).ToArray();
        Assert.Empty(NmSuggestionEngine.SuggestNmMessageCandidatesByObservedCycle(tooFew));

        var tooShort = Enumerable.Range(0, 6)
            .Select(i => Frame(i * 0.5, 0x601, false, 0, 0, 0, 0))
            .ToArray();
        Assert.Empty(NmSuggestionEngine.SuggestNmMessageCandidatesByObservedCycle(tooShort));

        // 扩展位必须体现在候选串里，避免标准/扩展被去重成同一条。
        var extended = Enumerable.Range(0, 6)
            .Select(i => Frame(i * 0.5, 0x600, true, 0, 0, 0, 0, 0, 0, 0, 0))
            .ToArray();
        Assert.Contains(NmSuggestionEngine.SuggestNmMessageCandidatesByObservedCycle(extended),
            c => c.Contains("0x600(ext)", StringComparison.Ordinal));
    }

    [Fact]
    public void SuggestNidByteCandidates_SeparatesStandardAndExtendedSamples()
    {
        // 同数值 ID 0x500：标准帧恒为 0xAA，扩展帧恒为 0x50。
        // 新旧口径若混样，占比会变成 10/20；按 (Id, IsExtended) 分组后各为 10/10。
        var frames = Enumerable.Range(0, 10)
            .Select(i => Frame(i * 0.5, 0x500, false, 0xAA, 0x00))
            .Concat(Enumerable.Range(0, 10)
                .Select(i => Frame(i * 0.5, 0x500, true, 0x50, 0x00)))
            .ToArray();

        var standard = NmSuggestionEngine.SuggestNidByteCandidates(
            frames, 0x500, isExtended: false, stationAddress: 0xAA);
        var extended = NmSuggestionEngine.SuggestNidByteCandidates(
            frames, 0x500, isExtended: true, stationAddress: 0x50);

        Assert.Equal((byte)0, standard[0].Byte);
        Assert.Contains("10/10", standard[0].Evidence);
        Assert.Equal((byte)0, extended[0].Byte);
        Assert.Contains("10/10", extended[0].Evidence);
    }
}
