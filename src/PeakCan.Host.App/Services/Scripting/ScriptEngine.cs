using System.Collections.Concurrent;
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
    public async Task<ScriptResult> RunAsync(
        string script,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        // Stop any previously running script.
        Stop();

        var effectiveTimeout = timeout ?? DefaultTimeout;
        _executionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _executionCts.CancelAfter(effectiveTimeout);

        var tcs = new TaskCompletionSource<ScriptResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock)
        {
            _executionTask = Task.Run(() => ExecuteScript(script, tcs, _executionCts.Token), _executionCts.Token);
        }

        // Register timeout callback.
        _executionCts.Token.Register(() =>
        {
            if (!tcs.Task.IsCompleted)
            {
                tcs.TrySetResult(new ScriptResult(
                    Success: false,
                    Error: "Script execution timed out",
                    ErrorType: ScriptErrorType.Timeout));
                InterruptEngine();
            }
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Stop the currently running script. Safe to call when no script
    /// is running (no-op). Calls <c>onDispose()</c> if defined.
    /// </summary>
    public void Stop()
    {
        _executionCts?.Cancel();
        InterruptEngine();

        lock (_lock)
        {
            if (_executionTask is { IsCompleted: false } task)
            {
                // Wait briefly for graceful shutdown.
                task.Wait(TimeSpan.FromMilliseconds(100));
            }
        }
    }

    /// <summary>
    /// Interrupt the V8 engine to break out of infinite loops.
    /// </summary>
    private void InterruptEngine()
    {
        try
        {
            _engine?.Interrupt();
        }
        catch (Exception ex)
        {
            LogInterruptFailed(_logger, ex);
        }
    }

    /// <summary>
    /// Core execution logic. Runs on a dedicated worker thread.
    /// </summary>
    private void ExecuteScript(string script, TaskCompletionSource<ScriptResult> tcs, CancellationToken ct)
    {
        V8ScriptEngine? engine = null;
        try
        {
            engine = CreateEngine(ct);
            _engine = engine;

            // Set the current engine for ScriptConsole routing.
            ScriptConsole.CurrentEngine = this;

            // Compile and run the script.
            engine.Execute(script);

            // If the script defines onInit(), call it.
            try
            {
                engine.Execute("if (typeof onInit === 'function') onInit();");
            }
            catch (Exception ex)
            {
                LogOnInitError(_logger, ex);
                EmitOutput(ScriptOutputLine.Error($"onInit() error: {ex.Message}"));
            }

            // Script completed successfully.
            tcs.TrySetResult(new ScriptResult(Success: true, Error: null, ErrorType: null));
        }
        catch (OperationCanceledException)
        {
            tcs.TrySetResult(new ScriptResult(
                Success: false,
                Error: "Script execution was cancelled",
                ErrorType: ScriptErrorType.Cancelled));
        }
        catch (Exception ex)
        {
            // V8 exceptions (V8ScriptInterruptedException, V8Exception) are
            // caught here since the specific types are not available without
            // additional using directives.
            var errorType = ex.Message.Contains("interrupted", StringComparison.OrdinalIgnoreCase)
                ? ScriptErrorType.Timeout
                : ScriptErrorType.Runtime;

            LogScriptError(_logger, ex);
            tcs.TrySetResult(new ScriptResult(
                Success: false,
                Error: ex.Message,
                ErrorType: errorType));
        }
        finally
        {
            // Call onDispose() if defined.
            if (engine is not null)
            {
                try
                {
                    engine.Execute("if (typeof onDispose === 'function') onDispose();");
                }
                catch { /* ignore cleanup errors */ }

                try
                {
                    engine.Dispose();
                }
                catch { /* ignore dispose errors */ }

                _engine = null;
                ScriptConsole.CurrentEngine = null;
            }
        }
    }

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
        if (_canApi is not null)
        {
            engine.AddHostObject("can", (IScriptCanApi)_canApi);
        }

        // Inject dbc.* API (if available). v1.7.0 MINOR Item 2: cast to
        // IScriptDbcApi so scripts see only the minimal surface (no Dispose).
        if (_dbcApi is not null)
        {
            engine.AddHostObject("dbc", (IScriptDbcApi)_dbcApi);
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
    Cancelled
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
}
