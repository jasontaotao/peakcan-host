using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

public class UdsClientKeyAlgorithmCtorTests
{
    private static IsoTpLayer NewIsoTp()
    {
        // IsoTpLayer ctor is (CanIdConfig config, Action<CanFrame> sendFrame).
        // The sendFrame lambda never fires in these tests because no real
        // frames are sent. The MessageReceived handler is hooked but the
        // tests do not trigger it.
        return new IsoTpLayer(
            config: new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 },
            sendFrame: _ => { });
    }

    [Fact]
    public void NewCtor_With_KeyAlgorithm_DoesNotThrow()
    {
        using var sut = new UdsClient(NewIsoTp(), new FakeKeyDerivationAlgorithm());

        Assert.NotNull(sut);
    }

    [Fact]
    public void NewCtor_With_NullIsoTp_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            new UdsClient(isoTp: null!, new FakeKeyDerivationAlgorithm());
        });
    }

    [Fact]
    public void NewCtor_With_NullKeyAlgorithm_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            new UdsClient(NewIsoTp(), keyAlgorithm: null!);
        });
    }

    [Fact]
    public void NewCtor_With_Timer_Uses_Provided_Timer()
    {
        var timer = new UdsTimer { P2Timeout = TimeSpan.FromMilliseconds(123) };

        using var sut = new UdsClient(NewIsoTp(), new FakeKeyDerivationAlgorithm(), timer);

        Assert.Equal(TimeSpan.FromMilliseconds(123), timer.P2Timeout);
    }

    [Fact]
    public void LegacyCtor_Still_Compiles_And_DoesNotThrow()
    {
        using var sut = new UdsClient(NewIsoTp());

        Assert.NotNull(sut);
    }
}
