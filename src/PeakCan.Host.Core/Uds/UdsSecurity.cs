namespace PeakCan.Host.Core.Uds;

/// <summary>
/// UDS security access state management. Tracks seed/key exchange,
/// authentication status, and lockout state per security level.
/// </summary>
public sealed class UdsSecurity
{
    private readonly Dictionary<byte, SecurityLevelState> _levels = new();

    /// <summary>
    /// v1.3.0 MINOR Item 1: lockout policy. Defaults to 3 attempts / 5 s.
    /// Settable via <c>UdsClient</c> DI or directly for tests.
    /// </summary>
    public UdsSecurityLockoutConfig LockoutConfig { get; internal set; } =
        UdsSecurityLockoutConfig.Default;

    /// <summary>Get the current seed for a security level.</summary>
    public byte[]? GetSeed(byte level)
    {
        lock (_levels)
        {
            return _levels.TryGetValue(level, out var state) ? state.Seed : null;
        }
    }

    /// <summary>Set the seed received from ECU.</summary>
    public void SetSeed(byte level, byte[] seed)
    {
        lock (_levels)
        {
            _levels[level] = new SecurityLevelState
            {
                Seed = seed,
                IsAuthenticated = false
            };
        }
    }

    /// <summary>Mark a security level as authenticated.</summary>
    public void SetAuthenticated(byte level)
    {
        lock (_levels)
        {
            if (_levels.TryGetValue(level, out var state))
            {
                state.IsAuthenticated = true;
            }
        }
    }

    /// <summary>Check if a security level is authenticated.</summary>
    public bool IsAuthenticated(byte level)
    {
        lock (_levels)
        {
            return _levels.TryGetValue(level, out var state) && state.IsAuthenticated;
        }
    }

    /// <summary>
    /// Clear authentication for all levels (e.g., on session change).
    /// Lockout state is INDEPENDENT of session state — it is preserved
    /// across <see cref="Reset"/> per spec D8.
    /// </summary>
    public void Reset()
    {
        lock (_levels)
        {
            foreach (var state in _levels.Values)
            {
                state.Seed = null;
                state.IsAuthenticated = false;
            }
            // Note: AttemptCount and LockedUntilUtc are intentionally preserved.
            // Security lockout is a policy that should not be bypassed by
            // session transitions (an attacker could otherwise reset their
            // counter by triggering a diagnostic session change).
        }
    }

    /// <summary>
    /// v1.3.0 MINOR Item 1: true if the security level is currently locked
    /// out (host-side enforcement). Lockout state is independent of session
    /// state — it is NOT cleared by <see cref="Reset"/> (D8 from spec).
    /// </summary>
    public bool IsLocked(byte level)
    {
        lock (_levels)
        {
            if (_levels.TryGetValue(level, out var state) && state.LockedUntilUtc is DateTime until)
                return DateTime.UtcNow < until;
            return false;
        }
    }

    /// <summary>
    /// v1.3.0 MINOR Item 1: remaining lockout time. Returns
    /// <see cref="TimeSpan.Zero"/> if not locked.
    /// </summary>
    public TimeSpan RemainingLockoutDelay(byte level)
    {
        lock (_levels)
        {
            if (_levels.TryGetValue(level, out var state) && state.LockedUntilUtc is DateTime until)
            {
                var remaining = until - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// v1.3.0 MINOR Item 1: record a failed authentication attempt.
    /// Increments counter; on reaching <see cref="UdsSecurityLockoutConfig.MaxAttempts"/>
    /// sets <see cref="SecurityLevelState.LockedUntilUtc"/> and resets counter.
    /// </summary>
    internal void RecordFailedAttempt(byte level)
    {
        lock (_levels)
        {
            var state = _levels.TryGetValue(level, out var s) ? s : new SecurityLevelState();
            state.AttemptCount++;
            if (state.AttemptCount >= LockoutConfig.MaxAttempts)
            {
                state.LockedUntilUtc = DateTime.UtcNow + LockoutConfig.LockoutDuration;
                state.AttemptCount = 0;  // reset counter, lockout takes effect
            }
            _levels[level] = state;
        }
    }

    /// <summary>
    /// v1.3.0 MINOR Item 1: clear lockout and attempt counter for a level.
    /// Called by <c>UdsClient.SecurityAccessAsync</c> on successful auth.
    /// Lockout is independent of session state — does NOT clear on session
    /// change (D8 from spec).
    /// </summary>
    public void ResetLockout(byte level)
    {
        lock (_levels)
        {
            if (_levels.TryGetValue(level, out var state))
            {
                state.AttemptCount = 0;
                state.LockedUntilUtc = null;
            }
        }
    }

    private sealed class SecurityLevelState
    {
        public byte[]? Seed { get; set; }
        public bool IsAuthenticated { get; set; }

        // v1.3.0 MINOR Item 1: lockout state
        public int AttemptCount { get; set; }
        public DateTime? LockedUntilUtc { get; set; }
    }
}