using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Uds.IsoTp;

namespace PeakCan.Host.Core.Uds;

/// <summary>
/// UDS (Unified Diagnostic Services) client implementing ISO 14229.
/// Provides high-level API for diagnostic operations.
/// <para>
/// <b>Thread-safety:</b> This class is thread-safe. Requests are
/// serialized internally to prevent overlapping request/response pairs.
/// </para>
/// </summary>
public class UdsClient : IDisposable
{
    private readonly IsoTpLayer _isoTp;
    private readonly UdsTimer _timer;
    private readonly SemaphoreSlim _requestLock = new(1, 1);

    // Response correlation. v1.2.12 Item 14: all accesses go through
    // Volatile.Read / Volatile.Write so the OnMessageReceived callback
    // thread always observes the most recent value written by the
    // request thread — without the fence the JIT may hoist or cache the
    // read across threads, leading to lost wake-ups or mismatched
    // response correlation. (The field is not declared `volatile` to
    // avoid the C# CS0420 warning "a reference to a volatile field will
    // not be treated as volatile" when the explicit Volatile.Read/Write
    // APIs are used; either alone is sufficient, both is redundant and
    // triggers the diagnostic.)
    private TaskCompletionSource<byte[]>? _responseTcs;
    private CancellationTokenSource? _responseCts;

    /// <summary>
    /// v1.2.13 PATCH Item 4: test-visible hook fired when P2 timeout
    /// auto-cancels the in-flight response TCS. Production code never
    /// reads this; tests use it to assert the timeout fired without
    /// waiting the full P2 ms in the test wall-clock.
    /// </summary>
    internal Action? OnP2TimeoutFiredForTesting { get; set; }

    // C-8 fix: the SID of the in-flight request, used to validate that an
    // incoming positive response (SID+0x40) actually echoes our SID. Without
    // this guard, stale or out-of-sequence frames are accepted as the result.
    private byte _pendingRequestSid;

    // v1.1.0: OEM-specific SecurityAccess key derivation. Nullable so the
    // legacy 2-arg ctor keeps working for tests that don't care about
    // SecurityAccess. The new overload SecurityAccessAsync(byte, CancellationToken)
    // throws InvalidOperationException when this is null.
    private readonly IKeyDerivationAlgorithm? _keyAlgorithm;

    /// <summary>Current diagnostic session.</summary>
    public UdsSession Session { get; }

    /// <summary>Security access state.</summary>
    public UdsSecurity Security { get; }

    /// <summary>Create a new UDS client.</summary>
    /// <param name="isoTp">ISO-TP transport layer.</param>
    /// <param name="timer">UDS timer for timeout management.</param>
    /// <param name="sessionLogger">
    /// Optional logger threaded into <see cref="UdsSession"/> so S3
    /// keepalive failures surface in production (v1.2.13 PATCH Item 2).
    /// Defaults to <c>null</c> for backward compatibility with v1.2.x
    /// callers.
    /// </param>
    public UdsClient(IsoTpLayer isoTp, UdsTimer? timer = null, ILogger<UdsSession>? sessionLogger = null)
    {
        ArgumentNullException.ThrowIfNull(isoTp);

        _isoTp = isoTp;
        _timer = timer ?? new UdsTimer();
        Session = new UdsSession(sessionLogger);
        Security = new UdsSecurity();

        // Subscribe to ISO-TP messages
        _isoTp.MessageReceived += OnMessageReceived;
    }

