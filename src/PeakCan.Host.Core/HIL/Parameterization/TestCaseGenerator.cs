using System.Globalization;
using System.Text.RegularExpressions;

namespace PeakCan.HIL.Core.HIL;

/// <summary>
/// Expands a TestCaseTemplate with parameter values into a concrete TestCase.
/// </summary>
public static class TestCaseGenerator
{
    public static TestCase Generate(TestCaseTemplate template, ParameterSet parameters)
    {
        var resolvedSteps = template.Steps.Select(step =>
        {
            var resolvedParams = new Dictionary<string, object>();
            foreach (var kv in step.Parameters)
                resolvedParams[kv.Key] = Resolve(kv.Value, parameters);

            var kind = Enum.Parse<TestCaseStepKind>(step.Kind, ignoreCase: true);
            var stepParams = StepParametersFactory.Create(kind, resolvedParams);
            return TestCaseStep.Create(stepParams, step.Label);
        }).ToList();

        return new TestCase(
            Id: $"{template.BaseId}_{ComputeParameterId(parameters)}",
            Name: Resolve(template.NameTemplate, parameters),
            Description: Resolve(template.DescriptionTemplate, parameters),
            PreConditions: null,
            Steps: resolvedSteps,
            PostConditions: null,
            Tags: template.Tags,
            TimeoutMs: 0);
    }

    private static string Resolve(string template, ParameterSet parameters)
        => Regex.Replace(template, @"\{\{(\w+)\}\}", m =>
            parameters.Values.TryGetValue(m.Groups[1].Value, out var v)
                ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? m.Value
                : m.Value);

    private static string ComputeParameterId(ParameterSet parameters)
        => string.Join("_", parameters.Values.Select(kv => $"{kv.Key}={kv.Value}"));
}
