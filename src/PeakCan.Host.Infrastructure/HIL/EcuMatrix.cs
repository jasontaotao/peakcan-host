using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.CanChannels;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Multiple StatefulVirtualEcu instances sharing a single VirtualChannel.
/// Each ECU responds to different CAN ID pairs.
/// </summary>
public sealed class EcuMatrix : IDisposable
{
    private readonly List<StatefulVirtualEcu> _ecus = new();
    private readonly VirtualChannel _channel;
    private int _disposed;

    public EcuMatrix(int channelCapacity = 1000)
    {
        _channel = new VirtualChannel(channelCapacity);
    }

    public void AddEcu(EcuScript script, ILogger<StatefulVirtualEcu>? logger = null)
    {
        var ecu = new StatefulVirtualEcu(_channel, script.CanIds, script.StateMachine, logger);

        // Sprint 10: Inject DidValues if present in script but not yet in context
        if (script.DidValues is { Count: > 0 } && !ecu.StateMachine.Context.HasKey("DidValues"))
        {
            ecu.StateMachine.Context.Set("DidValues", script.DidValues);
        }

        // CAN ID conflict detection: two ECUs cannot send on the same CAN ID
        var newSendId = ecu.SendCanId;
        if (_ecus.Any(e => e.SendCanId == newSendId))
        {
            ecu.Dispose();
            throw new InvalidOperationException(
                $"CAN ID conflict: send ID 0x{newSendId:X3} already assigned to another ECU");
        }

        _ecus.Add(ecu);
    }

    public ICanChannel Channel => _channel;

    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        => _channel.ConnectAsync(baud, fd, ct);

    public Task DisconnectAsync(CancellationToken ct = default)
        => _channel.DisconnectAsync(ct);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        foreach (var ecu in _ecus) ecu.Dispose();
        _channel.Dispose();
    }
}
