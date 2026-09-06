namespace PeakCan.Host.Core.Uds.IsoTp;

/// <summary>
/// ISO 15765-2 (ISO-TP) frame types for segmented message transport
/// over CAN. Supports Single Frame (SF), First Frame (FF),
/// Consecutive Frame (CF), and Flow Control (FC).
/// </summary>
public enum IsoTpFrameType
{
    /// <summary>Single Frame — payload ≤ 7 bytes (classic) or ≤ 63 bytes (CAN FD).</summary>
    Single = 0,

    /// <summary>First Frame — initiates multi-frame transfer.</summary>
    First = 1,

    /// <summary>Consecutive Frame — continues multi-frame transfer.</summary>
    Consecutive = 2,

    /// <summary>Flow Control — regulates multi-frame transfer.</summary>
    FlowControl = 3
}

/// <summary>
/// ISO-TP frame structure. Encapsulates the PCI (Protocol Control
/// Information) and payload for each frame type.
/// </summary>
public readonly record struct IsoTpFrame
{
    /// <summary>Frame type (SF, FF, CF, FC).</summary>
    public IsoTpFrameType Type { get; }

    /// <summary>
    /// Payload length (SF) or total message length (FF).
    /// For CF/FC, this field is not used.
    /// </summary>
    public int Length { get; }

    /// <summary>
    /// Sequence number (CF) or flow status (FC).
    /// For SF/FF, this field is not used.
    /// </summary>
    public int SequenceOrStatus { get; }

    /// <summary>
    /// Block size (FC only). Number of CF frames before next FC.
    /// 0 = unlimited (send all remaining CFs).
    /// </summary>
    public int BlockSize { get; }

    /// <summary>
    /// STmin (FC only). Minimum time between CF frames in milliseconds.
    /// 0-127 ms, or 0xF1-0xF9 for 100-900 μs.
    /// </summary>
    public int StMin { get; }

    /// <summary>Frame payload bytes.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    public IsoTpFrame(
        IsoTpFrameType type,
        int length = 0,
        int sequenceOrStatus = 0,
        int blockSize = 0,
        int stMin = 0,
        ReadOnlyMemory<byte> data = default)
    {
        Type = type;
        Length = length;
        SequenceOrStatus = sequenceOrStatus;
        BlockSize = blockSize;
        StMin = stMin;
        Data = data;
    }

    /// <summary>
    /// Encode this frame into CAN data bytes (8 bytes for classic CAN).
    /// </summary>
    public byte[] Encode()
    {
        var result = new byte[8];

        switch (Type)
        {
            case IsoTpFrameType.Single:
                // SF: PCI = 0x0L where L = data length
                result[0] = (byte)(0x00 | (Data.Length & 0x0F));
                Data.Span.CopyTo(result.AsSpan(1));
                break;

            case IsoTpFrameType.First:
                // FF: PCI = 0x1L LL where LLLL = total length
                result[0] = (byte)(0x10 | ((Length >> 8) & 0x0F));
                result[1] = (byte)(Length & 0xFF);
                Data.Span.CopyTo(result.AsSpan(2));
                break;

            case IsoTpFrameType.Consecutive:
                // CF: PCI = 0x2N where N = sequence number (0-15)
                result[0] = (byte)(0x20 | (SequenceOrStatus & 0x0F));
                Data.Span.CopyTo(result.AsSpan(1));
                break;

            case IsoTpFrameType.FlowControl:
                // FC: PCI = 0x3F FS BS STmin
                result[0] = (byte)(0x30 | (SequenceOrStatus & 0x0F)); // Flow status
                result[1] = (byte)(BlockSize & 0xFF);
                result[2] = (byte)(StMin & 0xFF);
                break;
        }

        return result;
    }

    /// <summary>
    /// Decode CAN data bytes into an ISO-TP frame.
    /// </summary>
    /// <param name="data">CAN frame data (8 bytes for classic CAN).</param>
    /// <returns>Decoded ISO-TP frame.</returns>
    /// <exception cref="ArgumentException">Invalid frame data.</exception>
    public static IsoTpFrame Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
            throw new ArgumentException("Frame data too short", nameof(data));

        int pci = (data[0] >> 4) & 0x0F;

        return pci switch
        {
            0x0 => DecodeSingleFrame(data),
            0x1 => DecodeFirstFrame(data),
            0x2 => DecodeConsecutiveFrame(data),
            0x3 => DecodeFlowControl(data),
            _ => throw new ArgumentException($"Unknown PCI: 0x{pci:X}", nameof(data))
        };
    }

    private static IsoTpFrame DecodeSingleFrame(ReadOnlySpan<byte> data)
    {
        int length = data[0] & 0x0F;
        if (length == 0)
            throw new ArgumentException("SF length cannot be 0");

        var payload = data.Slice(1, Math.Min(length, data.Length - 1)).ToArray();
        return new IsoTpFrame(IsoTpFrameType.Single, data: payload);
    }

    private static IsoTpFrame DecodeFirstFrame(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            throw new ArgumentException("FF data too short");

        int length = ((data[0] & 0x0F) << 8) | data[1];
        if (length < 8)
            throw new ArgumentException($"FF length must be ≥ 8, got {length}");

        var payload = data.Slice(2).ToArray();
        return new IsoTpFrame(IsoTpFrameType.First, length: length, data: payload);
    }

    private static IsoTpFrame DecodeConsecutiveFrame(ReadOnlySpan<byte> data)
    {
        int sequence = data[0] & 0x0F;
        var payload = data.Slice(1).ToArray();
        return new IsoTpFrame(IsoTpFrameType.Consecutive, sequenceOrStatus: sequence, data: payload);
    }

    private static IsoTpFrame DecodeFlowControl(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
            throw new ArgumentException("FC data too short");

        int flowStatus = data[0] & 0x0F;
        int blockSize = data[1];
        int stMin = data[2];

        return new IsoTpFrame(
            IsoTpFrameType.FlowControl,
            sequenceOrStatus: flowStatus,
            blockSize: blockSize,
            stMin: stMin);
    }
}
