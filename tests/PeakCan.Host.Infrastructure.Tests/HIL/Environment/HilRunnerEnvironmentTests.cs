using Xunit;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class HilRunnerEnvironmentTests
{
    [Fact]
    public void SuiteWithValidEnvironment_PassesValidation()
    {
        var nodes = new List<RestbusNode>
        {
            new() { Name = "A", Identity = new RawCanNodeIdentity() },
        };
        var errors = RestbusNodeValidator.Validate(nodes, null, null);
        Assert.Empty(errors);
    }

    [Fact]
    public void SuiteWithInvalidEnvironment_FailsValidation()
    {
        var nodes = new List<RestbusNode>
        {
            new() { Name = "A", Identity = new RawCanNodeIdentity(), Channel = "GHOST" },
        };
        var channels = new List<ChannelConfig> { new("CAN1", "51", null, false) };
        var errors = RestbusNodeValidator.Validate(nodes, channels, null);
        Assert.NotEmpty(errors);
    }
}