    /// <summary>
    /// Create a new UDS client with an OEM-specific key derivation algorithm
    /// for SecurityAccess (0x27). Added in v1.1.0.
    /// </summary>
    /// <param name="isoTp">ISO-TP transport layer.</param>
    /// <param name="keyAlgorithm">OEM key algorithm. Must not be null.</param>
    /// <param name="timer">Optional UDS timer. Defaults to a fresh <see cref="UdsTimer"/>.</param>
    /// <param name="sessionLogger">
    /// Optional logger threaded into <see cref="UdsSession"/> so S3
    /// keepalive failures surface in production (v1.2.13 PATCH Item 2).
    /// Defaults to <c>null</c> for backward compatibility with v1.2.x
    /// callers.
    /// </param>
    public UdsClient(IsoTpLayer isoTp, IKeyDerivationAlgorithm keyAlgorithm, UdsTimer? timer = null, ILogger<UdsSession>? sessionLogger = null)
    {
        ArgumentNullException.ThrowIfNull(isoTp);
        ArgumentNullException.ThrowIfNull(keyAlgorithm);

        _isoTp = isoTp;
        _keyAlgorithm = keyAlgorithm;
        _timer = timer ?? new UdsTimer();
        Session = new UdsSession(sessionLogger);
        Security = new UdsSecurity();

        // Subscribe to ISO-TP messages
        _isoTp.MessageReceived += OnMessageReceived;
    }

    /// <summary>
    /// v1.3.0 MINOR Item 5: create a new UDS client with an OEM-specific
    /// key derivation algorithm AND a custom SecurityAccess lockout policy.
    /// <para>
    /// Existing ctors keep the default <see cref="UdsSecurityLockoutConfig.Default"/>
    /// (3 attempts / 5 s). This overload lets <c>AppHostBuilder</c> thread
    /// an OEM-overridable policy through DI without changing the lockout
    /// state-machine semantics.
    /// </para>
    /// </summary>
    /// <param name="isoTp">ISO-TP transport layer.</param>
    /// <param name="keyAlgorithm">OEM key algorithm. Must not be null.</param>
    /// <param name="lockoutConfig">
    /// Lockout policy applied to <see cref="Security"/>'s
    /// <see cref="UdsSecurity.LockoutConfig"/> post-construction.
    /// Must not be null.
    /// </param>
    /// <param name="timer">Optional UDS timer. Defaults to a fresh <see cref="UdsTimer"/>.</param>
    /// <param name="sessionLogger">
    /// Optional logger threaded into <see cref="UdsSession"/>. Defaults
    /// to <c>null</c> for backward compatibility with v1.2.x callers.
    /// </param>
    public UdsClient(IsoTpLayer isoTp, IKeyDerivationAlgorithm keyAlgorithm, UdsSecurityLockoutConfig lockoutConfig, UdsTimer? timer = null, ILogger<UdsSession>? sessionLogger = null)
        : this(isoTp, keyAlgorithm, timer, sessionLogger)
    {
        ArgumentNullException.ThrowIfNull(lockoutConfig);
        Security.LockoutConfig = lockoutConfig;
    }

