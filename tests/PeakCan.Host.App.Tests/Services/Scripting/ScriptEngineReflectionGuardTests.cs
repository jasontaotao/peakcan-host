using FluentAssertions;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.Core;
using Xunit;
using Xunit.Abstractions;

namespace PeakCan.Host.App.Tests.Services.Scripting;

/// <summary>
/// v3.5.7 PATCH: regression coverage for ClearScript's reflection guard
/// — the actual security boundary behind the v3.5.5 sandbox hardening.
/// <para>
/// v3.5.5's <c>AddRestrictedHostObject&lt;T&gt;</c> was originally
/// described as hiding System.Object members (<c>GetType</c>,
/// <c>ToString</c>, <c>Equals</c>). Empirical testing on ClearScript 7.4.5
/// revealed those members ARE exposed as script-callable functions, but
/// ClearScript's <c>ScriptEngine.CheckReflection()</c> blocks reflection
/// attempts at INVOCATION time with <c>UnauthorizedAccessException</c>.
/// These tests pin that behavior so future ClearScript upgrades that
/// weaken the guard are caught.
/// </para>
/// <para>
/// Test names use the format <c>Restricted_*_Blocked*</c> /
/// <c>Unrestricted_*_Blocked*</c> to make the security boundary explicit
/// in test output.
/// </para>
/// </summary>
public class ScriptEngineReflectionGuardTests
{
    private readonly ITestOutputHelper _output;
    public ScriptEngineReflectionGuardTests(ITestOutputHelper output) => _output = output;

    private static string SafeEval(V8ScriptEngine engine, string code)
    {
        try
        {
            var result = engine.Evaluate(code);
            return result?.ToString() ?? "null";
        }
        catch (System.Exception ex)
        {
            // Reflection guard throws UnauthorizedAccessException, which
            // ClearScript wraps as ScriptEngineException. Capture the
            // message so the test output shows WHY it was blocked.
            return $"EX:{ex.GetType().Name}:{ex.Message}";
        }
    }

    [Fact]
    public void Restricted_can_GetType_Is_Blocked_By_Reflection_Guard()
    {
        // Per v3.5.5 PATCH: AddRestrictedHostObject<IScriptCanApi> restricts
        // the visible surface to IScriptCanApi's declared members. ClearScript
        // additionally blocks reflection attempts via CheckReflection().
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.DisableGlobalMembers);
        engine.AddRestrictedHostObject<IScriptCanApi>("can", new StubScriptApi());

        var result = SafeEval(engine, "can.GetType()");
        _output.WriteLine($"can.GetType() → {result}");
        result.Should().Contain("Use of reflection is prohibited",
            "ClearScript's reflection guard must block can.GetType() — that's the actual sandbox boundary");
    }

    [Fact]
    public void Restricted_can_ReflectionEscape_To_Process_Is_Blocked()
    {
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.DisableGlobalMembers);
        engine.AddRestrictedHostObject<IScriptCanApi>("can", new StubScriptApi());

        var result = SafeEval(engine,
            "try { var t = can.GetType(); var a = t.Assembly; var p = a.GetType('System.Diagnostics.Process'); p ? 'REACHED' : 'NO-PROCESS'; } " +
            "catch(e) { 'BLOCKED:' + e.message; }");
        _output.WriteLine($"can→GetType→Asm→Process → {result}");
        result.Should().StartWith("BLOCKED:");
    }

    [Fact]
    public void Unrestricted_Delegate_Method_Is_Blocked_By_Reflection_Guard()
    {
        // v3.5.5 PATCH: delegates (console_log / log / warn / error / delay /
        // hex / toHex) are injected via plain AddHostObject — System.Object
        // members AND Delegate.Method/Target are exposed as script-callable
        // properties. The reflection guard blocks Method access at both
        // typeof AND invocation, blocking any script → MethodInfo → Assembly
        // → System.Diagnostics.Process escape path.
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.DisableGlobalMembers);
        engine.AddHostObject("log", new Action<string>((msg) => { }));

        // Even typeof fails — the property accessor itself is flagged as
        // reflection. This is the strongest guard signal in ClearScript 7.4.5.
        var result = SafeEval(engine, "typeof log.Method");
        _output.WriteLine($"typeof log.Method → {result}");
        result.Should().Contain("Use of reflection is prohibited",
            "ClearScript must block Delegate.Method access on unrestricted host objects");
    }

    [Fact]
    public void Unrestricted_Delegate_ReflectionEscape_To_Process_Is_Blocked()
    {
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.DisableGlobalMembers);
        engine.AddHostObject("log", new Action<string>((msg) => { }));

        var result = SafeEval(engine,
            "try { var m = log.Method; var a = m.DeclaringType.Assembly; var p = a.GetType('System.Diagnostics.Process'); p ? 'REACHED' : 'NO-PROCESS'; } " +
            "catch(e) { 'BLOCKED:' + e.message; }");
        _output.WriteLine($"log→Method→Asm→Process → {result}");
        result.Should().StartWith("BLOCKED:");
    }

    [Fact]
    public void Unrestricted_Delegate_ReflectionEscape_Via_GetType_Is_Blocked()
    {
        // Variant using GetType() instead of Method — should also be blocked.
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.DisableGlobalMembers);
        engine.AddHostObject("log", new Action<string>((msg) => { }));

        var result = SafeEval(engine,
            "try { var t = log.GetType(); var a = t.Assembly; var ts = a.GetTypes(); ts.length > 0 ? 'ALL-TYPES:' + ts.length : 'EMPTY'; } " +
            "catch(e) { 'BLOCKED:' + e.message; }");
        _output.WriteLine($"log→GetType→Asm→GetTypes → {result}");
        result.Should().StartWith("BLOCKED:");
    }

    private sealed class StubScriptApi : IScriptCanApi
    {
        public Task<bool> Send(int id, byte[] data, bool fd = false, bool extended = false) => Task.FromResult(true);
        public bool IsConnected => false;
        public string? GetChannelId() => null;
        public string OnFrame(Action<CanFrame> callback) => "";
        public void OffFrame(string callbackId) { }
        public string OnMessage(object id, Action<CanFrame> callback) => "";
        public void OffMessage(object id, string callbackId) { }
        public Task<bool> Send(CanFrame frame) => Task.FromResult(true);
    }
}