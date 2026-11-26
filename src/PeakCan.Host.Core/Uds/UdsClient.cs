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

    /// <summary>
    /// The P2* duration the NRC 0x78 path attempted to arm on the response
    /// CTS. Null until the first response-pending is observed.
    /// <para>
    /// Production code never reads this; tests use it to assert the value
    /// equals the ECU-negotiated P2* (and not a mis-scaled one) without
    /// waiting out the real timeout. Recorded before the CancelAfter call so
    /// it survives the shutdown race where the CTS is already disposed.
    /// </para>
    /// </summary>
    internal TimeSpan? LastP2StarArmedForTesting { get; private set; }

    /// <summary>
    /// The timer this client actually reads for P2 / P2* arming.
    /// <para>
    /// Production code never reads this; tests use it to prove that two stacks built
    /// back-to-back do not share a timer (flash runs would otherwise inherit the
    /// previous run's ECU-negotiated timing).
    /// </para>
    /// </summary>
    internal UdsTimer TimerForTesting => _timer;

    // C-8 fix: the SID of the in-flight request, used to validate that an
    // incoming positive response (SID+0x40) actually echoes our SID. Without
    // this guard, stale or out-of-sequence frames are accepted as the result.
    private byte _pendingRequestSid;

    /// <summary>
    /// 断连复位的代际号，每次 <see cref="ResetDiagnosticState"/> 自增。
    /// <para>
    /// 解决的是一条**无法靠加锁消除**的竞态：<c>DiagnosticSessionControlAsync</c>
    /// 在 <c>SendRequestAsync</c> 返回（此时 <c>_requestLock</c> 已释放）之后才把
    /// 协商结果写进 <see cref="Session"/> 与 <see cref="_timer"/>。若断连复位恰好
    /// 落在「响应已到、续写未跑」之间，复位先清干净，随后那条续写又把上一台 ECU
    /// 的 P2/P2* 和 <c>HasNegotiatedTiming=true</c> 写回去——正是本阶段要消灭的
    /// 「面板说默认、实际用旧值」。
    /// </para>
    /// <para>
    /// 加锁解决不了：续写本身不持锁。所以由续写自己比对代际号，发现期间发生过
    /// 复位就放弃写入。
    /// </para>
    /// </summary>
    private int _resetEpoch;

    /// <summary>
    /// 用户配置的时序基线（毫秒）。断连复位与重配都回到这里，而不是硬编码的
    /// 50/5000——否则用户改个 CAN ID 就把自己配的 P2 打回默认。
    /// <para>
    /// 未配置时为 <c>null</c>，表示用 <see cref="UdsTimer"/> 的内置默认。
    /// </para>
    /// </summary>
    private (int P2Ms, int P2StarMs)? _userTimingBaseline;

    /// <summary>
    /// 当前复位代际号。调用方可在一次请求前后各读一次，比对是否期间发生过
    /// 断连复位（<see cref="ResetDiagnosticState"/>）。
    /// </summary>
    internal int ResetEpoch => Volatile.Read(ref _resetEpoch);

    /// <summary>
    /// Test-only hook fired between <see cref="UdsSession.SetSession"/> and the
    /// <c>_timer</c> writes in <c>DiagnosticSessionControlAsync</c> — i.e. inside the
    /// non-atomic window the second <see cref="ResetEpoch"/> check guards.
    /// <para>Production code never sets this.</para>
    /// </summary>
    internal Action? BetweenSessionAndTimerWriteForTesting { get; set; }

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
    /// <param name="timeProvider">
    /// M3.2（spec D5）：lockout 计时 TimeProvider（可选注入，缺省 System）。
    /// 端到端测试与 server 共享虚拟时钟。
    /// </param>
    public UdsClient(IsoTpLayer isoTp, UdsTimer? timer = null, ILogger<UdsSession>? sessionLogger = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(isoTp);

        _isoTp = isoTp;
        _timer = timer ?? new UdsTimer();
        Session = new UdsSession(sessionLogger);
        Security = new UdsSecurity(timeProvider);

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
    public UdsClient(IsoTpLayer isoTp, IKeyDerivationAlgorithm keyAlgorithm, UdsTimer? timer = null, ILogger<UdsSession>? sessionLogger = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(isoTp);
        ArgumentNullException.ThrowIfNull(keyAlgorithm);

        _isoTp = isoTp;
        _keyAlgorithm = keyAlgorithm;
        _timer = timer ?? new UdsTimer();
        Session = new UdsSession(sessionLogger);
        Security = new UdsSecurity(timeProvider);

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
    public UdsClient(IsoTpLayer isoTp, IKeyDerivationAlgorithm keyAlgorithm, UdsSecurityLockoutConfig lockoutConfig, UdsTimer? timer = null, ILogger<UdsSession>? sessionLogger = null, TimeProvider? timeProvider = null)
        : this(isoTp, keyAlgorithm, timer, sessionLogger, timeProvider)
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

/// <summary>
/// DiagnosticSessionControl (0x10) positive response.
/// <para>
/// Both timing values are already converted to MILLISECONDS here — the wire
/// encoding differs (P2 is ms, P2* is 10 ms units) but every consumer of this
/// record expects milliseconds. Do not re-scale at the call sites.
/// </para>
/// </summary>
public sealed record DiagnosticSessionResponse
{
    public byte SessionType { get; init; }

    /// <summary>P2 timeout in milliseconds (wire value is already ms).</summary>
    public int P2 { get; init; }

    /// <summary>P2* timeout in milliseconds (wire value is in 10 ms units, ×10 applied).</summary>
    public int P2Star { get; init; }
    // === Flow A methods moved to UdsClient/TransportFlow.cs (W12 Task 1) ===
    // === Flow B methods moved to UdsClient/SessionFlow.cs (W12 Task 2) ===
    // === Flow C methods moved to UdsClient/DataIOFlow.cs (W12 Task 3) ===
    // === Flow D methods moved to UdsClient/SecurityFlow.cs (W12 Task 4) ===
    // === Flow E methods moved to UdsClient/TransferFlow.cs (W12 Task 5) ===
}
