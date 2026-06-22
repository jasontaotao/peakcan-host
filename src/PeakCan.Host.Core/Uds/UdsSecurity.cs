namespace PeakCan.Host.Core.Uds;

/// <summary>
/// UDS security access state management. Tracks seed/key exchange
/// and authentication status for each security level.
/// </summary>
public sealed class UdsSecurity
{
    private readonly Dictionary<byte, SecurityLevelState> _levels = new();

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

    /// <summary>Clear authentication for all levels (e.g., on session change).</summary>
    public void Reset()
    {
        lock (_levels)
        {
            _levels.Clear();
        }
    }

    private sealed class SecurityLevelState
    {
        public byte[]? Seed { get; init; }
        public bool IsAuthenticated { get; set; }
    }
}
