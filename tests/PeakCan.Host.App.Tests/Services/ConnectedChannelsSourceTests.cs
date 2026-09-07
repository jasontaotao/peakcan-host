using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.HIL.Core;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

public sealed class ConnectedChannelsSourceTests
{
    [Fact]
    public void Publish_RaisesChanged_WithSnapshot()
    {
        var source = new ConnectedChannelsSource();
        var raised = false;
        source.Changed += () => raised = true;

        source.Publish(new[] { new HilViewModel.ConnectedChannel(0x51, BaudRate.CanFd1Mbps, true, "USB1", Substitute.For<ICanChannel>()) });

        Assert.True(raised);
        Assert.Single(source.Current);
    }

    [Fact]
    public void Publish_NullSnapshot_Throws()
    {
        var source = new ConnectedChannelsSource();
        Assert.Throws<ArgumentNullException>(() => source.Publish(null!));
    }
}
