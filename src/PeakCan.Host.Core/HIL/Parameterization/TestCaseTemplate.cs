namespace PeakCan.Host.Core.HIL;

/// <summary>
/// A parameterized test case template. Expanded into concrete TestCase via TestCaseGenerator.
/// </summary>
public sealed record TestCaseTemplate(
    string BaseId,
    string NameTemplate,
    string DescriptionTemplate,
    IReadOnlyList<TemplateStep> Steps,
    IReadOnlyList<string> Tags);
