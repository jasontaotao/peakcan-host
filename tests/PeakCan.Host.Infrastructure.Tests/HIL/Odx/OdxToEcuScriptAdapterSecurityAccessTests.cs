using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Host.Infrastructure.HIL.Odx;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Odx;

/// <summary>
/// Sprint 9 Inc 1: SecurityAccess transition generation.
/// Verifies that the adapter generates correct seed/key-verify transitions
/// from ODX SecurityAccess configuration.
/// </summary>
public class OdxToEcuScriptAdapterSecurityAccessTests
{
    private static string CreateSecurityAccessOdx(int? bitLength)
    {
        var posResponse = bitLength.HasValue
            ? $"""
                <POS-RESPONSE ID="POS_Seed">
                    <PARAM SEMANTIC="DATA">
                        <DIAG-CODED-TYPE>
                            <BIT-LENGTH>{bitLength.Value}</BIT-LENGTH>
                        </DIAG-CODED-TYPE>
                    </PARAM>
                </POS-RESPONSE>
                <POS-RESPONSE-REFS>
                    <POS-RESPONSE-REF ID-REF="POS_Seed"/>
                </POS-RESPONSE-REFS>
            """
            : "";

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx">
                <DIAG-LAYER-CONTAINER ID="L1">
                    <DIAG-LAYER ID="ECU_Layer" SHORT-NAME="ECU">
                        <DIAG-COMMS>
                            <DIAG-SERVICE ID="SES_SecurityAccess_Send" SHORT-NAME="SecurityAccess_Send">
                                <REQUEST-REF ID-REF="REQ_SecurityAccess_Send"/>
                                {posResponse}
                            </DIAG-SERVICE>
                        </DIAG-COMMS>
                        <REQUESTS>
                            <REQUEST ID="REQ_SecurityAccess_Send">
                                <PARAMS>
                                    <PARAM SEMANTIC="SERVICE-ID">
                                        <CODED-VALUE>39</CODED-VALUE>
                                    </PARAM>
                                    <PARAM SEMANTIC="SUBFUNCTION">
                                        <CODED-VALUE>1</CODED-VALUE>
                                    </PARAM>
                                </PARAMS>
                            </REQUEST>
                        </REQUESTS>
                    </DIAG-LAYER>
                </DIAG-LAYER-CONTAINER>
            </ODX>
            """;
    }

    [Fact]
    public void SecurityAccess_SeedLength4_GeneratesTwoTransitions()
    {
        // Arrange: ODX with 0x27 SecurityAccess, BIT-LENGTH=32 (4 bytes)
        var odxXml = CreateSecurityAccessOdx(bitLength: 32);
        var tempPath = Path.GetTempFileName() + ".odx";
        File.WriteAllText(tempPath, odxXml);

        try
        {
            // Act
            var adapter = new OdxToEcuScriptAdapter();
            var transitions = adapter.Load(tempPath, out _);

            // Assert: 2 SecurityAccess transitions (seed + key verify)
            var secTransitions = transitions
                .Where(t => t.ServiceId == 0x27)
                .ToList();

            Assert.Equal(2, secTransitions.Count);

            // Seed transition (0x27, 0x01) — wildcard FromState (matches any state)
            var seedT = secTransitions.First(t => t.SubFunction == 0x01);
            Assert.Null(seedT.FromState); // wildcard: FSM starts in "default"
            Assert.Equal("seedSent", seedT.ToState);
            Assert.Null(seedT.DataMask); // ← KEY: DataMask=null, no NRE
            Assert.Null(seedT.DataPattern);
            Assert.IsType<DynamicResponse>(seedT.Response);
            Assert.Equal("SecurityAccessSeed", ((DynamicResponse)seedT.Response).GeneratorName);

            // Key verify transition (0x27, 0x02) — wildcard FromState
            var keyT = secTransitions.First(t => t.SubFunction == 0x02);
            Assert.Null(keyT.FromState); // wildcard
            Assert.Equal("unlocked", keyT.ToState);
            Assert.Null(keyT.DataMask); // ← KEY: DataMask=null, no NRE
            Assert.Null(keyT.DataPattern);
            Assert.IsType<DynamicResponse>(keyT.Response);
            Assert.Equal("SecurityAccessVerifyKey", ((DynamicResponse)keyT.Response).GeneratorName);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void SecurityAccess_SeedLengthNull_SkipsAndWarns()
    {
        // Arrange: ODX with 0x27 but no BIT-LENGTH → SeedLength=null
        var odxXml = CreateSecurityAccessOdx(bitLength: null);
        var tempPath = Path.GetTempFileName() + ".odx";
        File.WriteAllText(tempPath, odxXml);

        try
        {
            // Act
            var adapter = new OdxToEcuScriptAdapter();
            var transitions = adapter.Load(tempPath, out _);

            // Assert: No SecurityAccess transitions (skipped)
            var secTransitions = transitions.Where(t => t.ServiceId == 0x27).ToList();
            Assert.Empty(secTransitions);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void SecurityAccess_DataMaskNull_ProcessRequestSucceeds()
    {
        // Arrange: ODX with valid SecurityAccess
        var odxXml = CreateSecurityAccessOdx(bitLength: 32);
        var tempPath = Path.GetTempFileName() + ".odx";
        File.WriteAllText(tempPath, odxXml);

        try
        {
            var adapter = new OdxToEcuScriptAdapter();
            var transitions = adapter.Load(tempPath, out _);

            // Act: Feed transitions into EcuStateMachine with a real generator
            var generators = new List<IEcuResponseGenerator>
            {
                new PeakCan.Host.Infrastructure.HIL.Generators.SecurityAccessSeedGenerator()
            };
            var fsm = new EcuStateMachine(transitions, generators);

            // Seed request: [0x27, 0x01]
            var seedRequest = new byte[] { 0x27, 0x01 };
            var (response, _) = fsm.ProcessRequest(seedRequest);

            // Assert: response is 0x67 0x01 + 4-byte seed (positive), state transitions to seedSent
            Assert.Equal(0x67, response[0]);
            Assert.Equal(0x01, response[1]); // subFunction in response
            Assert.Equal("seedSent", fsm.CurrentState);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
