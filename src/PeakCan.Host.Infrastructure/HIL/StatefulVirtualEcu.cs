using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.Uds.IsoTp;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Stateful ECU simulator: uses EcuStateMachine for state-driven response generation.
/// Replaces Phase 3's stateless VirtualEcu when stateful behavior is needed.
/// </summary>
public sealed class StatefulVirtualEcu : IDisposable
{
    private readonly ICanChannel _channel;
    private readonly IsoTpLayer _isoTp;
    private readonly EcuStateMachine _stateMachine;
    private readonly CanIdConfig _ecuCanIds;
    private readonly ILogger<StatefulVirtualEcu>? _logger;
    private int _disposed;

    public static int InstanceCount;

    /// <summary>Current ECU state name (delegated to state machine).</summary>
    public string CurrentState => _stateMachine.CurrentState;

    /// <summary>Underlying state machine (for context access in tests).</summary>
    public EcuStateMachine StateMachine => _stateMachine;

    /// <summary>
    /// ECU's send CAN ID (HIL listens here). Maps to CanIds.ResponseId (ECU perspective).
    /// </summary>
    public uint SendCanId => _ecuCanIds.ResponseId;

    public StatefulVirtualEcu(ICanChannel channel, CanIdConfig ecuCanIds,
        EcuStateMachine stateMachine, ILogger<StatefulVirtualEcu>? logger = null)
    {
        _channel = channel;
        _ecuCanIds = ecuCanIds;
        _stateMachine = stateMachine;
        _logger = logger;
        Interlocked.Increment(ref InstanceCount);

        _isoTp = new IsoTpLayer(ecuCanIds, SendFrameAsync, logger: null);
        _isoTp.MessageReceived += OnUdsRequestReceived;
        _channel.FrameReceived += OnCanFrameReceived;
    }

    private void OnCanFrameReceived(CanFrame frame)
    {
        try { _isoTp.ProcessFrame(frame); }
        catch (ArgumentException) { /* frame filtered by CAN ID - normal */ }
    }

    private void OnUdsRequestReceived(byte[] request)
    {
        var (response, delayMs) = _stateMachine.ProcessRequest(request);
        _ = SendResponseAsync(response, delayMs);
    }

    private async Task SendResponseAsync(byte[] data, int delayMs)
    {
        if (delayMs > 0)
            await Task.Delay(delayMs).ConfigureAwait(false);

        await _isoTp.SendMessageAsync(data).ConfigureAwait(false);
    }

    private Task SendFrameAsync(CanFrame frame)
        => _channel.WriteAsync(frame, CancellationToken.None).AsTask();

    /// <summary>Reset the ECU state machine to initial state.</summary>
    public void Reset() => _stateMachine.Reset();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Interlocked.Decrement(ref InstanceCount);
        _channel.FrameReceived -= OnCanFrameReceived;
        _isoTp.MessageReceived -= OnUdsRequestReceived;
        _isoTp.Dispose();
    }
}
