namespace PeakCan.Host.Core.Uds;

/// <summary>
/// Server-side 0x27 policy (spec §6.3). Defaults mirror the client-side
/// <see cref="UdsSecurityLockoutConfig.Default"/> (3 attempts / 5 s) so the
/// end-to-end pairing exhibits both-passes-visible lockout semantics.
/// </summary>
public sealed record SecurityAccessServerConfig
{
    /// <summary>Failed sendKey attempts tolerated per level before lockout.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Lockout delay started when the attempt limit is reached.</summary>
    public TimeSpan LockoutDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>requestSeed behavior while Delayed (spec §6.3 flow item 5).</summary>
    public LockoutSeedBehavior LockoutSeedBehavior { get; init; } = LockoutSeedBehavior.ThirtySixThen37;

    /// <summary>Seed (and expected-key) length in bytes. ISO default 4.</summary>
    public int SeedLength { get; init; } = 4;

    /// <summary>
    /// Supported security level numbers n (requestSeed = 2n−1, sendKey = 2n).
    /// Default: levels 1..2 (sub-functions 0x01..0x04). Others → NRC 0x12.
    /// </summary>
    public IReadOnlyList<byte> SupportedLevels { get; init; } = new byte[] { 1, 2 };
}
