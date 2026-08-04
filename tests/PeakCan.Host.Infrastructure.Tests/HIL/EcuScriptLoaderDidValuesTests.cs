using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

/// <summary>
/// Sprint 10 Inc 6: didValues injection into context.
/// </summary>
public class EcuScriptLoaderDidValuesTests
{
    [Fact]
    public void EcuScriptLoader_DidValues_InjectsIntoContext()
    {
        var json = """
        {
            "name": "DidEcu",
            "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
            "didValues": { "0xF190": [65, 66, 67] },
            "states": [
                {
                    "name": "default",
                    "transitions": [
                        { "serviceId": "0x22", "response": { "$type": "dynamic", "generatorName": "DidReadout" } }
                    ]
                }
            ]
        }
        """;

        var script = EcuScriptLoader.Parse(json);
        Assert.True(script.StateMachine.Context.HasKey("DidValues"));

        var didValues = script.StateMachine.Context.Get<Dictionary<ushort, byte[]>>("DidValues");
        Assert.NotNull(didValues);
        Assert.Equal(new byte[] { 65, 66, 67 }, didValues![0xF190]);
    }
}
