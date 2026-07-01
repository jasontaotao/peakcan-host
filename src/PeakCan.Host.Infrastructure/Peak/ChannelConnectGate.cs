using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.Infrastructure.Peak;

/// <summary>
/// Pure-logic state machine for the connect / read-loop / disconnect
/// lifecycle of a CAN channel. Owns the <see cref="object"/> lock, the
/// <see cref="CancellationTokenSource"/> that drives the read loop, and
/// the <see cref="Task"/> reference that <c>DisconnectAsync</c> awaits.
/// <para>
/// <b>Why a gate?</b> <see cref="PeakCanChannel"/> previously assigned
/// its <c>_readLoop</c> field outside the connect lock and disposed its
/// CTS unconditionally on every <c>DisconnectAsync</c> call. Under
/// concurrent Connect / Disconnect this caused two failure modes:
/// <list type="bullet">
///   <item>the read loop captured a CTS that was already disposed and
///         hung on <c>Task.Delay(_, ct)</c>; and</item>
///   <item>a second <c>DisposeAsync</c> call threw
///         <see cref="ObjectDisposedException"/>.</item>
/// </list>
/// Pulling the lock + CTS + read-loop references into one object lets
/// us test the state machine in isolation (the SDK calls themselves
/// require real hardware and stay in <see cref="PeakCanChannel"/>).
/// </para>
/// <para>
/// <b>Thread-safety:</b> every public member takes <see cref="_lock"/>
/// for the minimum critical section. The <c>_cts</c> field is replaced
/// rather than mutated so the read loop can hold a stable reference.
/// </para>
/// </summary>
internal sealed class ChannelConnectGate : IDisposable
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private bool _connected;

    /// <summary>Snapshot of the connected state. Safe to read from any thread.</summary>
    public bool IsConnected
    {
        get { lock (_lock) return _connected; }
    }

    /// <summary>Snapshot of the currently-running read loop, or null.</summary>
    public Task? CurrentReadLoop
    {
        get { lock (_lock) return _readLoop; }
    }

    /// <summary>
    /// Returns the live <see cref="CancellationToken"/> that the read
    /// loop should observe. Only valid after a successful
    /// <see cref="TryEnter"/> and before <see cref="CaptureForDisconnect"/>;
    /// the caller is expected to capture this once and hand it to the
    /// loop's first argument. When the gate is not connected, returns
    /// <see cref="CancellationToken.None"/> so misuse cannot throw.
    /// </summary>
    public CancellationToken CurrentToken
    {
        get
        {
            lock (_lock)
            {
                return _cts?.Token ?? CancellationToken.None;
            }
        }
    }

    /// <summary>
    /// Atomically check the connected state and reserve a fresh CTS for
    /// the upcoming read loop. On success, <see cref="IsConnected"/>
    /// flips to true and the caller may proceed to initialize the
    /// hardware. On failure (already connected) returns
    /// <see cref="ErrorCode.InvalidState"/> without mutating state.
    /// <para>
    /// Honours <paramref name="ct"/>: if the token is already cancelled
    /// before the lock is taken, throws
    /// <see cref="OperationCanceledException"/> and leaves the gate
    /// disconnected.
    /// </para>
    /// </summary>
    public Result<Unit> TryEnter(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_connected)
            {
                return Result<Unit>.Fail(
                    ErrorCode.InvalidState,
                    "Channel is already connected");
            }
            _cts = new CancellationTokenSource();
            _connected = true;
            return Result<Unit>.Ok(default);
        }
    }

    /// <summary>
    /// Roll back a <see cref="TryEnter"/> that subsequently failed to
    /// initialize the hardware. Idempotent; safe to call when the gate
    /// is already disconnected.
    /// </summary>
    public void MarkFailed()
    {
        lock (_lock)
        {
            if (!_connected) return;
            _cts?.Dispose();
            _cts = null;
            _readLoop = null;
            _connected = false;
        }
    }

    /// <summary>
    /// Record the running read loop task. Must be called after
    /// <see cref="TryEnter"/> succeeded; subsequent reads via
    /// <see cref="CaptureForDisconnect"/> return this reference.
    /// </summary>
    public void SetReadLoop(Task readLoop)
    {
        ArgumentNullException.ThrowIfNull(readLoop);
        lock (_lock) _readLoop = readLoop;
    }

    /// <summary>
    /// Cancel the read-loop CTS and return both the cancellation token
    /// (already cancelled, so the loop can be awaited) and the loop
    /// task itself. If the gate is not connected returns a no-op token
    /// and a null task — callers should null-check <c>loop</c>.
    /// </summary>
    public (CancellationToken Token, Task? Loop) CaptureForDisconnect()
    {
        lock (_lock)
        {
            if (!_connected) return (CancellationToken.None, null);
            _cts?.Cancel();
            var loop = _readLoop;
            _connected = false;
            return (_cts!.Token, loop);
        }
    }

    /// <summary>
    /// Dispose the CTS. Idempotent: safe to call multiple times
    /// (WPF teardown + DI container dispose both call this). Must be
    /// called only after <see cref="CaptureForDisconnect"/> (or
    /// <see cref="MarkFailed"/>) has cleared the connected flag, so the
    /// read loop is no longer holding the token.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? toDispose;
        lock (_lock)
        {
            toDispose = _cts;
            _cts = null;
            _readLoop = null;
        }
        toDispose?.Dispose();
    }
}
