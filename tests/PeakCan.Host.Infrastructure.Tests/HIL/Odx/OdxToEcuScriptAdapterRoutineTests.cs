using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL.Odx;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Odx;

/// <summary>
/// Sprint 9 Inc 2: Routine transition generation.
/// Verifies that the adapter generates correct RoutineControl transitions.
/// </summary>
public class OdxToEcuScriptAdapterRoutineTests
{
    private static string CreateRoutineOdx()
    {
        return """
            <?xml version="1.0" encoding="utf-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx">
                <DIAG-LAYER-CONTAINER ID="L1">
                    <DIAG-LAYER ID="ECU_Layer" SHORT-NAME="ECU">
                        <DIAG-COMMS>
                            <DIAG-SERVICE ID="SES_Routine_Start" SHORT-NAME="Erase_Memory_Start">
                                <REQUEST-REF ID-REF="REQ_Routine"/>
                            </DIAG-SERVICE>
                            <DIAG-SERVICE ID="SES_Routine_Stop" SHORT-NAME="Erase_Memory_Stop">
                                <REQUEST-REF ID-REF="REQ_Routine"/>
                            </DIAG-SERVICE>
                        </DIAG-COMMS>
                        <REQUESTS>
                            <REQUEST ID="REQ_Routine">
                                <PARAMS>
                                    <PARAM SEMANTIC="SERVICE-ID">
                                        <CODED-VALUE>49</CODED-VALUE>
                                    </PARAM>
                                    <PARAM SEMANTIC="ID">
                                        <CODED-VALUE>65280</CODED-VALUE>
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
    public void Routine_GeneratesStartStopResults_Transitions()
    {
        var odxXml = CreateRoutineOdx();
        var tempPath = Path.GetTempFileName() + ".odx";
        File.WriteAllText(tempPath, odxXml);

        try
        {
            var adapter = new OdxToEcuScriptAdapter();
            var transitions = adapter.Load(tempPath);

            // Assert: 3 routine transitions (start=0x01, stop=0x02, results=0x03)
            var routineTransitions = transitions.Where(t => t.ServiceId == 0x31).ToList();
            Assert.Equal(3, routineTransitions.Count);

            // Start (subFunc=0x01)
            var startT = routineTransitions.First(t => t.SubFunction == 0x01);
            Assert.Null(startT.FromState);
            Assert.Equal(new byte[] { 0xFF, 0xFF }, startT.DataMask);
            Assert.Equal(new byte[] { 0xFF, 0x00 }, startT.DataPattern); // routineId=0xFF00
            Assert.IsType<StaticResponse>(startT.Response);
            Assert.Equal(new byte[] { 0x71, 0x01 }, ((StaticResponse)startT.Response).Data);

            // Stop (subFunc=0x02)
            var stopT = routineTransitions.First(t => t.SubFunction == 0x02);
            Assert.Equal(new byte[] { 0x71, 0x02 }, ((StaticResponse)stopT.Response).Data);

            // RequestResults (subFunc=0x03)
            var resultsT = routineTransitions.First(t => t.SubFunction == 0x03);
            Assert.Equal(new byte[] { 0x71, 0x03 }, ((StaticResponse)resultsT.Response).Data);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Routine_DataMaskLengthIs2()
    {
        var odxXml = CreateRoutineOdx();
        var tempPath = Path.GetTempFileName() + ".odx";
        File.WriteAllText(tempPath, odxXml);

        try
        {
            var adapter = new OdxToEcuScriptAdapter();
            var transitions = adapter.Load(tempPath);

            var routineTransitions = transitions.Where(t => t.ServiceId == 0x31).ToList();
            foreach (var t in routineTransitions)
            {
                Assert.Equal(2, t.DataMask?.Length); // 2 bytes for routineId
                Assert.Equal(2, t.DataPattern?.Length);
            }
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
