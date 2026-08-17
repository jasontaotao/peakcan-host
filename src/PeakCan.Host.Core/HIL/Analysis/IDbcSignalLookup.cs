namespace PeakCan.HIL.Core.HIL.Analysis;

/// <summary>
/// DBC 信号查找接口（规则 ③）。host 若无 DBC 加载机制，注入 null 跳过 ③。
/// signal.* 引用不在已加载 DBC → Critical；DBC 未加载时跳过（不报）。
/// </summary>
public interface IDbcSignalLookup
{
    /// <summary>DBC 是否已加载（未加载时规则 ③ 跳过）。</summary>
    bool IsLoaded { get; }

    /// <summary>信号是否存在于已加载 DBC。</summary>
    /// <param name="signalPath">信号路径（如 "Msg.Sig"，来自 SourceRef.Path）。</param>
    bool ContainsSignal(string signalPath);
}
