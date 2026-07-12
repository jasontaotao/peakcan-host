using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace PeakCan.Host.App.Services.Scripting;

public sealed partial class ScriptEngine
{
    // Flow B: CreateEngine (v1.7.0 MINOR Item 1 + v3.5.5 PATCH + earlier).
    // V8 engine creation with sandboxed globals + AddRestrictedHostObject
    // hardening. ClearScript-specific knowledge isolated to this partial.
    //
    // Cross-flow callers (partial-class visible):
    //   - CreateEngine <- ExecuteScript (Flow A)

    /// <summary>
    /// Create a new V8 engine with sandboxed globals.
    /// <para>
    /// v1.7.0 MINOR Item 1: applies <see cref="ScriptEngineOptions"/>
    /// resource caps via <c>V8RuntimeConstraints</c> (hard generation
    /// caps in MiB) and <c>V8ScriptEngine.MaxRuntimeHeapSize</c> (soft
    /// monitor cap in bytes). ClearScript 7.4.5 has no
    /// <c>V8ScriptEngine(flags, V8Runtime)</c> overload — the
    /// V8ScriptEngine owns its V8Runtime internally, so we apply
    /// constraints at construction + set the monitor cap afterward.
    /// </para>
    /// </summary>
    private V8ScriptEngine CreateEngine(CancellationToken ct)
    {
        // V8RuntimeConstraints properties are in MiB. Allocation
        // (new/old/exec) is requested as-is; negative or zero values
        // use ClearScript defaults.
        var constraints = new V8RuntimeConstraints
        {
            MaxNewSpaceSize = _options.MaxNewSpaceSizeMB,
            MaxOldSpaceSize = _options.MaxOldSpaceSizeMB,
        };

        var engine = new V8ScriptEngine(constraints, V8ScriptEngineFlags.DisableGlobalMembers);

        // MaxRuntimeHeapSize is in BYTES (nuint) — convert from MB.
        // Setting a monitor cap enables heap-size monitoring that
        // triggers V8RuntimeViolationPolicy.Interrupt (default) when
        // exceeded, preventing process termination on runaway scripts.
        engine.MaxRuntimeHeapSize = (nuint)(_options.MaxHeapSizeMB * 1024L * 1024L);

        // Set the current engine for ScriptConsole routing.
        ScriptConsole.CurrentEngine = this;

        // Inject console.log/warn/error as host object with lambda functions.
        // ClearScript's AddHostType doesn't work well with static methods,
        // so we inject individual functions instead.
        engine.AddHostObject("console_log", (Action<object[]>)((args) => ScriptConsole.Log(args)));
        engine.AddHostObject("console_warn", (Action<object[]>)((args) => ScriptConsole.Warn(args)));
        engine.AddHostObject("console_error", (Action<object[]>)((args) => ScriptConsole.Error(args)));

        // Execute JS to create the console object.
        engine.Execute(@"
            var console = {
                log: function() { console_log(Array.from(arguments)); },
                warn: function() { console_warn(Array.from(arguments)); },
                error: function() { console_error(Array.from(arguments)); }
            };
        ");

        // Inject can.* API (if available). v1.7.0 MINOR Item 2: cast to
        // IScriptCanApi so scripts see only the minimal surface (no Dispose,
        // no IFrameSink members).
        // v3.5.5 PATCH: use AddRestrictedHostObject<IScriptCanApi> so
        // ClearScript exposes ONLY members declared on IScriptCanApi,
        // not System.Object members like GetType/ToString/Equals that
        // would let a script reach CanApi's runtime type and from
        // there into arbitrary .NET runtime types
        // (e.g. can.GetType().Assembly.GetType("System.Diagnostics.Process")).
        // This is the "Option A" hardening path recommended by the
        // brief; ClearScript 7.4.5 supports it directly via the
        // <T>-constrained overload. IScriptCanApi carries no
        // overrides for the System.Object members so they are not
        // reachable from script code.
        if (_canApi is not null)
        {
            engine.AddRestrictedHostObject<IScriptCanApi>("can", _canApi);
        }

        // Inject dbc.* API (if available). v1.7.0 MINOR Item 2: cast to
        // IScriptDbcApi so scripts see only the minimal surface (no Dispose).
        // v3.5.5 PATCH: same AddRestrictedHostObject hardening as `can`.
        if (_dbcApi is not null)
        {
            engine.AddRestrictedHostObject<IScriptDbcApi>("dbc", _dbcApi);
        }

        // Inject utility functions (if available).
        if (_utilities is not null)
        {
            engine.AddHostObject("log", (Action<string>)((msg) => _utilities.Log(msg)));
            engine.AddHostObject("warn", (Action<string>)((msg) => _utilities.Warn(msg)));
            engine.AddHostObject("error", (Action<string>)((msg) => _utilities.Error(msg)));
            engine.AddHostObject("delay", (Func<int, Task>)((ms) => _utilities.Delay(ms, ct)));
            engine.AddHostObject("hex", (Func<int, string?>?)((v) => _utilities.Hex(v)));
            engine.AddHostObject("toHex", (Func<byte[]?, string?>?)((b) => _utilities.ToHex(b)));
        }

        return engine;
    }
}
