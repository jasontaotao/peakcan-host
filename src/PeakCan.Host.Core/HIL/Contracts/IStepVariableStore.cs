namespace PeakCan.HIL.Core.HIL.Contracts;

/// <summary>
/// 步骤间数据传递。Step executor 可写入读取结果，后续步骤引用。
/// 键名约定: "did_0xF190", "session", "seed_0x01" 等。
/// 独立于 <see cref="IAssertionContext"/>：只有需要步骤间传值的 executor 才探测此接口。
/// </summary>
public interface IStepVariableStore
{
    IDictionary<string, object> Variables { get; }
}
