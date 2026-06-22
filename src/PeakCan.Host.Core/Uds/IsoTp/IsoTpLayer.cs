using System.Buffers;
using System.Collections.Concurrent;

namespace PeakCan.Host.Core.Uds.IsoTp;

/// <summary>
/// ISO 15765-2 (ISO-TP) transport layer. Handles segmentation and
/// reassembly of messages longer than 7 bytes (classic CAN) or
/// 63 bytes (CAN FD).
/// <para>
/// <b>Thread-safety:</b> This class is thread-safe. Frame reception
/// and transmission are handled via callbacks that are invoked on
/// the caller's thread.
/// </para>
/// </summary>
public sealed class IsoTpLayer
{
    /// <summary>Maximum payload size for Single Frame (classic CAN).</summary>
    public const int MaxSingleFramePayload = 7;

    /// <summary>Maximum payload size for a complete message (4095 bytes per ISO-TP).</summary>
    public const int MaxMessageLength = 4095;

    private readonly CanIdConfig _config;
    private readonly Action<CanFrame> _sendFrame;

    // Reassembly state for incoming messages.
    private readonly object _rxLock = new();
    private byte[]? _rxBuffer;
    private int _rxExpectedLength;
    private int _rxReceivedLength;
    private int _rxExpectedSequence;
    private bool _rxInProgress;

    // Transmission state for outgoing messages.
    private readonly object _txLock = new();
    private int _txBlockSize;
    private int _txStMin;
    private bool _txWaitingForFc;

    /// <summary>Raised when a complete message is reassembled.</summary>
    public event Action<byte[]>? MessageReceived;

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
    /// Send a message, segmenting it into multiple CAN frames if needed.
    /// </summary>
    /// <param name="data">Message payload (up to 4095 bytes).</param>
    /// <exception cref="ArgumentException">Payload too long.</exception>
    public async Task SendMessageAsync(byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length > MaxMessageLength)
            throw new ArgumentException($"Payload too long: {data.Length} > {MaxMessageLength}");

