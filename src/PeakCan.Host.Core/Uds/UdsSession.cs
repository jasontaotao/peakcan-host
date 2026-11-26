using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Uds;

/// <summary>
/// UDS diagnostic session state management. Tracks the current
/// session type and handles session transitions.
/// </summary>
public sealed partial class UdsSession : IDisposable
{
    private readonly object _lock = new();
    private readonly ILogger<UdsSession>? _logger;
    private Timer? _s3Timer;
    private UdsClient? _udsClient;

    /// <summary>
    /// 保活循环当前配置的周期；null = 未启动。<see cref="ResetS3Timer"/> 用它重排，
    /// 保证调用方传入的 <c>interval</c> 在稳态也生效。
    /// </summary>
    private TimeSpan? _s3Interval;

    /// <summary>测试可见的当前保活周期；未启动时为 null。</summary>
    internal TimeSpan? S3TimerPeriodForTesting => _s3Interval;

    /// <summary>
    /// 测试可见：最近一次 <see cref="ResetS3Timer"/> **实际传给 <c>Timer.Change</c>**
    /// 的周期。null = 还没调用过。
    /// <para>
    /// 记录的是「真实副作用」而不是配置字段——若有人把实现改回
    /// <c>Change(S3Timeout, Infinite)</c>，这里就会变成 S3 超时时长，测试随即变红。
    /// 只断言 <see cref="_s3Interval"/> 字段发现不了这种退化。
    /// </para>
    /// </summary>
    internal TimeSpan? LastAppliedS3PeriodForTesting { get; private set; }

    /// <summary>
    /// v1.2.12 PATCH Item 9: number of S3 keepalive <c>TesterPresent</c>
    /// failures observed since the last <see cref="StopS3KeepAlive"/>.
    /// Updated via <see cref="Interlocked.Increment(ref long)"/> from the
    /// timer callback and read via <see cref="Interlocked.Read(ref long)"/>
    /// so all updates are race-free.
    /// </summary>
    private long _s3Failures;

    /// <summary>
    /// S3 会话超时（ISO 14229-1 <c>S3server</c>，默认 5 秒）。
    /// <para>
    /// <b>作用范围：UI 会话保活与 S3 计时复位。</b>刷写流水线
    /// （<c>PipelineExecutor.KeepAliveLoopAsync</c>）刻意独立维护自己的 1.5 秒节奏——
    /// 长 block transfer 期间需要更激进的保活，折叠到这里的 2.5 秒是功能退化。
    /// </para>
    /// <para>
    /// 改前这里散着三套魔法数：UI 的 TesterPresent 循环 2 秒、<c>StartS3KeepAlive</c>
    /// 默认 4 秒、<c>ResetS3Timer</c> 5 秒。三者各自演化，没人说得清哪个是对的。
    /// 现在 UI 侧统一从这一个常量派生。
    /// </para>
    /// </summary>
    public static readonly TimeSpan S3Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 保活发送间隔 = S3 的 50%（2.5 秒）。
    /// <para>
    /// <b>为什么是 50% 而不是 80%：</b>真实周期 = 请求耗时 + 间隔。若某次 3E 收到
    /// NRC 0x78，客户端还要按 P2*（默认 5000 ms）继续等——取 80%（4 秒）时
    /// 「4 秒 + 0x78 等待」会直接越过 S3 边界，保活反而把会话等掉。50% 留出的余量
    /// 足以覆盖一次完整的 P2* 等待。
    /// </para>
    /// <para>
    /// 用整数 ticks 计算，避免 <c>TotalMilliseconds * 0.5</c> 的浮点在边界值上漂移。
    /// </para>
    /// </summary>
    public static readonly TimeSpan KeepAliveInterval =
        TimeSpan.FromTicks(S3Timeout.Ticks / 2);

    /// <summary>Current session type.</summary>
    public byte SessionType { get; private set; } = 0x01; // Default

    /// <summary>
    /// P2 timeout (ms) — time between request and response.
    /// <para>
    /// 初值取自 <see cref="UdsTimer.DefaultP2Timeout"/> 而不是字面量：面板展示的
    /// 就是这两个值，而真正生效的是 <see cref="UdsTimer"/>。两处写死会分叉——
    /// 常量改了这里没改，新建的 UdsSession 就开始显示与行为不符的数字。
    /// </para>
    /// </summary>
    public int P2Timeout { get; private set; } =
        (int)UdsTimer.DefaultP2Timeout.TotalMilliseconds;

    /// <summary>P2* timeout (ms) — time after NRC 0x78. 初值同 <see cref="P2Timeout"/> 的理由。</summary>
    public int P2StarTimeout { get; private set; } =
        (int)UdsTimer.DefaultP2StarTimeout.TotalMilliseconds;

