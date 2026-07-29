using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Setup;

namespace PeakCan.Host.Core.Tests.HIL.Fakes;

/// <summary>
/// Test fixture resolver. Returns FakeFixture for any key.
/// </summary>
internal sealed class FakeFixtureResolver : IFixtureResolver
{
    private readonly Dictionary<string, ITestFixture> _fixtures = new();

    public void Register(string key, ITestFixture fixture) => _fixtures[key] = fixture;

    public ITestFixture Resolve(string key) =>
        _fixtures.TryGetValue(key, out var f) ? f
        : throw new KeyNotFoundException($"Fixture '{key}' not in fake");
}
