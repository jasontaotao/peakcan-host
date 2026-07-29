using System.Text.Json.Serialization;

namespace PeakCan.Host.Core.HIL;

/// <summary>
/// A single test step. Pure data with validated factory.
/// Private constructor + Create() guarantees Kind == Parameters.Kind.
/// </summary>
[JsonConverter(typeof(TestCaseStepJsonConverter))]
public sealed record TestCaseStep
{
    public TestCaseStepKind Kind { get; }
    public string? Label { get; }
    public StepParameters Parameters { get; }

    private TestCaseStep(TestCaseStepKind kind, string? label, StepParameters parameters)
    {
        Kind = kind;
        Label = label;
        Parameters = parameters;
    }

    /// <summary>
    /// Factory method. Derives Kind from parameters.Kind, guaranteeing consistency.
    /// </summary>
    public static TestCaseStep Create(StepParameters parameters, string? label = null)
        => new(parameters.Kind, label, parameters);
}
