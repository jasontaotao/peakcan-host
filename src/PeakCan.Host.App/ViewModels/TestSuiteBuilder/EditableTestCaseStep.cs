using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.App.ViewModels.TestSuiteBuilder;

/// <summary>
/// 可编辑测试步骤。Params 是 StepParametersFactory.Create 期望形态的 dict,
/// 保存时复用工厂构建强类型, 保证与 runtime 字节一致。
/// </summary>
public sealed class EditableTestCaseStep
{
    public TestCaseStepKind Kind { get; }
    public string? Label { get; set; }
    public Dictionary<string, object> Params { get; }

    public EditableTestCaseStep(TestCaseStepKind kind, string? label, Dictionary<string, object>? paramDefaults = null)
    {
        Kind = kind;
        Label = label;
        Params = paramDefaults ?? StepFieldDescriptors.DefaultsFor(kind);
    }

    public TestCaseStep ToStep()
        => TestCaseStep.Create(StepParametersFactory.Create(Kind, Params), Label);

    public static EditableTestCaseStep New(TestCaseStepKind kind)
        => new(kind, null);

    public static EditableTestCaseStep FromStep(TestCaseStep step)
        => new(step.Kind, step.Label,
            new Dictionary<string, object>(StepParametersExporter.FromParameters(step.Parameters)));
}