    /// <summary>
    /// False while P2Timeout/P2StarTimeout still hold the 50/5000 ms construction
    /// defaults; set true by <see cref="SetSession"/> once an ECU has actually
    /// reported timings in a DiagnosticSessionControl positive response. Lets the
    /// UI label the values' provenance (本地默认 vs ECU 0x10 协商).
    /// <para>
    /// 只能表达「ECU 协商 / 非协商」两态。要区分「用户配置」这第三态，请用
    /// <see cref="TimingSource"/>——它把两者合起来给出完整来源。
    /// </para>
    /// </summary>
    public bool HasNegotiatedTiming { get; private set; }

    /// <summary>
    /// 当前时序值的来源，供界面标注。
    /// <para>
    /// 优先级：ECU 0x10 协商 &gt; 用户配置 &gt; 内置默认。这是三态而非两态的原因：
    /// 用户在通讯参数面板里填了 2000 ms，界面就该说「用户配置」；一旦 ECU 在
    /// 会话切换时报告了自己的值，来源就变成「ECU 协商」——用户配置被覆盖但不丢失
    /// （断连复位时回到它，见 <see cref="Reset"/> 的调用方）。
    /// </para>
    /// </summary>
    public UdsTimingSource TimingSource => HasNegotiatedTiming
        ? UdsTimingSource.EcuNegotiated
        : HasUserOverride ? UdsTimingSource.UserConfig : UdsTimingSource.Default;

    /// <summary>
    /// true = 当前基线来自用户配置（而非内置默认）。由 <see cref="Reset"/> 的调用方
    /// 决定：用户配过就是 true，断连复位后来源标注才能正确显示「用户配置」而不是
    /// 「本地默认」。
    /// </summary>
    public bool HasUserOverride { get; private set; }

    /// <summary>True if in Default session.</summary>
    public bool IsDefault => SessionType == 0x01;

    /// <summary>True if in Extended session.</summary>
    public bool IsExtended => SessionType == 0x02;

    /// <summary>True if in Programming session.</summary>
    public bool IsProgramming => SessionType == 0x03;

    /// <summary>
    /// Number of S3 keepalive <c>TesterPresent</c> failures observed
    /// since the last <see cref="StopS3KeepAlive"/>. Returns 0 if
    /// the keepalive was never started.
    /// </summary>
    public long S3FailureCount => Interlocked.Read(ref _s3Failures);

    /// <summary>
    /// v1.2.13 PATCH Item 2: production wire-up test accessor. Returns
    /// the logger passed to the logger-aware ctor, or null if the
    /// parameterless ctor was used. Used by AppHostBuilder DI tests to
    /// assert the wiring reaches UdsSession via UdsClient.
    /// </summary>
    internal ILogger<UdsSession>? SessionLogger => _logger;

    /// <summary>
    /// Create a new UdsSession without a logger (legacy callers).
    /// </summary>
    public UdsSession()
    {
    }

    /// <summary>
    /// Create a new UdsSession with an optional logger for S3 keepalive
    /// failure diagnostics (v1.2.12 PATCH Item 9).
    /// </summary>
    /// <param name="logger">
    /// Logger used to emit <c>Warning</c> events when an S3
    /// keepalive <c>TesterPresent</c> fails. May be <c>null</c>.
    /// </param>
    public UdsSession(ILogger<UdsSession>? logger)
    {
        _logger = logger;
    }

    /// <summary>Set session state after successful DiagnosticSessionControl.</summary>
    public void SetSession(byte sessionType, int p2, int p2Star)
    {
        lock (_lock)
        {
            SessionType = sessionType;
            P2Timeout = p2;
            P2StarTimeout = p2Star;
            HasNegotiatedTiming = true;
        }
    }

    /// <summary>
    /// 断连复位：会话回到默认，时序回到调用方给定的基线，来源标注清掉。
    /// <para>
    /// 基线由调用方传入而不是写死 50/5000，是为了让「断连后回到哪」只有一个来源：
    /// 现在传的是 <see cref="UdsTimer"/> 的默认值，将来引入用户配置后改传配置值即可，
    /// 本方法不用动。
    /// </para>
    /// <para>
    /// <b>必须同时清 <see cref="HasNegotiatedTiming"/></b>：否则界面继续标注
    /// 「ECU 0x10 协商」，而实际值是本地基线——这种「标注是假的」比数值错更难查。
    /// </para>
    /// </summary>
    /// <param name="p2TimeoutMs">复位后的 P2（毫秒）。</param>
    /// <param name="p2StarTimeoutMs">复位后的 P2*（毫秒）。</param>
    public void Reset(int p2TimeoutMs, int p2StarTimeoutMs)
    {
        Reset(p2TimeoutMs, p2StarTimeoutMs, hasUserOverride: false);
    }

    /// <summary>
    /// 断连复位，并显式指定基线是否来自用户配置。
    /// </summary>
    /// <param name="p2TimeoutMs">复位后的 P2（毫秒）。</param>
    /// <param name="p2StarTimeoutMs">复位后的 P2*（毫秒）。</param>
    /// <param name="hasUserOverride">
    /// true = 基线是用户配置值，来源标注显示「用户配置」；false = 内置默认。
    /// </param>
    public void Reset(int p2TimeoutMs, int p2StarTimeoutMs, bool hasUserOverride)
    {
        lock (_lock)
        {
            SessionType = 0x01;
            P2Timeout = p2TimeoutMs;
            P2StarTimeout = p2StarTimeoutMs;
            HasNegotiatedTiming = false;
            HasUserOverride = hasUserOverride;
        }
    }

