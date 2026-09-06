namespace PeakCan.Host.Core.Uds;

/// <summary>
/// v1.3.0 MINOR Item 1: lockout policy for UDS SecurityAccess (ISO 14229-1
/// §8.4). After <see cref="MaxAttempts"/> failed authentications, the
/// security level is locked for <see cref="LockoutDuration"/>. Per-level
/// scope (D4 from spec).
/// </summary>
public sealed record UdsSecurityLockoutConfig(int MaxAttempts, TimeSpan LockoutDuration)
{
    /// <summary>Default policy: 3 attempts / 5 s lockout.</summary>
    public static UdsSecurityLockoutConfig Default { get; } =
        new(MaxAttempts: 3, LockoutDuration: TimeSpan.FromSeconds(5));
}