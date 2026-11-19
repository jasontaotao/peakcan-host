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
        Features: withLayout ? new NmFeatures(PnEnabled: false) : null);

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

        var suggestion = NmSuggestionEngine.SuggestNidByte(frames, 0x18FFFAF4, stationAddress: 0xF4);

        Assert.Equal((byte?)0, suggestion.Byte);
        Assert.Contains("0xF4", suggestion.Evidence);
    }

    [Fact]
    public void SuggestCycleMs_UsesMedianAndRequiresFiveSamples()
    {
        var frames = Enumerable.Range(0, 6)
            .Select(i => Frame(i * 0.5, 0x18FFFAF4, true, 0xF4))
            .ToArray();

        Assert.Equal(500, NmSuggestionEngine.SuggestCycleMs(frames, 0x18FFFAF4));

        var tooFew = frames.Take(4).ToArray();
        Assert.Null(NmSuggestionEngine.SuggestCycleMs(tooFew, 0x18FFFAF4));
    }
}
