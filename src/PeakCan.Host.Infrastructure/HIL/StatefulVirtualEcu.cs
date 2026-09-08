using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.IsoTp;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Stateful ECU simulator: uses EcuStateMachine for state-driven response generation.
/// Replaces Phase 3's stateless VirtualEcu when stateful behavior is needed.
/// <para>
/// M3.1（spec §6.3）：SID 0x27 由 host 侧 <see cref="SecurityAccessServer"/>
/// 全状态机接管（NRC 矩阵 + 锁定流程；hil-core 冻结不可扩展）；0x10/0x11
/// 作为生命周期钩子转达（默认会话重锁 / ECUReset 清易失计数）。
/// </para>
/// </summary>
public sealed class StatefulVirtualEcu : IDisposable
{
    private readonly ICanChannel _channel;
    private readonly IsoTpLayer _isoTp;
    private readonly EcuStateMachine _stateMachine;
    private readonly CanIdConfig _ecuCanIds;
    private readonly ILogger<StatefulVirtualEcu>? _logger;
    private readonly SecurityAccessServer _securityServer;
    private int _disposed;

    public static int InstanceCount;

    /// <summary>Current ECU state name (delegated to state machine).</summary>
    public string CurrentState => _stateMachine.CurrentState;

    /// <summary>Underlying state machine (for context access in tests).</summary>
    public EcuStateMachine StateMachine => _stateMachine;

    /// <summary>Server-side 0x27 machine (M3.1; test/E2E access).</summary>
    public SecurityAccessServer SecurityServer => _securityServer;

    /// <summary>
    /// ECU's send CAN ID (HIL listens here). Maps to CanIds.ResponseId (ECU perspective).
    /// </summary>
    public uint SendCanId => _ecuCanIds.ResponseId;

    public StatefulVirtualEcu(ICanChannel channel, CanIdConfig ecuCanIds,
        EcuStateMachine stateMachine, ILogger<StatefulVirtualEcu>? logger = null,
        SecurityAccessServer? securityServer = null)
    {
        _channel = channel;
        _ecuCanIds = ecuCanIds;
        _stateMachine = stateMachine;
        _logger = logger;
        _securityServer = securityServer ?? new SecurityAccessServer();
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
        // M3.1: host-side 0x27 machine answers before the (frozen) script
        // state machine; 0x10/0x11 are lifecycle hooks around script handling.
        if (request.Length >= 1)
        {
            switch (request[0])
            {
                case 0x27:
                {
                    // Authoritative response from the host-side full machine
                    // (NRC matrix + lockout). The script state machine still
                    // processes the request so script-driven 0x27 state
                    // transitions (locked→seedSent, ODX-imported scripts)
                    // remain observable side effects; its response is discarded.
                    _ = _stateMachine.ProcessRequest(request);
                    var secRsp = _securityServer.HandleRequest(request);
                    if (secRsp.Length == 0) return; // suppressPositiveResponseBit
                    _ = SendResponseAsync(secRsp, 0);
                    return;
                }
                case 0x10 when request.Length >= 2:
                {
                    var (resp, delay) = _stateMachine.ProcessRequest(request);
                    // Relock hook AFTER the session actually switched.
                    _securityServer.OnSessionChanged(defaultSession: (request[1] & 0x7F) == 0x01);
                    _ = SendResponseAsync(resp, delay);
                    return;
                }
                case 0x11:
                {
                    var (resp, delay) = _stateMachine.ProcessRequest(request);
                    _securityServer.OnEcuReset();
                    _ = SendResponseAsync(resp, delay);
                    return;
                }
            }
        }

        var (fallback, fallbackDelay) = _stateMachine.ProcessRequest(request);
        _ = SendResponseAsync(fallback, fallbackDelay);
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
