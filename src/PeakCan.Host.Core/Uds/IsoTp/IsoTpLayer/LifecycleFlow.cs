using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Uds.IsoTp;

public sealed partial class IsoTpLayer
{
    // Flow A: Lifecycle (v1.2.12 PATCH Item 2 + earlier).
    // 2 ctors + Reset + Dispose moved verbatim from IsoTpLayer.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - Reset -> CancelReceiveWatchdog (Flow E, cross-partial) + _rxLock/_rxInProgress/_rxBuffer/_txLock/_txWaitingForFc (state, main)
    //   - Dispose -> _sendGate (state, main)
    //   - ctors -> DI fields (main)

    /// <summary>Create a new ISO-TP layer.</summary>
    /// <param name="config">CAN ID configuration for request/response.</param>
    /// <param name="sendFrame">Callback to send a CAN frame.</param>
    public IsoTpLayer(CanIdConfig config, Action<CanFrame> sendFrame)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(sendFrame);

        _config = config;
        _sendFrame = sendFrame;
    }

    /// <summary>
    /// v1.2.12 PATCH Item 2: async-callback ctor overload. The async callback
    /// is awaited internally so the SDK read thread never blocks on
    /// <c>.AsTask().Wait()</c> (the v1.2.11 deadlock root cause). Concurrent
    /// <see cref="SendMessageAsync"/> calls are serialized through a
    /// <see cref="SemaphoreSlim"/>(1,1) so the FF/CF sequence of one transport
    /// cannot interleave with another.
    /// </summary>
    /// <param name="config">CAN ID configuration for request/response.</param>
    /// <param name="sendFrame">Async callback to send a CAN frame.</param>
    /// <param name="logger">Optional logger for send-callback exceptions (logged at Error, not propagated).</param>
    public IsoTpLayer(CanIdConfig config, Func<CanFrame, Task> sendFrame, ILogger<IsoTpLayer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(sendFrame);

        _config = config;
        _sendFrameAsync = sendFrame;
        _logger = logger;
    }

    /// <summary>Reset reassembly state (e.g., on timeout).</summary>
    public void Reset()
    {
        lock (_rxLock)
        {
            _rxInProgress = false;
            _rxBuffer = null;
        }
        CancelReceiveWatchdog();

        lock (_txLock)
        {
            _txWaitingForFc = false;
        }
    }

    /// <summary>
    /// v1.2.12 PATCH Item 2: dispose the send-path semaphore so the layer
    /// can be replaced cleanly. The app owns a single IsoTpLayer per
    /// channel, so this is called at process shutdown.
    /// </summary>
    public void Dispose()
    {
        _sendGate.Dispose();
    }
}