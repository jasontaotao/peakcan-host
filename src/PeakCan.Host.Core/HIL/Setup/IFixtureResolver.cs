namespace PeakCan.Host.Core.HIL.Setup;

/// <summary>
/// Resolves fixture key to fixture instance. Decouples Engine from IServiceProvider keyed DI.
/// </summary>
public interface IFixtureResolver
{
    ITestFixture Resolve(string key);
}