        if (data.Length <= MaxSingleFramePayload)
        {
            // Single Frame
            SendSingleFrame(data);
        }
        else
        {
            // Multi-frame: FF + CFs
            await SendMultiFrameAsync(data, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Process an incoming CAN frame. Call this from your CAN receive handler.
    /// </summary>
    /// <param name="frame">Received CAN frame.</param>
    public void ProcessFrame(CanFrame frame)
    {
        // Only process frames with our response CAN ID.
        if (frame.Id.Raw != _config.ResponseId)
            return;

        var isoFrame = IsoTpFrame.Decode(frame.Data.Span);

        switch (isoFrame.Type)
        {
            case IsoTpFrameType.Single:
                HandleSingleFrame(isoFrame);
                break;

            case IsoTpFrameType.First:
                HandleFirstFrame(isoFrame);
                break;

            case IsoTpFrameType.Consecutive:
                HandleConsecutiveFrame(isoFrame);
                break;

            case IsoTpFrameType.FlowControl:
                HandleFlowControl(isoFrame);
                break;
        }
    }

    /// <summary>Reset reassembly state (e.g., on timeout).</summary>
    public void Reset()
    {
        lock (_rxLock)
        {
            _rxInProgress = false;
            _rxBuffer = null;
        }

        lock (_txLock)
        {
            _txWaitingForFc = false;
        }
    }

    private void SendSingleFrame(byte[] data)
    {
        var frame = new IsoTpFrame(IsoTpFrameType.Single, data: data);
        var canData = frame.Encode();
        SendCanFrame(canData);
    }

    private async Task SendMultiFrameAsync(byte[] data, CancellationToken ct)
    {
        lock (_txLock)
        {
            _txWaitingForFc = true;
        }

        // Send First Frame
        var ffData = data.AsMemory(0, Math.Min(6, data.Length));
        var ff = new IsoTpFrame(IsoTpFrameType.First, length: data.Length, data: ffData);
        SendCanFrame(ff.Encode());

        // Wait for Flow Control
        if (!await WaitForFlowControlAsync(ct).ConfigureAwait(false))
            throw new TimeoutException("No Flow Control received");

        // Send Consecutive Frames
        int offset = 6;
        int sequence = 1;

        while (offset < data.Length)
        {
            ct.ThrowIfCancellationRequested();

            int chunkSize = Math.Min(7, data.Length - offset);
            var cfData = data.AsMemory(offset, chunkSize);
            var cf = new IsoTpFrame(IsoTpFrameType.Consecutive, sequenceOrStatus: sequence, data: cfData);
            SendCanFrame(cf.Encode());

            offset += chunkSize;
            sequence = (sequence + 1) & 0x0F;

            // Apply STmin delay
            if (_txStMin > 0 && offset < data.Length)
            {
                await Task.Delay(_txStMin, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> WaitForFlowControlAsync(CancellationToken ct)
    {
        // Wait up to P2* (5 seconds default) for Flow Control
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        while (!timeoutCts.Token.IsCancellationRequested)
        {
            lock (_txLock)
            {
                if (!_txWaitingForFc)
                    return true;
            }

            await Task.Delay(1, timeoutCts.Token).ConfigureAwait(false);
        }

        return false;
    }

    private void HandleSingleFrame(IsoTpFrame frame)
    {
        // Single frame: complete message
        MessageReceived?.Invoke(frame.Data.ToArray());
    }

    private void HandleFirstFrame(IsoTpFrame frame)
    {
        lock (_rxLock)
        {
            _rxInProgress = true;
            _rxExpectedLength = frame.Length;
            _rxReceivedLength = 0;
            _rxExpectedSequence = 1;
            _rxBuffer = new byte[frame.Length];

            // Copy first chunk
            var firstChunk = frame.Data.Span;
            firstChunk.CopyTo(_rxBuffer.AsSpan(0, Math.Min(firstChunk.Length, frame.Length)));
            _rxReceivedLength += firstChunk.Length;

            // Send Flow Control
            SendFlowControl();
        }
    }

    private void HandleConsecutiveFrame(IsoTpFrame frame)
    {
        lock (_rxLock)
        {
            if (!_rxInProgress || _rxBuffer is null)
                return;

            // Validate sequence number
            if (frame.SequenceOrStatus != _rxExpectedSequence)
            {
                // Sequence error: abort reassembly
                _rxInProgress = false;
                _rxBuffer = null;
                return;
            }

            // Copy data
            int remaining = _rxExpectedLength - _rxReceivedLength;
            int chunkSize = Math.Min(frame.Data.Length, remaining);
            frame.Data.Span.Slice(0, chunkSize).CopyTo(_rxBuffer.AsSpan(_rxReceivedLength, chunkSize));
            _rxReceivedLength += chunkSize;
            _rxExpectedSequence = (_rxExpectedSequence + 1) & 0x0F;

            // Check if complete
            if (_rxReceivedLength >= _rxExpectedLength)
            {
                var complete = _rxBuffer;
                _rxInProgress = false;
                _rxBuffer = null;

                // Deliver complete message
                MessageReceived?.Invoke(complete);
            }
        }
    }

    private void HandleFlowControl(IsoTpFrame frame)
    {
        lock (_txLock)
        {
            if (!_txWaitingForFc)
                return;

            _txBlockSize = frame.BlockSize;
            _txStMin = frame.StMin;
            _txWaitingForFc = false;
        }
    }

    private void SendFlowControl()
    {
        // Send Flow Control with BS=0 (unlimited), STmin=0 (no delay)
        var fc = new IsoTpFrame(
            IsoTpFrameType.FlowControl,
            sequenceOrStatus: 0, // Continue to send
            blockSize: 0,       // Unlimited
            stMin: 0);          // No delay

        var canData = fc.Encode();
        SendCanFrame(canData);
    }

    private void SendCanFrame(byte[] data)
    {
        var frame = new CanFrame(
            new CanId(_config.RequestId, FrameFormat.Standard),
            data,
            FrameFlags.None,
            default,
            default);

        _sendFrame(frame);
    }
}

/// <summary>
/// CAN ID configuration for ISO-TP communication.
/// </summary>
public sealed record CanIdConfig
{
    /// <summary>CAN ID for request frames (client → ECU).</summary>
    public uint RequestId { get; init; }

    /// <summary>CAN ID for response frames (ECU → client).</summary>
    public uint ResponseId { get; init; }

    /// <summary>
    /// Functional CAN ID for broadcast requests (optional).
    /// Used for functional addressing (e.g., 0x7DF for OBD-II).
    /// </summary>
    public uint? FunctionalId { get; init; }
}
