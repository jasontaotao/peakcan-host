using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL.Odx;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Odx;

/// <summary>
/// Sprint 18 Inc 7: OdxToEcuScriptAdapter STATE-CHART integration — the adapter
/// now returns the SECURITY chart's start state via `out initialState` and applies
/// chart source/target states to matched transitions; routine responses come from
/// the ODX POS-RESPONSE chain instead of a hardcoded [0x71, 0x01].
/// </summary>
public class OdxToEcuScriptAdapterStateChartTests
{
    private static readonly string DemoCddPath = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "PeakCan.Host.Core.Tests", "Fixtures", "Odx", "Demo_Cdd.odx-d"));

    private static readonly string CompleteOdxPath = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "PeakCan.Host.Core.Tests", "Fixtures", "Odx", "complete.odx"));

    [Fact]
    public void Load_DemoCdd_ReturnsInitialStateLocked()
    {
        Assert.True(File.Exists(DemoCddPath), $"Fixture missing: {DemoCddPath}");
        var adapter = new OdxToEcuScriptAdapter();

        adapter.Load(DemoCddPath, out var initialState);

        // Demo_Cdd's SECURITY chart (id=_362) starts in "Locked".
        Assert.Equal("Locked", initialState);
    }

    [Fact]
    public void Load_CompleteOdx_ReturnsInitialStateDefault()
    {
        Assert.True(File.Exists(CompleteOdxPath), $"Fixture missing: {CompleteOdxPath}");
        var adapter = new OdxToEcuScriptAdapter();

        adapter.Load(CompleteOdxPath, out var initialState);

        // complete.odx has no STATE-CHART -> backward-compatible default.
        Assert.Equal("default", initialState);
    }

    [Fact]
    public void Load_InlineSecurityChart_TransitionFromStateLocked()
    {
        // Inline ODX with a SECURITY STATE-CHART whose start state is "Locked"
        // and whose Send_Key DIAG-SERVICE references a Locked -> UnlockedL1
        // transition. Verifies the adapter applies chart source/target states.
        // (Demo_Cdd's seed response uses DOP-REF indirection which the
        // SecurityAccessExtractor cannot resolve, so 0x27 transitions are not
        // generated there — see plan deviation note.)
        var odxXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx">
                <DIAG-LAYER-CONTAINER ID="L1">
                    <DIAG-LAYER ID="ECU_Layer" SHORT-NAME="ECU">
                        <DIAG-COMMS>
                            <DIAG-SERVICE ID="SES_SecurityAccess_Send" SHORT-NAME="SecurityAccess_Send">
                                <REQUEST-REF ID-REF="REQ_SecurityAccess_Send"/>
                                <POS-RESPONSE-REFS>
                                    <POS-RESPONSE-REF ID-REF="POS_Seed"/>
                                </POS-RESPONSE-REFS>
                                <STATE-TRANSITION-REFS>
                                    <STATE-TRANSITION-REF ID-REF="ST_LockedToUnlockedL1"/>
                                </STATE-TRANSITION-REFS>
                            </DIAG-SERVICE>
                        </DIAG-COMMS>
                        <REQUESTS>
                            <REQUEST ID="REQ_SecurityAccess_Send">
                                <PARAMS>
                                    <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>39</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE></PARAM>
                                </PARAMS>
                            </REQUEST>
                        </REQUESTS>
                        <POS-RESPONSES>
                            <POS-RESPONSE ID="POS_Seed">
                                <PARAMS>
                                    <PARAM SEMANTIC="DATA">
                                        <DIAG-CODED-TYPE><BIT-LENGTH>32</BIT-LENGTH></DIAG-CODED-TYPE>
                                    </PARAM>
                                </PARAMS>
                            </POS-RESPONSE>
                        </POS-RESPONSES>
                        <STATE-CHARTS>
                            <STATE-CHART ID="SC_Security">
                                <SHORT-NAME>SecurityAccess</SHORT-NAME>
                                <SEMANTIC>SECURITY</SEMANTIC>
                                <STATE-TRANSITIONS>
                                    <STATE-TRANSITION ID="ST_LockedToUnlockedL1">
                                        <SOURCE-SNREF SHORT-NAME="Locked" />
                                        <TARGET-SNREF SHORT-NAME="UnlockedL1" />
                                    </STATE-TRANSITION>
                                </STATE-TRANSITIONS>
                                <START-STATE-SNREF SHORT-NAME="Locked" />
                                <STATES>
                                    <STATE ID="S_Locked"><SHORT-NAME>Locked</SHORT-NAME></STATE>
                                    <STATE ID="S_UnlockedL1"><SHORT-NAME>UnlockedL1</SHORT-NAME></STATE>
                                </STATES>
                            </STATE-CHART>
                        </STATE-CHARTS>
                    </DIAG-LAYER>
                </DIAG-LAYER-CONTAINER>
            </ODX>
            """;
        var tempPath = Path.GetTempFileName() + ".odx";
        File.WriteAllText(tempPath, odxXml);

        try
        {
            var adapter = new OdxToEcuScriptAdapter();
            var transitions = adapter.Load(tempPath, out var initialState);

            Assert.Equal("Locked", initialState);

            // 0x27 0x01 (seed request) -> STATE-TRANSITION-REF ST_LockedToUnlockedL1
            // which is Locked -> UnlockedL1 in the SECURITY chart.
            var seed = transitions.Single(t => t.ServiceId == 0x27 && t.SubFunction == 0x01);
            Assert.Equal("Locked", seed.FromState);
            Assert.Equal("UnlockedL1", seed.ToState);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Load_InlineOdx_RoutineResponseComesFromOdx()
    {
        // ODX with a 0x31 routine whose POS-RESPONSE carries a DATA byte (10).
        // The adapter must use that payload, not the hardcoded [0x71, 0x01].
        var odxXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx">
                <DIAG-LAYER-CONTAINER ID="L1">
                    <DIAG-LAYER ID="ECU_Layer" SHORT-NAME="ECU">
                        <DIAG-COMMS>
                            <DIAG-SERVICE ID="SES_Routine" SHORT-NAME="Erase_Memory_Start">
                                <REQUEST-REF ID-REF="REQ_Routine"/>
                                <POS-RESPONSE-REFS>
                                    <POS-RESPONSE-REF ID-REF="PR_Routine"/>
                                </POS-RESPONSE-REFS>
                            </DIAG-SERVICE>
                        </DIAG-COMMS>
                        <REQUESTS>
                            <REQUEST ID="REQ_Routine">
                                <PARAMS>
                                    <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>49</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="ID"><CODED-VALUE>65280</CODED-VALUE></PARAM>
                                </PARAMS>
                            </REQUEST>
                        </REQUESTS>
                        <POS-RESPONSES>
                            <POS-RESPONSE ID="PR_Routine">
                                <PARAMS>
                                    <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>113</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="ID"><CODED-VALUE>65280</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="DATA"><CODED-VALUE>10</CODED-VALUE></PARAM>
                                </PARAMS>
                            </POS-RESPONSE>
                        </POS-RESPONSES>
                    </DIAG-LAYER>
                </DIAG-LAYER-CONTAINER>
            </ODX>
            """;
        var tempPath = Path.GetTempFileName() + ".odx";
        File.WriteAllText(tempPath, odxXml);

        try
        {
            var adapter = new OdxToEcuScriptAdapter();
            var transitions = adapter.Load(tempPath, out _);

            var startT = transitions.Single(t => t.ServiceId == 0x31 && t.SubFunction == 0x01);
            var staticResp = Assert.IsType<StaticResponse>(startT.Response);
            // [0x71, subFunc=0x01, ...data from ODX (0x0A)]
            Assert.Equal(new byte[] { 0x71, 0x01, 0x0A }, staticResp.Data);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
