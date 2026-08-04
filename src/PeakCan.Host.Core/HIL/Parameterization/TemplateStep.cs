namespace PeakCan.HIL.Core.HIL;

/// <summary>
/// A template step with string fields for parameter substitution.
/// Parameters use Dictionary for easy initialization in tests and JSON deserialization.
/// </summary>
public sealed record TemplateStep(
    string Kind,
    string? Label,
    Dictionary<string, string> Parameters);
