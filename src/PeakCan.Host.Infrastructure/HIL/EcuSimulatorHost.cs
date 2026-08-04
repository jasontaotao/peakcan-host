using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds.IsoTp;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Hosts a stateful virtual ECU on a real CAN channel for standalone simulation.
/// Connects the channel, then blocks until cancellation. Disposes the ECU and
/// disconnects the channel on shutdown.
/// </summary>
public sealed class EcuSimulatorHost : IAsyncDisposable, IDisposable
{
    private readonly ICanChannel _channel;
    private readonly StatefulVirtualEcu _ecu;

    public EcuSimulatorHost(ICanChannel channel, CanIdConfig canIds,
        EcuStateMachine stateMachine, ILogger<StatefulVirtualEcu>? logger = null)
    {
        _channel = channel;

        // Sprint 13: detect CAN ID conflict (ECU would send and receive on same ID).
        if (canIds.RequestId == canIds.ResponseId)
        {
            logger?.LogWarning(
                "ECU CAN ID conflict: RequestId == ResponseId (0x{Id:X}). " +
                "ECU would send and receive on the same CAN ID — messages may loop back.",
                canIds.RequestId);
        }

        _ecu = new StatefulVirtualEcu(channel, canIds, stateMachine, logger);
    }

    /// <summary>
    /// Connect the channel and block until <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        await _channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true, ct).ConfigureAwait(false);
        try
        {
            // Block until cancellation — the ECU reacts to incoming frames via events.
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            await _channel.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _ecu.Dispose();
        await _channel.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _ecu.Dispose();
        // Synchronous dispose for non-async callers (tests).
        // Use ConfigureFalse + GetResult to minimize deadlock risk.
        _channel.DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
    }
}
