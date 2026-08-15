using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests;

public class HilRunnerServiceTests
{
    [Fact]
    public void ResolveCaseLogDirectory_UsesDefault_WhenNull()
    {
        var request = new HilRunRequest("d.dbc", "s.json");
        var dir = HilRunnerService.ResolveCaseLogDirectory(request);
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PeakCanHost", "hil-reports", "case-logs");
        Assert.Equal(expected, dir);
    }

    [Fact]
    public void ResolveCaseLogDirectory_UsesOverride_WhenSet()
    {
        var request = new HilRunRequest("d.dbc", "s.json", CaseLogDirectory: @"C:\logs");
        Assert.Equal(@"C:\logs", HilRunnerService.ResolveCaseLogDirectory(request));
    }
}
