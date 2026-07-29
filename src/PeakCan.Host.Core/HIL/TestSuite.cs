namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Pure data model for a test suite.
/// Stores case list, fixture keys, and execution config.
/// </summary>
public sealed record TestSuite(
    string Name,
    IReadOnlyList<TestCase> Cases,
    IReadOnlyList<string> GlobalCaseFixtureKeys,
    IReadOnlyList<string> SuiteFixtureKeys,
    TestSuiteConfig Config,
    int TimeoutMs = 0);
