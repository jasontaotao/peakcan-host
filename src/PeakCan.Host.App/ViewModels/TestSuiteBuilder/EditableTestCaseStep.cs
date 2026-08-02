using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.App.ViewModels.TestSuiteBuilder;

/// <summary>
/// 可编辑测试步骤。Params 是 StepParametersFactory.Create 期望形态的 dict,
/// 保存时复用工厂构建强类型, 保证与 runtime 字节一致。
/// </summary>
public sealed partial class EditableTestCaseStep : ObservableObject
{
    public TestCaseStepKind Kind { get; }
    [ObservableProperty] private string? _label;
    public Dictionary<string, object> Params { get; }

    public EditableTestCaseStep(TestCaseStepKind kind, string? label, Dictionary<string, object>? paramDefaults = null)
    {
        Kind = kind;
        Label = label;
        Params = paramDefaults ?? StepFieldDescriptors.DefaultsFor(kind);
    }

    public TestCaseStep ToStep()
        => TestCaseStep.Create(StepParametersFactory.Create(Kind, Params), Label);

    /// <summary>
    /// 写参数并通知绑定刷新（Dictionary 无 INPC, 程序化改值后需手动触发
    /// Params[...] 索引器绑定重估）。
    /// </summary>
    public void SetParam(string key, object value)
    {
        Params[key] = value;
        OnPropertyChanged(nameof(Params));
    }

    public static EditableTestCaseStep New(TestCaseStepKind kind)
        => new(kind, null);

    public static EditableTestCaseStep FromStep(TestCaseStep step)
        => new(step.Kind, step.Label,
            new Dictionary<string, object>(StepParametersExporter.FromParameters(step.Parameters)));
}
