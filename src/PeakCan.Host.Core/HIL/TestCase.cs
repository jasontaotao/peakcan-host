namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Pure data model for a single HIL test case.
/// Serializable to JSON/YAML for persistence.
/// </summary>
public sealed record TestCase(
    string Id,
    string Name,
    string Description,
    string? PreConditions,
    IReadOnlyList<TestCaseStep> Steps,
    string? PostConditions,
    IReadOnlyList<string> Tags,
    int TimeoutMs = 0,
    IReadOnlyList<string>? CaseFixtureKeys = null);
