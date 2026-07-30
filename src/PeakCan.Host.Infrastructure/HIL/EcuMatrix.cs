using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.CanChannels;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Multiple VirtualEcu instances sharing a single VirtualChannel.
/// Each ECU responds to different CAN ID pairs.
/// </summary>
public sealed class EcuMatrix : IDisposable
{
    private readonly List<VirtualEcu> _ecus = new();
    private readonly VirtualChannel _channel;
    private int _disposed;

    public EcuMatrix(int channelCapacity = 1000)
    {
        _channel = new VirtualChannel(channelCapacity);
    }

    public void AddEcu(EcuScript script, ILogger<VirtualEcu>? logger = null)
    {
        // Create ECU first to get its actual send ID (VirtualEcu.RequestId = CanIds.ResponseId)
        var ecu = new VirtualEcu(_channel, script.CanIds, script.Rules, logger);

        // CAN ID conflict detection: two ECUs cannot send on the same CAN ID
        var newSendId = ecu.RequestId;
        if (_ecus.Any(e => e.RequestId == newSendId))
        {
            ecu.Dispose();
            throw new InvalidOperationException(
                $"CAN ID conflict: request ID 0x{newSendId:X3} already assigned to another ECU");
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
