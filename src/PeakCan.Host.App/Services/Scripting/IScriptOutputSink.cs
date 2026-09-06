namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// Sink for script output lines. P1-2（2026-09-06，Lazy&lt;T&gt; 清零）：唯一生产实现是
/// <see cref="ScriptOutputHub"/>（<see cref="ScriptEngine"/> 订阅 hub 并转发到
/// <see cref="ScriptEngine.OutputReceived"/>）；tests substitute a fake.
/// </summary>
public interface IScriptOutputSink
{
    void EmitOutput(ScriptOutputLine line);
}
