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
    // P1-2（2026-09-06，Lazy<T> 清零）：ScriptUtilities 直接 ctor 注入——输出环已由
    // ScriptOutputHub 解耦（ScriptUtilities → hub → 本类订阅转发），不再需要延迟解析。
    private readonly ScriptUtilities? _utilities;
    private readonly ScriptEngineOptions _options;
    private readonly ScriptOutputHub? _outputHub;
    private readonly Action<ScriptOutputLine>? _hubForward;

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
    /// 6-arg ctor so existing callers and tests see no behavior change.
    /// </summary>
    public ScriptEngine(
        ILogger<ScriptEngine> logger,
        CanApi? canApi,
        DbcApi? dbcApi,
        ScriptUtilities? utilities)
        : this(logger, canApi, dbcApi, utilities, ScriptEngineOptions.Default, outputHub: null)
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
    /// <para>
    /// P1-2（2026-09-06，Lazy&lt;T&gt; 清零）：<paramref name="utilities"/> 直接持有；
    /// <paramref name="outputHub"/> 非空时订阅并转发到
    /// <see cref="OutputReceived"/>（ScriptUtilities 的 log/warn/error 经 hub 抵达
    /// 外部订阅者，行为与旧 IScriptOutputSink 直连一致）。
    /// </para>
    /// </summary>
    internal ScriptEngine(
        ILogger<ScriptEngine> logger,
        CanApi? canApi,
        DbcApi? dbcApi,
        ScriptUtilities? utilities,
        ScriptEngineOptions options,
        ScriptOutputHub? outputHub = null)
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
        _outputHub = outputHub;
        if (outputHub is not null)
        {
            _hubForward = line => OutputReceived?.Invoke(line);
            outputHub.OutputReceived += _hubForward;
        }
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



    public void Dispose()
    {
        Stop();
        if (_outputHub is not null && _hubForward is not null)
            _outputHub.OutputReceived -= _hubForward;
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
    // === Flow B methods moved to ScriptEngine/CreateEngineFlow.cs (W14 Task 2) ===
    // === Flow C methods moved to ScriptEngine/ScriptHelpersFlow.cs (W14 Task 3) ===
}
