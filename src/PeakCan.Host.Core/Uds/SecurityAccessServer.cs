using System.Security.Cryptography;

namespace PeakCan.Host.Core.Uds;

/// <summary>
/// Server-side UDS 0x27 SecurityAccess state machine (spec 2026-09-07 §6.3).
/// <para>
/// Hil-core is frozen (Phase 1–3, spec §11): the package's script-driven
/// <c>EcuStateMachine</c> only carries the simplified XOR-0xAA generators
/// without attempt counting / lockout — so this host-side machine owns the
/// full NRC matrix and is plugged into <c>StatefulVirtualEcu</c> ahead of
/// the script dispatch for SID 0x27 (plus 0x10/0x11 lifecycle hooks).
/// </para>
/// <para>
/// Per spec D5 all timing goes through <see cref="TimeProvider"/> — the
/// end-to-end pairing drives both sides on one virtual clock with zero
/// real waiting. Per-level state is volatile (spec §6.3 boundary: counters
/// cleared on ECU reset; lockout delays persist).
/// </para>
/// </summary>
public sealed class SecurityAccessServer
{
    private const byte SidSecurityAccess = 0x27;
    private const byte NrcSubFunctionNotSupported = 0x12;
    private const byte NrcIncorrectMessageLength = 0x13;
    private const byte NrcRequestSequenceError = 0x24;
    private const byte NrcRequestOutOfRange = 0x31;
    private const byte NrcInvalidKey = 0x35;
    private const byte NrcExceededNumberOfAttempts = 0x36;
    private const byte NrcRequiredTimeDelayNotExpired = 0x37;

    private readonly SecurityAccessServerConfig _config;
    private readonly IKeyDerivationAlgorithm? _keyAlgorithm;
    private readonly TimeProvider _timeProvider;
    private readonly Func<byte[]> _seedGenerator;
    private readonly Dictionary<byte, LevelState> _levels = new();

    /// <summary>Default key derivation mirrors the legacy built-in generator: key[i] = seed[i] ^ 0xAA.</summary>
    private static readonly IKeyDerivationAlgorithm DefaultAlgorithm = new XorAAAlgorithm();

    public SecurityAccessServer(
        SecurityAccessServerConfig? config = null,
        IKeyDerivationAlgorithm? keyAlgorithm = null,
        Func<byte[]>? seedGenerator = null,
        TimeProvider? timeProvider = null)
    {
        _config = config ?? new SecurityAccessServerConfig();
        if (_config.SeedLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "SeedLength must be positive.");
        if (_config.MaxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "MaxAttempts must be positive.");
        _keyAlgorithm = keyAlgorithm;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _seedGenerator = seedGenerator ?? DefaultSeedGenerator;
    }

    /// <summary>ISO 14229-1: RNG non-predictable is sufficient for v1 (spec §6.3).</summary>
    private static byte[] DefaultSeedGenerator()
    {
        var seed = new byte[32];
        RandomNumberGenerator.Fill(seed);
        return seed;
    }

    /// <summary>Whether <paramref name="level"/> is currently authenticated (unlocked).</summary>
    public bool IsAuthenticated(byte level)
        => GetState(level, create: false)?.Authenticated ?? false;

    /// <summary>Whether <paramref name="level"/> is in the Delayed (lockout) state.</summary>
    public bool IsLocked(byte level) => RefreshLocked(GetState(level, create: false));

    /// <summary>
    /// Session-change hook: switching back to the default session re-locks ALL
    /// levels (spec §6.3 boundary). Non-default sessions keep authentication state.
    /// </summary>
    public void OnSessionChanged(bool defaultSession)
    {
        if (!defaultSession) return;
        foreach (var state in _levels.Values)
        {
            state.Authenticated = false;
            state.SeedRequested = false;
        }
    }

    /// <summary>
    /// ECU-reset hook: v1 attempt counters are volatile (spec §6.3 boundary)
    /// and reset implies default session → all levels re-lock. Lockout delays
    /// persist (the documented difference from real ECUs).
    /// </summary>
    public void OnEcuReset()
    {
        foreach (var state in _levels.Values)
        {
            state.Authenticated = false;
            state.SeedRequested = false;
            state.AttemptCount = 0;
        }
    }