    /// <summary>
    /// 只改时序基线，**不动会话类型**。
    /// <para>
    /// 用于用户修改通讯参数：ECU 那边还停在编程会话里，本地模型不能因为改了个
    /// 超时值就宣称「默认会话」——那会让界面显示与实际 ECU 状态不符。
    /// </para>
    /// </summary>
    /// <param name="p2TimeoutMs">P2（毫秒）。</param>
    /// <param name="p2StarTimeoutMs">P2*（毫秒）。</param>
    /// <param name="hasUserOverride">true = 来自用户配置。</param>
    public void SetTimingBaseline(int p2TimeoutMs, int p2StarTimeoutMs, bool hasUserOverride)
    {
        lock (_lock)
        {
            P2Timeout = p2TimeoutMs;
            P2StarTimeout = p2StarTimeoutMs;
            HasNegotiatedTiming = false;
            HasUserOverride = hasUserOverride;
        }
    }

    /// <summary>
    /// 重启保活计时器（发完 TesterPresent 后调用），按**已配置的周期**重新计时。
    /// <para>
    /// <b>不能用 <c>Change(S3Timeout, Infinite)</c></b>：那会把周期拉成 5 秒且不再
    /// 重复，于是 <see cref="StartS3KeepAlive"/> 的 <c>interval</c> 只在第一个 tick
    /// 生效，稳态变成 100% of S3——正好抵消了留余量的设计意图，且调用方传进来的
    /// 间隔被静默丢弃。
    /// </para>
    /// </summary>
    public void ResetS3Timer()
    {
        if (_s3Interval is not { } interval) return;
        LastAppliedS3PeriodForTesting = interval;
        _s3Timer?.Change(interval, interval);
    }

    /// <summary>Start automatic S3 keep-alive (TesterPresent).</summary>
    public void StartS3KeepAlive(UdsClient client, TimeSpan? interval = null)
    {
        _udsClient = client;
        var effectiveInterval = interval ?? KeepAliveInterval;
        // 记下周期：ResetS3Timer 要用它重排，而不是拉成 S3 超时时长。
        _s3Interval = effectiveInterval;

        _s3Timer?.Dispose();
        _s3Timer = new Timer(async _ =>
        {
            try
            {
                await client.TesterPresentAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // v1.2.13 PATCH Item 3: shutdown path. Previously this arm
                // was a silent no-op (dead-code trap — it neither logged
                // nor counted, making it indistinguishable from "no
                // failure" in observability). Now: log at Debug so the
                // shutdown race is visible in diagnostic logs without
                // polluting the Warning channel, and keep _s3Failures at 0
                // because OCE is an expected shutdown signal, not a
                // TesterPresent failure.
                if (_logger is not null)
                {
                    LogS3KeepAliveShutdown(_logger);
                }
            }
            catch (Exception ex)
            {
                // v1.2.12 PATCH Item 9: previously swallowed silently. Now
                // increment the failure counter and emit a Warning so bus
                // drops and ECU-disconnect surface in the diagnostic logs.
                Interlocked.Increment(ref _s3Failures);
                if (_logger is not null)
                {
                    LogS3KeepAliveFailed(_logger, ex);
                }
            }
        }, null, effectiveInterval, effectiveInterval);
    }

    /// <summary>Stop automatic S3 keep-alive and reset the failure counter.</summary>
    public void StopS3KeepAlive()
    {
        _s3Timer?.Dispose();
        _s3Timer = null;
        _s3Interval = null;
        // v1.2.12 PATCH Item 9: reset the failure counter on stop so a
        // subsequent StartS3KeepAlive observes a fresh window.
        Interlocked.Exchange(ref _s3Failures, 0);
    }

    public void Dispose()
    {
        _s3Timer?.Dispose();
    }

    // v1.2.12 PATCH Item 9: source-generated log helper. Source-gen
    // requires a non-null ILogger argument (it dereferences without a
    // null check), so the call site guards with `if (_logger is not null)`.
    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning, Message = "UdsSession S3 keepalive TesterPresent failed")]
    private static partial void LogS3KeepAliveFailed(ILogger logger, Exception ex);

    // v1.2.13 PATCH Item 3: source-generated log helper for the OCE catch
    // arm. Emits at Debug (not Warning) so the shutdown race is observable
    // in diagnostic logs without polluting the Warning channel with
    // expected shutdown-time exceptions. Source-gen requires a non-null
    // ILogger argument; the call site guards with `if (_logger is not null)`.
    [LoggerMessage(EventId = 5002, Level = LogLevel.Debug, Message = "UdsSession S3 keepalive cancelled during shutdown")]
    private static partial void LogS3KeepAliveShutdown(ILogger logger);
}
