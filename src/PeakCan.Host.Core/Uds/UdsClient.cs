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
    // === Flow E methods moved to UdsClient/TransferFlow.cs (W12 Task 5) ===
}
