using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace PeakCan.Host.App.Services.Scripting;

public sealed partial class ScriptEngine
{
    // Flow A: ExecutionLifecycle (v1.7.0 MINOR + v1.7.1 PATCH + v1.7.3 PATCH
    // + v3.5.5 PATCH + v3.5.7 PATCH + v3.5.8 PATCH + earlier).
    // RunAsync + Stop + InterruptEngine + ExecuteScript kept together as ONE
    // partial per W14 D2 + W3 R3 sister lesson (mutable-state coupling on
    // _engine + _executionCts + _executionTask + _generation). State
    // ownership stays in main; this partial moves lifecycle methods only.
    //
    // Cross-flow callers (partial-class visible):
    //   - ExecuteScript -> CreateEngine (Flow B)
    //   - ExecuteScript -> EmitOutput + IsResourceLimit (Flow C)

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

        // v3.5.8 PATCH: increment generation BEFORE scheduling the new
        // task. Any older ExecuteScript instance that hasn't yet reached
        // its entry-check will see _generation > myGen on its eventual
        // entry and bail without writing to _engine. This closes the
        // stale-task write race that v3.5.7's Interlocked.Exchange
        // alone couldn't prevent (Task.Run scheduling delay could let
        // a delayed old task overwrite the new task's _engine reference).
        long myGen = Interlocked.Increment(ref _generation);

