using PeakCan.HIL.Core.HIL;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests;

public class HilRunnerServiceTests
{
    [Fact]
    public void ResolveEnvironmentLogger_Uses_Registered_GenericLogger()
    {
        var expected = NullLogger<EnvironmentRuntime>.Instance;
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<EnvironmentRuntime>>(sp => expected);

        var actual = HilRunnerService.ResolveEnvironmentLogger(services.BuildServiceProvider());

        Assert.Same(expected, actual);
    }

    [Fact]
    public void ResolveEnvironmentLogger_FallsBackToNullLogger()
    {
        var actual = HilRunnerService.ResolveEnvironmentLogger(new ServiceCollection().BuildServiceProvider());

        Assert.Same(NullLogger<EnvironmentRuntime>.Instance, actual);
    }
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

