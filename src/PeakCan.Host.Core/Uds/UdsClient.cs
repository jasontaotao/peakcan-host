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
public partial class UdsClient : IDisposable
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



}

/// <summary>DiagnosticSessionControl response.</summary>
public sealed record DiagnosticSessionResponse
{
    public byte SessionType { get; init; }
    public int P2 { get; init; }
    public int P2Star { get; init; }
    // === Flow A methods moved to UdsClient/TransportFlow.cs (W12 Task 1) ===
    // === Flow B methods moved to UdsClient/SessionFlow.cs (W12 Task 2) ===
    // === Flow C methods moved to UdsClient/DataIOFlow.cs (W12 Task 3) ===
    // === Flow D methods moved to UdsClient/SecurityFlow.cs (W12 Task 4) ===
}