    /// <summary>
    /// Handles a raw 0x27 request. Returns the response bytes; an empty array
    /// means "no response" (suppressPositiveResponseBit on a positive outcome).
    /// </summary>
    public byte[] HandleRequest(ReadOnlySpan<byte> request)
    {
        byte[] Neg(byte nrc) => new[] { (byte)0x7F, SidSecurityAccess, nrc };

        if (request.Length < 2 || request[0] != SidSecurityAccess)
            return Neg(NrcIncorrectMessageLength);

        var suppressBit = (request[1] & 0x80) != 0;
        var sub = (byte)(request[1] & 0x7F);
        var isSendKey = (sub & 0x01) == 0;
        var level = isSendKey ? (byte)(sub / 2) : (byte)((sub + 1) / 2);

        var state = GetState(level, create: true)!;

        if (!_config.SupportedLevels.Contains(level))
            return Neg(NrcSubFunctionNotSupported);

        if (isSendKey)
        {
            // Length first — a malformed message trumps sequence/lockout NRCs.
            if (request.Length != 2 + _config.SeedLength)
                return Neg(NrcIncorrectMessageLength);

            // Lockout gate: sendKey during the delay → 0x37 (requiredTimeDelayNotExpired).
            if (RefreshLocked(state))
                return Neg(NrcRequiredTimeDelayNotExpired);

            if (!state.SeedRequested)
                return Neg(NrcRequestSequenceError);

            var expectedKey = (_keyAlgorithm ?? DefaultAlgorithm).ComputeKey(state.CurrentSeed!, sub);
            var receivedKey = request.Slice(2).ToArray();
            if (!CryptographicOperations.FixedTimeEquals(expectedKey, receivedKey))
            {
                state.SeedRequested = false;
                state.AttemptCount++;
                if (state.AttemptCount >= _config.MaxAttempts)
                {
                    // Nth failure → 0x35, enter Delayed and start the lockout
                    // timer (spec §6.3 flow item 1). Counter resets so the next
                    // lockout cycle starts fresh.
                    state.LockoutExpiry = _timeProvider.GetTimestamp()
                        + (long)(_config.LockoutDuration.TotalSeconds * _timeProvider.TimestampFrequency);
                    state.AttemptCount = 0;
                    state.FirstRequestSeedInLockout = true;
                    state.Authenticated = false;
                }
                return Neg(NrcInvalidKey);
            }

            state.Authenticated = true;
            state.SeedRequested = false;
            state.AttemptCount = 0; // spec: 成功解锁清零
            state.CurrentSeed = null;
            return Positive(sub, suppressBit, []);
        }

        // ---- requestSeed (odd sub-function) ----

        // Lockout gate (spec §6.3 flow items 2–4); expiry checked lazily.
        // A locked level never returns the all-zero seed — 0x36/0x37 here.
        if (RefreshLocked(state))
        {
            if (_config.LockoutSeedBehavior == LockoutSeedBehavior.Always37 || !state.FirstRequestSeedInLockout)
                return Neg(NrcRequiredTimeDelayNotExpired);
            state.FirstRequestSeedInLockout = false;
            return Neg(NrcExceededNumberOfAttempts);
        }

        // AccessDataRecord present but not supported (spec §6.3 0x31 example).
        if (request.Length > 2)
            return Neg(NrcRequestOutOfRange);

        // Already authenticated → ISO 14229-1 all-zero seed. The zero seed is
        // also the reference for any subsequent sendKey on this level.
        if (state.Authenticated)
        {
            state.CurrentSeed = new byte[_config.SeedLength];
            state.SeedRequested = true;
            return Positive(sub, suppressBit, state.CurrentSeed);
        }

        // Repeated requestSeed returns the same seed until the key is sent
        // (ISO 14229-1) or the level is unlocked/reset.
        state.CurrentSeed ??= TruncateSeed(_seedGenerator());
        state.SeedRequested = true;
        return Positive(sub, suppressBit, state.CurrentSeed);
    }

    private byte[] Positive(byte sub, bool suppressBit, byte[] payload)
    {
        if (suppressBit) return [];
        var rsp = new byte[2 + payload.Length];
        rsp[0] = 0x67;
        rsp[1] = sub;
        payload.CopyTo(rsp, 2);
        return rsp;
    }

    private byte[] TruncateSeed(byte[] generated)
    {
        if (generated.Length == _config.SeedLength) return generated;
        if (generated.Length < _config.SeedLength)
            throw new InvalidOperationException(
                $"Seed generator produced {generated.Length} bytes, config requires {_config.SeedLength}.");
        return generated[.._config.SeedLength];
    }

    private LevelState? GetState(byte level, bool create)
    {
        if (_levels.TryGetValue(level, out var state)) return state;
        if (!create) return null;
        state = new LevelState();
        _levels[level] = state;
        return state;
    }

    /// <summary>Lazy expiry: a Delayed level whose timer elapsed returns to normal (flow item 4).</summary>
    private bool RefreshLocked(LevelState? state)
    {
        if (state is null || state.LockoutExpiry == 0) return false;
        if (_timeProvider.GetTimestamp() < state.LockoutExpiry) return true;
        state.LockoutExpiry = 0;
        state.AttemptCount = 0;
        state.FirstRequestSeedInLockout = false;
        return false;
    }

    private sealed class LevelState
    {
        public bool Authenticated;
        public bool SeedRequested;
        public int AttemptCount;
        public byte[]? CurrentSeed;
        public long LockoutExpiry; // provider timestamp; 0 = not locked
        public bool FirstRequestSeedInLockout;
    }

    private sealed class XorAAAlgorithm : IKeyDerivationAlgorithm
    {
        public byte[] ComputeKey(byte[] seed, byte securityLevel)
            => seed.Select(b => (byte)(b ^ 0xAA)).ToArray();
    }
}
