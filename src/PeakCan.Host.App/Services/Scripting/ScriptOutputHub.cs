namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// P1-2（2026-09-06，Lazy&lt;T&gt; 清零）：脚本输出中枢。输出流改为单向
/// <c>ScriptUtilities → hub → 订阅者</c>：ScriptEngine 在 ctor 订阅 hub 并转发到自己的
/// <see cref="ScriptEngine.OutputReceived"/>，外部观察者契约不变。取代旧设计
/// "ScriptEngine 实现 <see cref="IScriptOutputSink"/>"，它造成
/// <c>ScriptUtilities → IScriptOutputSink(=ScriptEngine) → ScriptUtilities</c>
/// 的 DI 解析环（旧靠 ctor 注入 Lazy&lt;ScriptUtilities&gt; 破环）。
/// </summary>
public sealed class ScriptOutputHub : IScriptOutputSink
{
    /// <summary>Raised for every emitted output line (utilities + engine internal).</summary>
    public event Action<ScriptOutputLine>? OutputReceived;

    /// <inheritdoc/>
    public void EmitOutput(ScriptOutputLine line) => OutputReceived?.Invoke(line);
}
