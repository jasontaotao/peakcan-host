using PeakCan.HIL.Core.HIL;
using Xunit;

namespace PeakCan.HIL.Core.Tests.HIL.Parameterization;

public class TestCaseGeneratorTests
{
    private static Dictionary<string, string> Dict(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Generate_SingleParameter_ReplacesInName()
    {
        var template = new TestCaseTemplate("test", "BMS_RPM_{{rpm}}_Test",
            "Test at {{rpm}} RPM", Array.Empty<TemplateStep>(), Array.Empty<string>());

        var result = TestCaseGenerator.Generate(template, ParameterSet.Create(("rpm", 3000.0)));

        Assert.Equal("BMS_RPM_3000_Test", result.Name);
        Assert.Equal("Test at 3000 RPM", result.Description);
    }

    [Fact]
    public void Generate_MultipleParameters_ReplacesAll()
    {
        var template = new TestCaseTemplate("test", "{{prefix}}_{{rpm}}",
            "{{prefix}} test", Array.Empty<TemplateStep>(), Array.Empty<string>());

        var result = TestCaseGenerator.Generate(template,
            ParameterSet.Create(("prefix", "BMS"), ("rpm", 3000.0)));

        Assert.Equal("BMS_3000", result.Name);
    }

    [Fact]
    public void Generate_StepParameters_ConvertedToStrongType()
    {
        var template = new TestCaseTemplate("test", "Test", "Desc",
            new[]
            {
                new TemplateStep("waitForSignal", "Wait", Dict(
                    ("SignalName", "RPM"),
                    ("Expected", "{{rpm}}"),
                    ("Tolerance", "50"),
                    ("TimeoutMs", "5000"))),
            },
            Array.Empty<string>());

        var result = TestCaseGenerator.Generate(template, ParameterSet.Create(("rpm", 3000.0)));

        var param = (WaitForSignalStep)result.Steps[0].Parameters;
        Assert.Equal(3000.0, param.Expected);
        Assert.Equal(50.0, param.Tolerance);
    }

    [Fact]
    public void Generate_Label_PreservedFromTemplate()
    {
        var template = new TestCaseTemplate("test", "Test", "Desc",
            new[] { new TemplateStep("delay", "My Label", Dict(("Milliseconds", "100"))) },
            Array.Empty<string>());

        var result = TestCaseGenerator.Generate(template, ParameterSet.Empty);

        Assert.Equal("My Label", result.Steps[0].Label);
    }

    [Fact]
    public void Generate_IdCombinesBaseIdAndParameterId()
    {
        var template = new TestCaseTemplate("base", "Name", "",
            Array.Empty<TemplateStep>(), Array.Empty<string>());

        var result = TestCaseGenerator.Generate(template, ParameterSet.Create(("rpm", 3000.0)));

        Assert.StartsWith("base_", result.Id);
        Assert.Contains("rpm=3000", result.Id);
    }

    [Fact]
    public void Generate_UnresolvedParameter_KeepsPlaceholder()
    {
        var template = new TestCaseTemplate("test", "{{unknown}}", "",
            Array.Empty<TemplateStep>(), Array.Empty<string>());

        var result = TestCaseGenerator.Generate(template, ParameterSet.Empty);

        Assert.Equal("{{unknown}}", result.Name);
    }

    [Fact]
    public void Generate_TypeConversionFailure_Throws()
    {
        var template = new TestCaseTemplate("test", "Test", "",
            new[] { new TemplateStep("delay", null, Dict(("Milliseconds", "not_a_number"))) },
            Array.Empty<string>());

        Assert.ThrowsAny<Exception>(() => TestCaseGenerator.Generate(template, ParameterSet.Empty));
    }
}
