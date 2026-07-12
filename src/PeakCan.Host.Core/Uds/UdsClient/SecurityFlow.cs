namespace PeakCan.Host.Core.Uds;

public partial class UdsClient
{
    // Flow D: SecurityAccess (0x27) x 2 overloads.
    // 3-arg overload (level + key + ct): RequestSeed/SendKey + lockout counter
    // (v1.3.0 MINOR Item 1 + v1.3.1 PATCH Item 1). 2-arg overload (level + ct):
    // full RequestSeed -> ComputeKey -> SendKey handshake via injected
    // IKeyDerivationAlgorithm (v1.1.0 + v1.3.1 PATCH Item 2 fail-fast).
    // Extracted from UdsClient.cs verbatim per W12 D5.

    /// <summary>
    /// SecurityAccess (0x27).
    /// </summary>
    /// <param name="level">Security level (1=RequestSeed, 3=RequestSeed, ...).</param>
    /// <param name="key">Security key (for SendKey).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Seed bytes (for RequestSeed) or success (for SendKey).</returns>
    public virtual async Task<byte[]> SecurityAccessAsync(byte level, byte[]? key = null, CancellationToken ct = default)
    {
        // v1.3.0 MINOR Item 1: lockout check before wire emit.
        // Lockout state is independent of session state - even if a session
        // reset was attempted, the lockout window persists (D8).
        if (Security.IsLocked(level))
            throw new UdsSecurityLockedException(level, Security.RemainingLockoutDelay(level));

        byte[] requestData;
        byte subFunction;

        if (key is null)
        {
            // RequestSeed
            subFunction = level;
            requestData = [subFunction];
        }
        else
        {
            // SendKey
            subFunction = (byte)(level + 1);
            requestData = new byte[1 + key.Length];
            requestData[0] = subFunction;
            Array.Copy(key, 0, requestData, 1, key.Length);
        }

        try
        {
            var response = await SendRequestAsync(0x27, requestData, ct).ConfigureAwait(false);

            if (key is null)
            {
                // Seed response: [level, seed...]
                Security.SetSeed(level, response[1..]);
                return response[1..];
            }
            else
            {
                // Key response: success (empty or level)
                Security.SetAuthenticated(level);
                Security.ResetLockout(level);  // v1.3.0 MINOR Item 1: clear on successful auth
                return response;
            }
        }
        catch (UdsNegativeResponseException nrc)
            when (key is not null
                  && ((byte)nrc.ResponseCode == 0x35
                      || (byte)nrc.ResponseCode == 0x36
                      || (byte)nrc.ResponseCode == 0x37))
        {
            // v1.3.1 PATCH Item 1: lockout counter only counts SendKey
            // (key is not null) failures. RequestSeed failures are not
            // authentication policy violations - they are flow-control
            // signals (e.g. ECU not in Programming session, conditions
            // not correct for SecurityAccess). Recording them as host-side
            // auth failures would let a benign NRC 0x22 trip lockout.
            Security.RecordFailedAttempt(level);
            throw;
        }
    }

    /// <summary>
    /// SecurityAccess (0x27) using the injected <see cref="IKeyDerivationAlgorithm"/>.
    /// Performs the full handshake: RequestSeed → ComputeKey → SendKey.
    /// </summary>
    /// <param name="requestLevel">Security level sub-function byte (0x01, 0x03, ...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success response bytes from the ECU after SendKey.</returns>
    /// <exception cref="InvalidOperationException">
    ///   The client was constructed via the legacy 2-arg ctor that does not
    ///   take an <see cref="IKeyDerivationAlgorithm"/>.
    /// </exception>
    /// <exception cref="KeyAlgorithmNotConfiguredException">
    ///   The injected algorithm's placeholder has not been replaced with an
    ///   OEM-specific implementation.
    /// </exception>
    /// <exception cref="UdsSecurityLockedException">
    ///   The level is locked (either already-locked at entry, or
    ///   mid-handshake lockout triggered by a concurrent
    ///   <c>SecurityAccessAsync</c> call exhausting the failure counter
    ///   between RequestSeed and SendKey legs).
    /// </exception>
    /// <remarks>
    /// v1.3.1 PATCH Item 2: the 2-arg overload adds an explicit pre-check
    /// at entry to fail-fast on already-locked levels without touching
    /// the wire. This is defensive coding - the 3-arg overload's entry
    /// check (called transitively for the RequestSeed leg) already
    /// provides this; the explicit check makes the intent visible at the
    /// 2-arg signature boundary.
    /// <para>
    /// <b>Mid-handshake lockout race (TOCTOU window):</b> between the
    /// RequestSeed leg completing and the SendKey leg starting, a
    /// concurrent caller may exhaust the lockout counter on the same
    /// level. In that case, the SendKey leg's entry check
    /// (<see cref="UdsSecurityLockedException"/>) fires from inside this
    /// 2-arg call. This is intentional behavior - the entry check at the
    /// 3-arg SendKey call is the source of truth for lockout state. The
    /// 2-arg overload surfaces the same exception type with the actual
    /// remaining delay; callers should treat the handshake as failed
    /// and wait for the lockout window to expire before retrying.
    /// </para>
    /// </remarks>
    public virtual async Task<byte[]> SecurityAccessAsync(byte requestLevel, CancellationToken ct = default)
    {
        if (_keyAlgorithm is null)
            throw new InvalidOperationException(
                "UdsClient was constructed without an IKeyDerivationAlgorithm. " +
                "Use the (IsoTpLayer, IKeyDerivationAlgorithm, UdsTimer?) constructor " +
                "or call SecurityAccessAsync(byte level, byte[] key, CancellationToken) directly.");

        // v1.3.1 PATCH Item 2: fail-fast pre-check. The 3-arg overload's
        // entry check (transitive via RequestSeed leg below) would also
        // catch this; the explicit check makes the intent visible at the
        // 2-arg signature boundary and avoids wire-allocate for the
        // RequestSeed frame when the level is already locked.
        if (Security.IsLocked(requestLevel))
            throw new UdsSecurityLockedException(requestLevel, Security.RemainingLockoutDelay(requestLevel));

        // RequestSeed leg via the existing 3-arg method (key=null returns seed bytes).
        byte[] seed = await SecurityAccessAsync(requestLevel, key: null, ct).ConfigureAwait(false);

        // SECURITY: never log seed bytes - see commit a9fe443 (C-2 fix).
        byte[] key = _keyAlgorithm.ComputeKey(seed, requestLevel);

        // SendKey leg via the existing 3-arg method. If a concurrent
        // caller triggers lockout between the RequestSeed and SendKey
        // legs, the SendKey leg's entry check throws
        // UdsSecurityLockedException - see <remarks> above.
        return await SecurityAccessAsync(requestLevel, key, ct).ConfigureAwait(false);
    }
}
