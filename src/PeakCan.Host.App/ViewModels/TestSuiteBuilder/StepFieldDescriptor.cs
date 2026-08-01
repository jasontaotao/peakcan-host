namespace PeakCan.Host.App.ViewModels.TestSuiteBuilder;

public enum StepFieldKind { Text, Number, Bool, Enum, CanId, DbcSignal, HexBytes, IntList }

/// <summary>
/// 属性面板里一个可编辑字段的描述。Key 必须与 StepParametersFactory.Create 的键一致。
/// </summary>
public sealed record StepFieldDescriptor(string Key, string Label, StepFieldKind Kind, string[]? EnumValues = null);
