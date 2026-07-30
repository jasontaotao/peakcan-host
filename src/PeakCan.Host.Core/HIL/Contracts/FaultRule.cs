namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>
/// Fault injection rule: matches frames by CAN ID, applies a fault transformation.
/// </summary>
public sealed record FaultRule
{
    public required FaultType Type { get; init; }

    /// <summary>Target CAN ID. null = match all frames.</summary>
    public uint? TargetCanId { get; init; }

    /// <summary>Drop probability (0.0-1.0). For Drop type only.</summary>
    public double Probability { get; init; } = 1.0;

    /// <summary>Delay in ms. For Delay type only.</summary>
    public int DelayMs { get; init; }

    /// <summary>Byte positions to corrupt. For Corrupt type only.</summary>
    public int[]? CorruptByteIndices { get; init; }

    /// <summary>XOR mask for corruption. For Corrupt type only.</summary>
    public byte CorruptXorMask { get; init; } = 0xFF;

    public bool Matches(CanFrame frame)
        => TargetCanId is null || frame.Id.Raw == TargetCanId.Value;

    public IReadOnlyList<CanFrame> Apply(CanFrame frame)
    {
        return Type switch
        {
            FaultType.Drop => ApplyDrop(frame),
            FaultType.Corrupt => ApplyCorrupt(frame),
            FaultType.Duplicate => ApplyDuplicate(frame),
            _ => new[] { frame }
        };
    }

    private IReadOnlyList<CanFrame> ApplyDrop(CanFrame frame)
    {
        // Random.Shared is thread-safe
        if (Random.Shared.NextDouble() < Probability)
            return Array.Empty<CanFrame>(); // drop frame
        return new[] { frame };
    }

    private IReadOnlyList<CanFrame> ApplyCorrupt(CanFrame frame)
    {
        if (CorruptByteIndices is null || CorruptByteIndices.Length == 0)
            return new[] { frame };

        // CanFrame.Data is ReadOnlyMemory<byte>, need to copy-modify-wrap
        var data = frame.Data.ToArray();
        foreach (var idx in CorruptByteIndices)
        {
            if (idx >= 0 && idx < data.Length)
                data[idx] ^= CorruptXorMask;
        }
        return new[] { frame with { Data = new ReadOnlyMemory<byte>(data) } };
    }

    private IReadOnlyList<CanFrame> ApplyDuplicate(CanFrame frame)
        => new[] { frame, frame }; // send twice
}

public enum FaultType
{
    /// <summary>Drop frame (optionally probabilistic).</summary>
    Drop,

    /// <summary>Delay frame by N ms.</summary>
    Delay,

    /// <summary>Corrupt specific byte positions via XOR.</summary>
    Corrupt,

    /// <summary>Send frame twice.</summary>
    Duplicate,
}
