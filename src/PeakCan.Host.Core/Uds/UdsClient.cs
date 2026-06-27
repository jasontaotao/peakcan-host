using System.Collections.Concurrent;
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
    public UdsSession Session { get; } = new();

    /// <summary>Security access state.</summary>
    public UdsSecurity Security { get; }

    /// <summary>Create a new UDS client.</summary>
    /// <param name="isoTp">ISO-TP transport layer.</param>
    /// <param name="timer">UDS timer for timeout management.</param>
    public UdsClient(IsoTpLayer isoTp, UdsTimer? timer = null)
    {
        ArgumentNullException.ThrowIfNull(isoTp);

        _isoTp = isoTp;
        _timer = timer ?? new UdsTimer();
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
    public UdsClient(IsoTpLayer isoTp, IKeyDerivationAlgorithm keyAlgorithm, UdsTimer? timer = null)
    {
        ArgumentNullException.ThrowIfNull(isoTp);
        ArgumentNullException.ThrowIfNull(keyAlgorithm);

        _isoTp = isoTp;
        _keyAlgorithm = keyAlgorithm;
        _timer = timer ?? new UdsTimer();
        Security = new UdsSecurity();

        // Subscribe to ISO-TP messages
        _isoTp.MessageReceived += OnMessageReceived;
    }

    /// <summary>
    /// Send a UDS service request and wait for response.
    /// </summary>
    /// <param name="serviceId">Service ID (SID).</param>
    /// <param name="data">Service data (excluding SID).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Response bytes (excluding SID + 0x40).</returns>
    public async Task<byte[]> SendRequestAsync(byte serviceId, byte[]? data = null, CancellationToken ct = default)
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
    public async Task<byte> EcuResetAsync(byte resetType, CancellationToken ct = default)
    {
        var response = await SendRequestAsync(0x11, new byte[] { resetType }, ct).ConfigureAwait(false);
        return response[0];
    }

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
            return response;
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
    public virtual async Task<byte[]> SecurityAccessAsync(byte requestLevel, CancellationToken ct = default)
    {
        if (_keyAlgorithm is null)
            throw new InvalidOperationException(
                "UdsClient was constructed without an IKeyDerivationAlgorithm. " +
                "Use the (IsoTpLayer, IKeyDerivationAlgorithm, UdsTimer?) constructor " +
                "or call SecurityAccessAsync(byte level, byte[] key, CancellationToken) directly.");

        // RequestSeed leg via the existing 3-arg method (key=null returns seed bytes).
        byte[] seed = await SecurityAccessAsync(requestLevel, key: null, ct).ConfigureAwait(false);

        // SECURITY: never log seed bytes — see commit a9fe443 (C-2 fix).
        byte[] key = _keyAlgorithm.ComputeKey(seed, requestLevel);

        // SendKey leg via the existing 3-arg method.
        return await SecurityAccessAsync(requestLevel, key, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// TesterPresent (0x3E).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task TesterPresentAsync(CancellationToken ct = default)
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
            // Item 14: Volatile.Write releases the null assignment to all
            // reader threads (e.g. OnMessageReceived callback), even on
            // weak memory models.
            var cts = Volatile.Read(ref _responseCts);
            Volatile.Write(ref _responseCts, null);
            Volatile.Write(ref _responseTcs, null);
            cts?.Dispose();
        }
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
                // Extend timeout to P2*
                cts?.CancelAfter(_timer.P2StarTimeout);
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
}

/// <summary>DiagnosticSessionControl response.</summary>
public sealed record DiagnosticSessionResponse
{
    public byte SessionType { get; init; }
    public int P2 { get; init; }
    public int P2Star { get; init; }
}