    /// <summary>
    /// Send a UDS service request and wait for response.
    /// </summary>
    /// <param name="serviceId">Service ID (SID).</param>
    /// <param name="data">Service data (excluding SID).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Response bytes (excluding SID + 0x40).</returns>
    /// <remarks>
    /// v1.2.14 PATCH Item 4: marked <c>virtual</c> so test doubles can
    /// intercept wire-level frame emit without subclassing the entire
    /// <see cref="UdsClient"/>. Visibility stays <c>public</c> for
    /// backwards compatibility with existing direct callers
    /// (e.g. <c>UdsClientTests</c>).
    /// </remarks>
    public virtual async Task<byte[]> SendRequestAsync(byte serviceId, byte[]? data = null, CancellationToken ct = default)
    {
        // Build request: SID + data
        byte[] request;
        if (data is null)
        {
            request = [serviceId];
        }
        else
        {
            request = new byte[1 + data.Length];
            request[0] = serviceId;
            Array.Copy(data, 0, request, 1, data.Length);
        }

        // Serialize requests to prevent overlapping
        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SendRequestInternalAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>
    /// DiagnosticSessionControl (0x10).
    /// </summary>
    /// <param name="sessionType">Session type (1=Default, 2=Extended, 3=Programming).</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync(byte sessionType, CancellationToken ct = default)
    {
        var response = await SendRequestAsync(0x10, new byte[] { sessionType }, ct).ConfigureAwait(false);

        // Parse response: [sessionType, P2high, P2low, P2*high, P2*low]
        if (response.Length < 5)
            throw new UdsException("Invalid DiagnosticSessionControl response");

        var result = new DiagnosticSessionResponse
        {
            SessionType = response[0],
            P2 = (response[1] << 8) | response[2],
            P2Star = (response[3] << 8) | response[4]
        };

        Session.SetSession(result.SessionType, result.P2, result.P2Star);

        // C-3 fix: propagate negotiated timings to UdsTimer so subsequent
        // requests honour the ECU's P2 / P2* (e.g. longer P2* in Programming
        // session). Without this, SendRequestInternalAsync would always use
        // the 50 ms default and time out on the first diagnostic request.
        _timer.P2Timeout = TimeSpan.FromMilliseconds(result.P2);
        _timer.P2StarTimeout = TimeSpan.FromMilliseconds(result.P2Star);

        return result;
    }

    /// <summary>
    /// ECUReset (0x11).
    /// </summary>
    /// <param name="resetType">Reset type (1=Hard, 2=KeyOff, 3=Soft).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// v1.3.0 MINOR Item 2: marked <c>virtual</c> for consistency with 7
    /// sibling UDS methods. Tests can override to intercept wire emit.
    /// Defensive length check on <c>response[0]</c> prevents
    /// <see cref="IndexOutOfRangeException"/> if <see cref="SendRequestAsync"/>
    /// returns an empty payload.
    /// </remarks>
    public virtual async Task<byte> EcuResetAsync(byte resetType, CancellationToken ct = default)
    {
        var response = await SendRequestAsync(0x11, new byte[] { resetType }, ct).ConfigureAwait(false);
        return response.Length > 0 ? response[0] : (byte)0;
    }

    /// <summary>
    /// v1.3.0 MINOR Item 2/4: type-safe enum overload.
    /// </summary>
    /// <param name="resetType">ISO 14229-1 §10.2 standard reset sub-function.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The echoed sub-function byte from the positive response.</returns>
    public Task<byte> EcuResetAsync(UdsResetType resetType, CancellationToken ct = default)
        => EcuResetAsync((byte)resetType, ct);

    /// <summary>
    /// ReadDataByIdentifier (0x22).
    /// </summary>
    /// <param name="did">Data Identifier (2 bytes).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>DID data bytes.</returns>
    public virtual async Task<byte[]> ReadDataByIdentifierAsync(ushort did, CancellationToken ct = default)
    {
        var didBytes = new byte[] { (byte)(did >> 8), (byte)(did & 0xFF) };
        var response = await SendRequestAsync(0x22, didBytes, ct).ConfigureAwait(false);

        // Response: [DIDhigh, DIDlow, data...]
        if (response.Length < 3)
            throw new UdsException("Invalid ReadDataByIdentifier response");

        return response[2..];
    }

    /// <summary>
    /// WriteDataByIdentifier (0x2E).
    /// </summary>
    /// <param name="did">Data Identifier (2 bytes).</param>
    /// <param name="data">Data to write.</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task WriteDataByIdentifierAsync(ushort did, byte[] data, CancellationToken ct = default)
    {
        var request = new byte[2 + data.Length];
        request[0] = (byte)(did >> 8);
        request[1] = (byte)(did & 0xFF);
        Array.Copy(data, 0, request, 2, data.Length);

        await SendRequestAsync(0x2E, request, ct).ConfigureAwait(false);
    }

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
        // Lockout state is independent of session state — even if a session
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
            // authentication policy violations — they are flow-control
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
    /// the wire. This is defensive coding — the 3-arg overload's entry
    /// check (called transitively for the RequestSeed leg) already
    /// provides this; the explicit check makes the intent visible at
    /// the 2-arg signature boundary.
    /// <para>
    /// <b>Mid-handshake lockout race (TOCTOU window):</b> between the
    /// RequestSeed leg completing and the SendKey leg starting, a
    /// concurrent caller may exhaust the lockout counter on the same
    /// level. In that case, the SendKey leg's entry check
    /// (<see cref="UdsSecurityLockedException"/>) fires from inside this
    /// 2-arg call. This is intentional behavior — the entry check at the
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

        // SECURITY: never log seed bytes — see commit a9fe443 (C-2 fix).
        byte[] key = _keyAlgorithm.ComputeKey(seed, requestLevel);

        // SendKey leg via the existing 3-arg method. If a concurrent
        // caller triggers lockout between the RequestSeed and SendKey
        // legs, the SendKey leg's entry check throws
        // UdsSecurityLockedException — see <remarks> above.
        return await SecurityAccessAsync(requestLevel, key, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// TesterPresent (0x3E).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// v1.2.14 PATCH Item 4: virtual seam so end-to-end test doubles can
    /// intercept wire-level CAN frame emit via the override of
    /// <see cref="SendRequestAsync"/>. S3 keepalive tests in
    /// <c>UdsSessionTests</c> previously relied on the same seam — this
    /// method was the undeclared one they couldn't override.
    /// </remarks>
    public virtual async Task TesterPresentAsync(CancellationToken ct = default)
    {
        await SendRequestAsync(0x3E, [0x00], ct).ConfigureAwait(false);
        Session.ResetS3Timer();
    }

    /// <summary>
    /// RoutineControl (0x31).
    /// </summary>
    /// <param name="routineControlType">Type (1=Start, 2=Stop, 3=QueryResult).</param>
    /// <param name="routineId">Routine ID (2 bytes).</param>
    /// <param name="data">Optional routine data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Routine result bytes.</returns>
    public virtual async Task<byte[]> RoutineControlAsync(byte routineControlType, ushort routineId, byte[]? data = null, CancellationToken ct = default)
    {
        var requestData = new byte[3 + (data?.Length ?? 0)];
        requestData[0] = routineControlType;
        requestData[1] = (byte)(routineId >> 8);
        requestData[2] = (byte)(routineId & 0xFF);
        if (data is not null)
            Array.Copy(data, 0, requestData, 3, data.Length);

        var response = await SendRequestAsync(0x31, requestData, ct).ConfigureAwait(false);

        // Response: [routineControlType, routineIdhigh, routineIdlow, result...]
        if (response.Length < 3)
            throw new UdsException("Invalid RoutineControl response");

        return response[3..];
    }

    /// <summary>
    /// v1.3.0 MINOR Item 3/4: type-safe enum overload.
    /// </summary>
    /// <param name="routineControlType">ISO 14229-1 §10.4 standard sub-function.</param>
    /// <param name="routineId">Routine identifier (2 bytes).</param>
    /// <param name="data">Optional routine data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Routine result bytes (after the [sub, routineIdHigh, routineIdLow] prefix).</returns>
    public Task<byte[]> RoutineControlAsync(
        RoutineControlType routineControlType, ushort routineId,
        byte[]? data = null, CancellationToken ct = default)
        => RoutineControlAsync((byte)routineControlType, routineId, data, ct);

    /// <summary>
    /// RequestDownload (0x34).
    /// </summary>
    /// <param name="address">Memory address.</param>
    /// <param name="length">Data length.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Block length for TransferData.</returns>
    public async Task<int> RequestDownloadAsync(uint address, uint length, CancellationToken ct = default)
    {
        // Format: [dataFormatId, addressAndLengthFormatId, address..., length...]
        // Simplified: 4-byte address, 4-byte length
        var requestData = new byte[10];
        requestData[0] = 0x00; // No compression, no encryption
        requestData[1] = 0x44; // 4-byte address, 4-byte length
        requestData[2] = (byte)(address >> 24);
        requestData[3] = (byte)((address >> 16) & 0xFF);
        requestData[4] = (byte)((address >> 8) & 0xFF);
        requestData[5] = (byte)(address & 0xFF);
        requestData[6] = (byte)(length >> 24);
        requestData[7] = (byte)((length >> 16) & 0xFF);
        requestData[8] = (byte)((length >> 8) & 0xFF);
        requestData[9] = (byte)(length & 0xFF);

        var response = await SendRequestAsync(0x34, requestData, ct).ConfigureAwait(false);

        // C-7 fix: response layout per ISO 14229-1 §10.6.2.4 is
        //   [dataFormatId, lengthFormatId, maxNumberOfBlockLength (lengthFormatId.lowNibble bytes)]
        // SendRequestAsync strips the SID, so response[0] is dataFormatId,
        // response[1] is lengthFormatId, and response[2..5] are the 4-byte
        // maxNumberOfBlockLength (the common case, low nibble = 4).
        if (response.Length < 5)
            throw new UdsException(
                $"Invalid RequestDownload response: length {response.Length} < 5");

        // Parse max block length (simplified: assume 4-byte)
        int blockLength = (response[1] << 24) | (response[2] << 16) | (response[3] << 8) | response[4];
        return blockLength;
    }

    /// <summary>
    /// TransferData (0x36).
    /// </summary>
    /// <param name="blockSequenceCounter">Block sequence counter (1-255).</param>
    /// <param name="data">Data to transfer.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task TransferDataAsync(byte blockSequenceCounter, byte[] data, CancellationToken ct = default)
    {
        var requestData = new byte[1 + data.Length];
        requestData[0] = blockSequenceCounter;
        Array.Copy(data, 0, requestData, 1, data.Length);

        await SendRequestAsync(0x36, requestData, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// RequestTransferExit (0x37).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task RequestTransferExitAsync(CancellationToken ct = default)
    {
        await SendRequestAsync(0x37, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ReadDTCInformation (0x19).
    /// </summary>
    /// <param name="subFunction">Sub-function (e.g., 0x02 = ReadDTCByStatusMask).</param>
    /// <param name="mask">DTC status mask.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>DTC data bytes.</returns>
    public virtual async Task<byte[]> ReadDtcInformationAsync(byte subFunction, byte mask = 0xFF, CancellationToken ct = default)
    {
        var response = await SendRequestAsync(0x19, [subFunction, mask], ct).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// ClearDiagnosticInformation (0x14).
    /// </summary>
    /// <param name="groupOfDtc">DTC group (0xFFFFFF = all).</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task ClearDiagnosticInformationAsync(uint groupOfDtc = 0xFFFFFF, CancellationToken ct = default)
    {
        var requestData = new byte[3];
        requestData[0] = (byte)(groupOfDtc >> 16);
        requestData[1] = (byte)((groupOfDtc >> 8) & 0xFF);
        requestData[2] = (byte)(groupOfDtc & 0xFF);

        await SendRequestAsync(0x14, requestData, ct).ConfigureAwait(false);
    }

    /// <summary>Start automatic TesterPresent.</summary>
    public void StartTesterPresent(TimeSpan? interval = null)
    {
        Session.StartS3KeepAlive(this, interval);
    }

    /// <summary>Stop automatic TesterPresent.</summary>
    public void StopTesterPresent()
    {
        Session.StopS3KeepAlive();
    }

    public void Dispose()
    {
        _isoTp.MessageReceived -= OnMessageReceived;
        _requestLock.Dispose();
        Volatile.Read(ref _responseCts)?.Dispose();
        Session.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<byte[]> SendRequestInternalAsync(byte[] request, CancellationToken ct)
    {
        _pendingRequestSid = request[0];
        Volatile.Write(ref _responseTcs, new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously));
        Volatile.Write(ref _responseCts, CancellationTokenSource.CreateLinkedTokenSource(ct));

        // v1.2.13 PATCH Item 4: register a callback so P2 timeout unblocks
        // await _responseTcs.Task. Without this registration the linked CTS
        // cancel only fires OperationCanceledException for whoever awaits the
        // token directly — and nothing does. P2 timeout would silently hang
        // the caller. The callback TrySetCancels the TCS so the await resumes
        // with TaskCanceledException → caught below → rethrown as UdsException.
        var registration = _responseCts.Token.Register(
            static state => ((UdsClient)state!).OnP2TimeoutFired(), this);

        // Register timeout
        _responseCts.CancelAfter(_timer.P2Timeout);

        try
        {
            // Send via ISO-TP
            await _isoTp.SendMessageAsync(request, ct).ConfigureAwait(false);

            // Wait for response
            var response = await _responseTcs.Task.ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UdsException("UDS response timeout");
        }
        finally
        {
            // v1.2.13 PATCH Item 4 (Phase 2.5 new finding): strict ordering
            // matters here. Disposal sequence:
            //   1. registration.Dispose()  — unhook the Token.Register callback
            //                                so it cannot fire during/after Dispose
            //   2. Volatile.Write(_responseTcs, null) — OnMessageReceived sees no TCS
            //   3. Volatile.Write(_responseCts, null) — OnMessageReceived sees no CTS
            //   4. cts?.Dispose()           — last; no in-flight reference remains
            // Without this ordering OnMessageReceived may cts?.CancelAfter on a
            // disposed CTS (ObjectDisposedException propagates onto the SDK
            // read thread — process crash on graceful shutdown).
            registration.Dispose();
            var cts = Volatile.Read(ref _responseCts);
            Volatile.Write(ref _responseTcs, null);
            Volatile.Write(ref _responseCts, null);
            cts?.Dispose();
        }
    }

    private void OnP2TimeoutFired()
    {
        OnP2TimeoutFiredForTesting?.Invoke();
        Volatile.Read(ref _responseTcs)?.TrySetCanceled();
    }

    private void OnMessageReceived(byte[] data)
    {
        if (data.Length < 1)
            return;

        byte sid = data[0];

        // Item 14: acquire-load the pending response handles. Without
        // Volatile.Read the JIT may have cached or hoisted the read.
        var tcs = Volatile.Read(ref _responseTcs);
        var cts = Volatile.Read(ref _responseCts);

        // Check for negative response (0x7F)
        if (sid == 0x7F && data.Length >= 3)
        {
            byte requestedSid = data[1];
            byte nrc = data[2];

            // Handle NRC 0x78 (requestCorrectlyReceivedResponsePending)
            if (nrc == 0x78)
            {
                // v1.2.13 PATCH Item 4 (Phase 2.5): guard against disposed
                // CTS. After SendRequestInternalAsync's finally has nulled
                // the fields and disposed cts, a late-arriving response
                // (already in flight on the SDK read thread) would crash
                // here. The IsCancellationRequested check is the cheap
                // fast-path; the try/catch handles the disposed-after-read
                // race window.
                if (cts is not null && !cts.IsCancellationRequested)
                {
                    try { cts.CancelAfter(_timer.P2StarTimeout); }
                    catch (ObjectDisposedException) { /* shutdown race */ }
                }
                return;
            }

            // Other NRCs: complete with error
            tcs?.TrySetException(new UdsNegativeResponseException(requestedSid, (UdsNegativeResponseCode)nrc));
            return;
        }

        // Positive response: SID + 0x40
        if (data.Length >= 2)
        {
            // C-8 fix: validate the SID echoes our in-flight request's SID + 0x40.
            // A misaligned SID means the frame is stale or from a different
            // request; dropping it lets the P2 timer expire (semantically correct).
            byte expectedPositiveSid = (byte)(_pendingRequestSid + 0x40);
            if (sid != expectedPositiveSid)
                return;

            tcs?.TrySetResult(data[1..]);
        }
    }

    /// <summary>
    /// v1.2.13 PATCH Item 4: test-only public surface for OnMessageReceived.
    /// Allows tests to drive late-arriving-response paths without standing
    /// up an IsoTpLayer + ICanChannel.
    /// </summary>
    internal void PublicOnMessageReceivedForTesting(byte[] data) => OnMessageReceived(data);
}

/// <summary>DiagnosticSessionControl response.</summary>
public sealed record DiagnosticSessionResponse
{
    public byte SessionType { get; init; }
    public int P2 { get; init; }
    public int P2Star { get; init; }
}
