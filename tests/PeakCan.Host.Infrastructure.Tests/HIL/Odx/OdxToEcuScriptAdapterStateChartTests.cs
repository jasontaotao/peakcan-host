using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
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
            // [0x71, subFunc=0x01, idHi=0xFF, idLo=0x00 (routine 65280), ...data 0x0A]
            // Code-review M4: ISO 14229-1 routine response echoes the 2-byte ID.
            Assert.Equal(new byte[] { 0x71, 0x01, 0xFF, 0x00, 0x0A }, staticResp.Data);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Build an inline ODX with a SECURITY STATE-CHART mirroring Demo_Cdd's layout:
    /// seed (0x27 0x01) has NO STATE-TRANSITION-REF; Send_Key (0x27 0x02) has three
    /// refs (Locked→UnlockedL1, UnlockedL1→UnlockedL1, Unlocked_L2→UnlockedL1).
    /// </summary>
    private static string CreateSecurityChartOdx()
    {
        return """
            <?xml version="1.0" encoding="utf-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx">
                <DIAG-LAYER-CONTAINER ID="L1">
                    <DIAG-LAYER ID="ECU_Layer" SHORT-NAME="ECU">
                        <DIAG-COMMS>
                            <DIAG-SERVICE ID="SES_RequestSeed" SHORT-NAME="Request_Seed">
                                <REQUEST-REF ID-REF="REQ_Seed"/>
                                <POS-RESPONSE-REFS>
                                    <POS-RESPONSE-REF ID-REF="POS_Seed"/>
                                </POS-RESPONSE-REFS>
                            </DIAG-SERVICE>
                            <DIAG-SERVICE ID="SES_SendKey" SHORT-NAME="Send_Key">
                                <REQUEST-REF ID-REF="REQ_SendKey"/>
                                <POS-RESPONSE-REFS>
                                    <POS-RESPONSE-REF ID-REF="POS_Seed"/>
                                </POS-RESPONSE-REFS>
                                <STATE-TRANSITION-REFS>
                                    <STATE-TRANSITION-REF ID-REF="ST_A"/>
                                    <STATE-TRANSITION-REF ID-REF="ST_B"/>
                                    <STATE-TRANSITION-REF ID-REF="ST_C"/>
                                </STATE-TRANSITION-REFS>
                            </DIAG-SERVICE>
                        </DIAG-COMMS>
                        <REQUESTS>
                            <REQUEST ID="REQ_Seed">
                                <PARAMS>
                                    <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>39</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE></PARAM>
                                </PARAMS>
                            </REQUEST>
                            <REQUEST ID="REQ_SendKey">
                                <PARAMS>
                                    <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>39</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>2</CODED-VALUE></PARAM>
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
                                    <STATE-TRANSITION ID="ST_A">
                                        <SOURCE-SNREF SHORT-NAME="Locked" />
                                        <TARGET-SNREF SHORT-NAME="UnlockedL1" />
                                    </STATE-TRANSITION>
                                    <STATE-TRANSITION ID="ST_B">
                                        <SOURCE-SNREF SHORT-NAME="UnlockedL1" />
                                        <TARGET-SNREF SHORT-NAME="UnlockedL1" />
                                    </STATE-TRANSITION>
                                    <STATE-TRANSITION ID="ST_C">
                                        <SOURCE-SNREF SHORT-NAME="Unlocked_L2" />
                                        <TARGET-SNREF SHORT-NAME="UnlockedL1" />
                                    </STATE-TRANSITION>
                                </STATE-TRANSITIONS>
                                <START-STATE-SNREF SHORT-NAME="Locked" />
                                <STATES>
                                    <STATE ID="S_Locked"><SHORT-NAME>Locked</SHORT-NAME></STATE>
                                    <STATE ID="S_UnlockedL1"><SHORT-NAME>UnlockedL1</SHORT-NAME></STATE>
                                    <STATE ID="S_UnlockedL2"><SHORT-NAME>Unlocked_L2</SHORT-NAME></STATE>
                                </STATES>
                            </STATE-CHART>
                        </STATE-CHARTS>
                    </DIAG-LAYER>
                </DIAG-LAYER-CONTAINER>
            </ODX>
            """;
    }

    [Fact]
    public void Load_InlineSecurityChart_SeedThenKeyVerify_StateChainWorks()
    {
        // Code-review M1: seed must NOT transition to the legacy "seedSent" state
        // (chart states are Locked/UnlockedL1/Unlocked_L2), and Send_Key's three
        // STATE-TRANSITION-REFs must each generate a transition so key-verify
        // matches from any chart state.
        var tempPath = Path.GetTempFileName() + ".odx";
        File.WriteAllText(tempPath, CreateSecurityChartOdx());

        try
        {
            var adapter = new OdxToEcuScriptAdapter();
            var transitions = adapter.Load(tempPath, out var initialState);
            var sm = new EcuStateMachine(transitions, initialState: initialState);

            // Initial state comes from the SECURITY chart START-STATE-SNREF.
            Assert.Equal("Locked", sm.CurrentState);

            // Seed request: no chart transition-ref -> state stays in Locked.
            sm.ProcessRequest(new byte[] { 0x27, 0x01 });
            Assert.Equal("Locked", sm.CurrentState);

            // Key verify: Locked -> UnlockedL1 (chart ref ST_A).
            sm.ProcessRequest(new byte[] { 0x27, 0x02 });
            Assert.Equal("UnlockedL1", sm.CurrentState);

            // Re-key from UnlockedL1: chart ref ST_B is UnlockedL1 -> UnlockedL1.
            sm.ProcessRequest(new byte[] { 0x27, 0x02 });
            Assert.Equal("UnlockedL1", sm.CurrentState);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Load_InlineSecurityChart_MultipleRefs_GeneratesOneTransitionPerRef()
    {
        // Code-review M1: a DIAG-SERVICE with multiple STATE-TRANSITION-REFs
        // must generate one transition per ref (each distinct FromState), not
        // silently keep only the first.
        var tempPath = Path.GetTempFileName() + ".odx";
        File.WriteAllText(tempPath, CreateSecurityChartOdx());

        try
        {
            var adapter = new OdxToEcuScriptAdapter();
            var transitions = adapter.Load(tempPath, out _);

            // Three Send_Key transitions, one per STATE-TRANSITION-REF.
            var keyVerify = transitions.Where(t => t.ServiceId == 0x27 && t.SubFunction == 0x02).ToList();
            Assert.Equal(3, keyVerify.Count);
            Assert.Contains(keyVerify, t => t.FromState == "Locked" && t.ToState == "UnlockedL1");
            Assert.Contains(keyVerify, t => t.FromState == "UnlockedL1" && t.ToState == "UnlockedL1");
            Assert.Contains(keyVerify, t => t.FromState == "Unlocked_L2" && t.ToState == "UnlockedL1");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Load_InlineOdx_RoutineStartStop_ResponsesCarryOwnSubFunction()
    {
        // Regression (code-review H1): a routine with distinct Start (sub=1) and
        // Stop (sub=2) DIAG-SERVICEs must echo each transition's own subfunction
        // byte in its response, not share one byte[] keyed only by routine id.
        var odxXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx">
                <DIAG-LAYER-CONTAINER ID="L1">
                    <DIAG-LAYER ID="ECU_Layer" SHORT-NAME="ECU">
                        <DIAG-COMMS>
                            <DIAG-SERVICE ID="SES_Start" SHORT-NAME="Erase_Memory_Start">
                                <REQUEST-REF ID-REF="REQ_Start"/>
                                <POS-RESPONSE-REFS>
                                    <POS-RESPONSE-REF ID-REF="PR_Start"/>
                                </POS-RESPONSE-REFS>
                            </DIAG-SERVICE>
                            <DIAG-SERVICE ID="SES_Stop" SHORT-NAME="Erase_Memory_Stop">
                                <REQUEST-REF ID-REF="REQ_Stop"/>
                                <POS-RESPONSE-REFS>
                                    <POS-RESPONSE-REF ID-REF="PR_Stop"/>
                                </POS-RESPONSE-REFS>
                            </DIAG-SERVICE>
                        </DIAG-COMMS>
                        <REQUESTS>
                            <REQUEST ID="REQ_Start">
                                <PARAMS>
                                    <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>49</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="ID"><CODED-VALUE>65280</CODED-VALUE></PARAM>
                                </PARAMS>
                            </REQUEST>
                            <REQUEST ID="REQ_Stop">
                                <PARAMS>
                                    <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>49</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>2</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="ID"><CODED-VALUE>65280</CODED-VALUE></PARAM>
                                </PARAMS>
                            </REQUEST>
                        </REQUESTS>
                        <POS-RESPONSES>
                            <POS-RESPONSE ID="PR_Start">
                                <PARAMS>
                                    <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>113</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="ID"><CODED-VALUE>65280</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="DATA"><CODED-VALUE>10</CODED-VALUE></PARAM>
                                </PARAMS>
                            </POS-RESPONSE>
                            <POS-RESPONSE ID="PR_Stop">
                                <PARAMS>
                                    <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>113</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>2</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="ID"><CODED-VALUE>65280</CODED-VALUE></PARAM>
                                    <PARAM SEMANTIC="DATA"><CODED-VALUE>20</CODED-VALUE></PARAM>
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
            var stopT = transitions.Single(t => t.ServiceId == 0x31 && t.SubFunction == 0x02);
            // Routine id 65280 = 0xFF00 echoed after [0x71, sub] (code-review M4).
            Assert.Equal(new byte[] { 0x71, 0x01, 0xFF, 0x00, 0x0A }, Assert.IsType<StaticResponse>(startT.Response).Data);
            Assert.Equal(new byte[] { 0x71, 0x02, 0xFF, 0x00, 0x14 }, Assert.IsType<StaticResponse>(stopT.Response).Data);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
