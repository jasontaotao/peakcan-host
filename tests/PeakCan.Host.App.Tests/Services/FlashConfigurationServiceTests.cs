using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels.Uds.FlashPipeline;
using PeakCan.HIL.Core.Uds.Odx;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

public class FlashConfigurationServiceTests
{
    [Fact]
    public void UpdateFromOdx_SetsSecurityAccessConfig()
    {
        var svc = new FlashConfigurationService();
        var config = new SecurityAccessConfig(0x01, 16);

        svc.UpdateFromOdx(config);

        var result = svc.GetSecurityAccessConfig();
        Assert.NotNull(result);
        Assert.Equal(0x01, result!.Level);
        Assert.Equal(16, result.SeedLength);
    }

    [Fact]
    public void UpdateFromOdx_SecondCall_OverwritesFirst()
    {
        var svc = new FlashConfigurationService();
        svc.UpdateFromOdx(new SecurityAccessConfig(0x01, 16));
        svc.UpdateFromOdx(new SecurityAccessConfig(0x11, 32));

        var result = svc.GetSecurityAccessConfig();
        Assert.Equal(0x11, result!.Level);
        Assert.Equal(32, result.SeedLength);
    }

    [Fact]
    public void UpdateFromOdx_Null_ClearsConfig()
    {
        var svc = new FlashConfigurationService();
        svc.UpdateFromOdx(new SecurityAccessConfig(0x01, 16));
        svc.UpdateFromOdx(null);

        Assert.Null(svc.GetSecurityAccessConfig());
    }

    [Fact]
    public void ConfigUpdated_RaisedOnUpdate()
    {
        var svc = new FlashConfigurationService();
        int raisedCount = 0;
        svc.ConfigUpdated += () => raisedCount++;

        svc.UpdateFromOdx(new SecurityAccessConfig(0x01, 16));
        Assert.Equal(1, raisedCount);

        svc.UpdateFromOdx(new SecurityAccessConfig(0x11, 32));
        Assert.Equal(2, raisedCount);
    }

    [Fact]
    public void GetEraseRoutineId_ReturnsNull()
    {
        var svc = new FlashConfigurationService();
        Assert.Null(svc.GetEraseRoutineId(0x0000, 0x1000));
    }

    [Fact]
    public void GetChecksumAlgorithm_DefaultsCrc32()
    {
        var svc = new FlashConfigurationService();
        Assert.Equal(ChecksumAlgorithm.Crc32, svc.GetChecksumAlgorithm());
    }
}
