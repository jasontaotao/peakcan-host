using Microsoft.ClearScript;

namespace PeakCan.Host.App.Services.Scripting;

public sealed partial class ScriptEngine
{
    // Flow C: ScriptHelpers (v1.7.3 PATCH Item 1 + earlier).
    // EmitOutput + IsResourceLimit extracted. Both are stateless helpers
    // called only from ExecuteScript (Flow A). Dispose stays in main with
    // field ownership per W14 D2.

    /// <summary>
    /// Emit an output line to subscribers. P1-2（2026-09-06）：本类不再是
    /// IScriptOutputSink 实现（由 ScriptOutputHub 承担），此方法保留为本类
    /// 内部错误输出的直达通道。
    /// </summary>
    public void EmitOutput(ScriptOutputLine line)
    {
        OutputReceived?.Invoke(line);
    }

    /// <summary>
    /// v1.7.3 PATCH Item 1: heuristic discrimination of V8 resource-cap
    /// violations from generic runtime errors. ClearScript 7.4.5 has no
    /// first-class <c>V8RuntimeViolationException</c> type (that name is
    /// 7.5+); all V8 script errors surface as
    /// <see cref="ScriptEngineException"/>. The filter matches broad V8
    /// resource-violation keywords (heap, allocation, limit, memory) — if
    /// ClearScript or V8 changes the message text, this helper is the
    /// single tuning point.
    /// </summary>
    private static bool IsResourceLimit(ScriptEngineException ex)
    {
        var msg = ex.Message;
        return msg.Contains("heap", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("allocation", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("limit", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("memory", StringComparison.OrdinalIgnoreCase);
    }
}
