using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class SignalOverridesTests
{
    [Fact]
    public void Start_SignalOverrides_AppliedToSignalState()
    {
        var node = new RestbusNode
        {
            Name = "Charger",
            Identity = new RawCanNodeIdentity(),
            Messages = [new NodeMessage(new CanMessageRef(512, false), 100, new DbcSignalsSource("CRM"))],
            SignalOverrides = new Dictionary<string, double> { ["CRM.CRM_Signal"] = 42 }
        };
        var dbc = DbcSignalsEncodingTests.CreateTestDbc();
        var runtime = new EnvironmentRuntime(new FakeChannel(), NullLogger<EnvironmentRuntime>.Instance, dbc);
        runtime.Start([node], null);
        var payload = runtime.GetEncodedPayload("Charger", "CRM");
        Assert.NotNull(payload);
        Assert.Equal(42, payload[0]);
        runtime.Stop();
    }
}