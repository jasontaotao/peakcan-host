namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// Sink for script output lines. Decouples <see cref="ScriptUtilities"/>
/// from the full <see cref="ScriptEngine"/> so the two no longer form a
/// constructor cycle — the engine is one implementation, tests substitute
/// a fake.
/// </summary>
public interface IScriptOutputSink
{
    void EmitOutput(ScriptOutputLine line);
}