        lock (_lock)
        {
            _executionTask = Task.Run(() => ExecuteScript(script, tcs, myGen, _executionCts.Token), _executionCts.Token);
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
            // v3.5.7 PATCH: Volatile.Read pairs with the Interlocked.Exchange
            // in ExecuteScript to give this observe a proper acquire fence.
            // Without it, the JIT could cache the field across the null-check
            // and observe a stale value after a fresh RunAsync has replaced
            // it (paired with the v3.5.5 line-183 race the Interlocked.
            // Exchange write fixes; this read completes the pair).
            Volatile.Read(ref _engine)?.Interrupt();
        }
        catch (Exception ex)
        {
            LogInterruptFailed(_logger, ex);
        }
    }

    /// <summary>
    /// Core execution logic. Runs on a dedicated worker thread.
    /// </summary>
    /// <param name="myGen">Generation captured by <see cref="RunAsync"/>
    /// at entry. If a newer RunAsync has incremented _generation
    /// before this task gets scheduled, this task is stale and
    /// returns immediately without touching any state — see the
    /// entry-check below.</param>
    private void ExecuteScript(string script, TaskCompletionSource<ScriptResult> tcs, long myGen, CancellationToken ct)
    {
        V8ScriptEngine? engine = null;
        try
        {
            // v3.5.8 PATCH: stale-task drop. If _generation has moved
            // past myGen (a newer RunAsync started after this task was
            // scheduled), this task is stale — bail before CreateEngine
            // and before the _engine write. Without this guard, a
            // Task.Run scheduling delay could let an old task's
            // Interlocked.Exchange (line below) overwrite the new task's
            // _engine reference AFTER the new task already installed
            // its engine, leaving _engine pointing to the disposed old
            // engine while the new engine was never reachable via
            // InterruptEngine. Mirrors CyclicSendService:180's
            // `if (state is long tickGen && tickGen != generation) return;`
            // drop pattern.
            if (Interlocked.Read(ref _generation) != myGen) return;

            engine = CreateEngine(ct);
            // v3.5.7 PATCH: Interlocked.Exchange for atomic publish of the
            // engine reference. The previous plain field write
            // (`_engine = engine`) had a race when Stop() raced against a
            // concurrent RunAsync: the old task's line-198 assignment could
            // land AFTER the new task's assignment, leaving _engine pointing
            // to the old (interrupted) engine while the new engine was never
            // registered — making InterruptEngine() a no-op for the new
            // engine. Interlocked.Exchange pairs with the Volatile.Read in
            // InterruptEngine to give the publish + observe proper fences.
            Interlocked.Exchange(ref _engine, engine);

            // Set the current engine for ScriptConsole routing.
            ScriptConsole.CurrentEngine = this;

            // Compile and run the script.
            engine.Execute(script);

            // If the script defines onInit(), call it. v1.7.1 PATCH Item 2:
            // onInit failure flips ScriptResult.Success to false (was
            // previously logged but ignored — script appeared successful
            // even when onInit had thrown).
            var onInitFailed = false;
            try
            {
                engine.Execute("if (typeof onInit === 'function') onInit();");
            }
            catch (Exception ex)
            {
                onInitFailed = true;
                LogOnInitError(_logger, ex);
                EmitOutput(ScriptOutputLine.Error($"onInit() error: {ex.Message}"));
            }

            // Script completed successfully (only if main body + onInit succeeded).
            if (!onInitFailed)
            {
                tcs.TrySetResult(new ScriptResult(Success: true, Error: null, ErrorType: null));
            }
            else
            {
                tcs.TrySetResult(new ScriptResult(
                    Success: false,
                    Error: "onInit() threw an exception",
                    ErrorType: ScriptErrorType.Runtime));
            }
        }
        catch (ScriptInterruptedException)
        {
            // v1.7.1 PATCH Item 2: typed catch for V8 interrupt (was
            // string-matched on "interrupted" message — fragile, replaced
            // with proper typed catch). ClearScript 7.4.5 uses
            // ScriptInterruptedException in Microsoft.ClearScript
            // (V8ScriptInterruptedException is 7.5+). MUST come before
            // OperationCanceledException since ScriptInterruptedException
            // derives from it.
            tcs.TrySetResult(new ScriptResult(
                Success: false,
                Error: "Script execution was interrupted",
                ErrorType: ScriptErrorType.Timeout));
        }
        catch (OperationCanceledException)
        {
            tcs.TrySetResult(new ScriptResult(
                Success: false,
                Error: "Script execution was cancelled",
                ErrorType: ScriptErrorType.Cancelled));
        }
        catch (ScriptEngineException ex) when (IsResourceLimit(ex))
        {
            // v1.7.3 PATCH Item 1: discriminate V8 resource-cap
            // violations (heap monitor) from generic runtime errors.
            // Closes v1.7.1 PATCH review MEDIUM #1. The when filter
            // matches ClearScript 7.4.5's ScriptEngineException.Message
            // text against broad V8 resource-violation keywords; if
            // ClearScript version or V8 version changes the message
            // text, IsResourceLimit is the single tuning point.
            LogScriptError(_logger, ex);
            tcs.TrySetResult(new ScriptResult(
                Success: false,
                Error: ex.Message,
                ErrorType: ScriptErrorType.ResourceLimit));
        }
        catch (ScriptEngineException ex)
        {
            // v1.7.1 PATCH Item 2: typed catch for all other ClearScript
            // V8 script errors (base type). Includes syntax errors +
            // non-resource runtime script exceptions. ClearScript 7.4.5
            // uses ScriptEngineException in Microsoft.ClearScript
            // (V8Exception is 7.5+).
            LogScriptError(_logger, ex);
            tcs.TrySetResult(new ScriptResult(
                Success: false,
                Error: ex.Message,
                ErrorType: ScriptErrorType.Runtime));
        }
        catch (Exception ex)
        {
            // Fallback for non-ClearScript exceptions (e.g. AggregateException
            // from Task.Run, or host-side errors).
            LogScriptError(_logger, ex);
            tcs.TrySetResult(new ScriptResult(
                Success: false,
                Error: ex.Message,
                ErrorType: ScriptErrorType.Runtime));
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

                // v3.5.5 PATCH: CAS-protected null. Only clear _engine
                // if it still points to OUR engine — a subsequent
                // RunAsync may have replaced it. Without this, the old
                // finally can null out a fresh engine and break its
                // interrupt path (InterruptEngine reads _engine).
                if (Interlocked.CompareExchange(ref _engine, null, engine) == engine)
                {
                    ScriptConsole.CurrentEngine = null;
                }
            }
        }
    }
}
