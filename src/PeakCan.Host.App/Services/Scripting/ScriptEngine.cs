using System.Collections.Concurrent;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// Manages a V8 JavaScript engine instance for executing user scripts
/// in a sandboxed environment. The engine exposes a whitelist of APIs
/// (can.*, dbc.*, utility functions) and blocks access to dangerous
/// system APIs (filesystem, network, process).
/// <para>
/// <b>Thread-safety:</b> The V8 engine is single-threaded. All script
/// execution happens on a dedicated worker thread. Callbacks registered
/// via <c>can.onFrame()</c> are marshaled to the V8 thread before
/// invocation.
/// </para>
/// <para>
/// <b>Lifecycle:</b> Each <see cref="RunAsync"/> call creates a fresh
/// V8 engine. The previous engine is disposed when the script stops or
/// when a new script starts. This prevents memory leaks from stale
/// engine state.
/// </para>
/// </summary>
public sealed partial class ScriptEngine : IDisposable
{
    /// <summary>Default script execution timeout (60 seconds).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly ILogger<ScriptEngine> _logger;
    private readonly CanApi? _canApi;
    private readonly DbcApi? _dbcApi;
    private readonly ScriptUtilities? _utilities;
    private readonly ScriptEngineOptions _options;

    private V8ScriptEngine? _engine;
    private CancellationTokenSource? _executionCts;
    private Task? _executionTask;
    // v3.5.8 PATCH: generation counter for stale-task drop. Mirrors the
    // CyclicSendService._generation + tickGen != generation pattern
    // (CyclicSendService.cs:41 + :180). RunAsync captures a generation
    // at entry; ExecuteScript self-checks at entry and bails if a newer
    // RunAsync has invalidated it. Prevents the stale-task write race
    // where a delayed old task's Interlocked.Exchange would overwrite
    // the new task's _engine reference.
    private long _generation;
    private readonly object _lock = new();

    /// <summary>Raised when the script produces output (log/warn/error).</summary>
    public event Action<ScriptOutputLine>? OutputReceived;

    /// <summary>True while a script is executing.</summary>
    public bool IsRunning
    {
        get { lock (_lock) return _executionTask is { IsCompleted: false }; }
    }

    /// <summary>
    /// Back-compat constructor. Equivalent to passing
    /// <see cref="ScriptEngineOptions.Default"/>; delegates to the
    /// 5-arg ctor so existing callers and tests see no behavior change.
    /// </summary>
    public ScriptEngine(
        ILogger<ScriptEngine> logger,
        CanApi? canApi,
        DbcApi? dbcApi,
        ScriptUtilities? utilities)
        : this(logger, canApi, dbcApi, utilities, ScriptEngineOptions.Default)
    {
    }

    /// <summary>
    /// v1.7.0 MINOR Item 1: full-fidelity constructor with
    /// <see cref="ScriptEngineOptions"/>. Bound at DI registration from
    /// <c>appsettings.json:Script</c> section.
    /// <para>
    /// <c>internal</c> because <see cref="ScriptEngineOptions"/> is
    /// internal (no public API justification for exposing V8 resource
    /// cap knobs to downstream consumers — DI configuration binding is
    /// the only entry point). Visible to test project via
    /// <c>InternalsVisibleTo PeakCan.Host.App.Tests</c>.
    /// </para>
    /// </summary>
    internal ScriptEngine(
        ILogger<ScriptEngine> logger,
        CanApi? canApi,
        DbcApi? dbcApi,
        ScriptUtilities? utilities,
        ScriptEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _canApi = canApi;
        _dbcApi = dbcApi;
        _utilities = utilities;
        // v1.7.0 MINOR Item 1: V8 isolate resource caps. Null = Default
        // (64 MB heap / 16 MiB new / 48 MiB old) — preserves pre-v1.7.0
        // behavior for direct-construction callers (unit tests).
        _options = options ?? ScriptEngineOptions.Default;
    }

    /// <summary>
    /// Execute <paramref name="script"/> in a sandboxed V8 engine.
    /// Returns a <see cref="ScriptResult"/> indicating success or failure.
    /// </summary>
    /// <param name="script">JavaScript source code to execute.</param>
    /// <param name="timeout">Maximum execution time. Pass null for <see cref="DefaultTimeout"/>.</param>
    /// <param name="ct">Cancellation token for external abort.</param>

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

    /// <summary>
    /// Emit an output line to subscribers.
    /// </summary>
    internal void EmitOutput(ScriptOutputLine line)
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

    public void Dispose()
    {
        Stop();
        _executionCts?.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to interrupt V8 engine")]
    private static partial void LogInterruptFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error calling onInit()")]
    private static partial void LogOnInitError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Script execution error")]
    private static partial void LogScriptError(ILogger logger, Exception ex);
}

/// <summary>
/// Result of a script execution.
/// </summary>
public sealed record ScriptResult(
    bool Success,
    string? Error,
    ScriptErrorType? ErrorType);

/// <summary>
/// Types of script errors.
/// </summary>
public enum ScriptErrorType
{
    /// <summary>JavaScript syntax or runtime error.</summary>
    Runtime,
    /// <summary>Script exceeded the timeout limit.</summary>
    Timeout,
    /// <summary>Script was cancelled externally.</summary>
    Cancelled,
    /// <summary>
    /// v1.7.3 PATCH Item 1: V8 resource-cap violation (heap monitor
    /// exceeded). Discriminated from <see cref="Runtime"/> by a
    /// <c>when</c> filter on <see cref="ScriptEngineException"/>'s
    /// message text via the <c>IsResourceLimit</c> helper.
    /// </summary>
    ResourceLimit
}

/// <summary>
/// A single line of script output.
/// </summary>
public sealed record ScriptOutputLine(
    DateTimeOffset Timestamp,
    string Message,
    ScriptOutputLevel Level)
{
    public static ScriptOutputLine Info(string message)
        => new(DateTimeOffset.Now, message, ScriptOutputLevel.Info);

    public static ScriptOutputLine Warning(string message)
        => new(DateTimeOffset.Now, message, ScriptOutputLevel.Warning);

    public static ScriptOutputLine Error(string message)
        => new(DateTimeOffset.Now, message, ScriptOutputLevel.Error);
}

/// <summary>
/// Script output severity levels.
/// </summary>
public enum ScriptOutputLevel
{
    Info,
    Warning,
    Error
    // === Flow A methods moved to ScriptEngine/ExecutionLifecycleFlow.cs (W14 Task 1) ===
}
