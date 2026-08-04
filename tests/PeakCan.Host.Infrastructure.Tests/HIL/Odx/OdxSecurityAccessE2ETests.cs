using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Host.Infrastructure.HIL.Generators;
using PeakCan.Host.Infrastructure.HIL.Odx;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Odx;

/// <summary>
/// M4 fix verification: ODX-imported SecurityAccess flow works end-to-end.
/// Verifies that wildcard (FromState=null) transitions are correctly preserved
/// through the ODX → JSON → EcuStateMachine pipeline.
/// </summary>
public class OdxSecurityAccessE2ETests
{
    private static string CreateSecurityAccessOdx()
    {
        return """
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
                  <POS-RESPONSES>
                    <POS-RESPONSE ID="POS_Seed">
                      <PARAM SEMANTIC="DATA">
                        <DIAG-CODED-TYPE>
                          <BIT-LENGTH>32</BIT-LENGTH>
                        </DIAG-CODED-TYPE>
                      </PARAM>
                    </POS-RESPONSE>
                  </POS-RESPONSES>
                </DIAG-LAYER>
              </DIAG-LAYER-CONTAINER>
            </ODX>
            """;
    }

    [Fact]
    public void M4_WildcardState_ParsedAsNullFromState()
    {
        // Arrange: ODX with SecurityAccess
        var tempPath = Path.GetTempFileName() + ".odx";
        File.WriteAllText(tempPath, CreateSecurityAccessOdx());

        try
        {
            // Act: Import to JSON then parse
            var json = OdxEcuScriptImporter.ImportToJson(tempPath, "TestECU", 0x7E0, 0x7E8);
            var script = EcuScriptLoader.Parse(json);

            // Assert: SecurityAccess transitions should be matchable from "default" state
            // (FromState=null means wildcard — matches any state)
            var generators = new List<IEcuResponseGenerator>
            {
                new SecurityAccessSeedGenerator(),
                new SecurityAccessVerifyKeyGenerator()
            };
            var fsm = new EcuStateMachine(script.StateMachine.CollectAllTransitions(), generators);

            // Step 1: Seed request from "default" state (FSM starts here)
            var seedRequest = new byte[] { 0x27, 0x01 };
            var (seedResponse, _) = fsm.ProcessRequest(seedRequest);

            // Should get positive response [0x67, 0x01, seed[0..3]]
            Assert.Equal(0x67, seedResponse[0]);
            Assert.Equal(0x01, seedResponse[1]);
            Assert.True(seedResponse.Length >= 6, $"Expected seed response with 4-byte seed, got {seedResponse.Length} bytes");

            // Step 2: Key verify from "seedSent" state (FSM transitioned here)
            // The key verify transition has FromState=null (wildcard), so it should match
            // even though FSM is now in "seedSent" state
            var seed = seedResponse[2..6];
            var key = seed.Select(b => (byte)(b ^ 0xAA)).ToArray(); // XOR 0xAA algorithm
            var keyRequest = new byte[] { 0x27, 0x02 }.Concat(key).ToArray();
            var (keyResponse, _) = fsm.ProcessRequest(keyRequest);

            // Should get positive response [0x67, 0x02]
            Assert.Equal(0x67, keyResponse[0]);
            Assert.Equal(0x02, keyResponse[1]);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}

/// <summary>
/// Helper to collect all transitions from an EcuStateMachine for testing.
/// </summary>
internal static class StateMachineExtensions
{
    public static IReadOnlyList<EcuStateTransition> CollectAllTransitions(this EcuStateMachine sm)
    {
        // Use reflection to access private _transitions field
        var field = typeof(EcuStateMachine).GetField("_transitions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (IReadOnlyList<EcuStateTransition>)(field?.GetValue(sm) ?? Array.Empty<EcuStateTransition>());
    }
}
