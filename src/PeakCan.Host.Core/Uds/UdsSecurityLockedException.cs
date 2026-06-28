using System;

namespace PeakCan.Host.Core.Uds;

/// <summary>
/// v1.3.0 MINOR Item 1: thrown by <c>UdsClient.SecurityAccessAsync</c> when
/// the requested security level is currently locked out (host-side
/// enforcement, before wire emit). Lockout is a per-level security policy
/// — see <see cref="UdsSecurity"/>.
/// </summary>
public sealed class UdsSecurityLockedException : UdsException
{
    /// <summary>Security level that is locked.</summary>
    public byte SecurityLevel { get; }

    /// <summary>Time remaining on the lockout window.</summary>
    public TimeSpan RemainingDelay { get; }

    public UdsSecurityLockedException(byte level, TimeSpan remaining)
        : base($"Security level 0x{level:X2} is locked; retry after {remaining.TotalSeconds:F1}s")
    {
        SecurityLevel = level;
        RemainingDelay = remaining;
    }
}
