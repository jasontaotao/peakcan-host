using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.Path;

namespace PeakCan.Host.App.Services;

/// <summary>
/// DBC load + lookup. MVP contract: parse a single DBC file off the UI
/// thread, expose the resulting <see cref="DbcDocument"/> as a property
/// + event, surface parse / IO failures via the <see cref="LoadFailed"/>
/// event. Cancellation is silent (no <c>LoadFailed</c>).
/// <para>
/// <b>Threading:</b> <see cref="LoadAsync"/> runs the file read on the
/// async I/O pool and the parse on a worker thread via
/// <see cref="Task.Run(Action, CancellationToken)"/>; the event
/// handlers fire on whatever thread the worker is on, so subscribers
/// must marshal to the UI thread if they touch WPF bindings.
/// </para>
/// <para>
/// <b>Partial class:</b> declared <c>partial</c> because WPF source
/// generators may emit a partial declaration for App-layer types
/// (verified during Task 15 review: removing <c>partial</c> fails
/// with CS0260 from the WPF temp-build). <see cref="LoadAsync"/> is
/// <c>virtual</c> (so this class is intentionally NOT <c>sealed</c>) to
/// allow tests to swap in a no-op / canned-document stub without
/// hitting the disk.
/// </para>
/// <para>
/// <b>Subscription discipline（2026-09-06 设计层 MEDIUM 审计结论）：</b>
/// <see cref="DbcLoaded"/> / <see cref="LoadFailed"/> 的订阅者分两类，各有
/// 明确契约：
/// <list type="bullet">
///   <item><b>app 生命周期单例 VM</b>（DbcViewModel / DbcSendViewModel /
///   TraceViewModel / TraceViewerViewModel / MultiFrameSendViewModel）：
///   ctor 订阅、进程退出即随之消亡——<b>有意不退订</b>（见 DbcViewModel
///   类文档的 footgun 备注：此前的 IDisposable 实现反而是隐患）。新增此类
///   订阅者必须在类文档写明与 DbcViewModel 同款的"DI 单例、终身存活"论据。</item>
///   <item><b>可释放的订阅者</b>（如 DbcApi）：ctor 订阅、<c>Dispose</c>
///   必须退订两个事件（守护测试：DbcApiTests.Dispose_Unsubscribes_Both_Events）。
///   新增 <c>IDisposable</c> 订阅者照此模式。</item>
/// </list>
/// <b>禁止</b>transient（非单例）组件无退订地订阅本服务事件——漏退订即跨
/// 实例状态污染（事件持有旧实例闭包，旧实例不被 GC）。
/// </para>
/// </summary>
public partial class DbcService
{
    private readonly ILogger<DbcService> _logger;

    // v1.6.6 PATCH Item 1: opt-in caps applied at LoadAsync entry (size,
    // pre-read) and inside DbcParser.Parse (message-count, mid-parse).
    // Back-compat: 1-arg ctor delegates with DbcOptions.Unlimited so all
    // existing callers and tests see no behavior change.
    private readonly DbcOptions _options;

    /// <summary>The most recently successfully parsed DBC, or null.</summary>
    /// <remarks>
    /// Thread-safety: written on a Task.Run worker (LoadAsync) and read
    /// from DbcDecodeBackgroundService's worker thread. Uses
    /// <see cref="Volatile.Read{T}"/> / <see cref="Volatile.Write{T}"/>
    /// to ensure cross-thread visibility without locks.
    /// </remarks>
    private DbcDocument? _current;

    public DbcDocument? Current
    {
        get => Volatile.Read(ref _current);
        private set => Volatile.Write(ref _current, value);
    }

    /// <summary>Raised after a successful parse; carries the new document.</summary>
    public event Action<DbcDocument>? DbcLoaded;

    /// <summary>Raised on IO error or parse failure; never raised on cancellation.</summary>
    public event Action<Error>? LoadFailed;

    /// <summary>
    /// Back-compat constructor. Equivalent to passing
    /// <see cref="DbcOptions.Unlimited"/>; delegates to the 2-arg ctor so
    /// existing callers and tests see no behavior change.
    /// </summary>
    public DbcService(ILogger<DbcService> logger)
        : this(logger, DbcOptions.Unlimited)
    {
    }

    /// <summary>
    /// v1.6.6 PATCH Item 1: full-fidelity constructor with opt-in
    /// <see cref="DbcOptions"/>. Bound at DI registration from
    /// <c>appsettings.json:Dbc</c> section.
    /// <para>
    /// <c>internal</c> because <see cref="DbcOptions"/> is internal
    /// (no public API justification for exposing the limit knobs to
    /// downstream consumers — DI configuration binding is the only
    /// entry point). Visible to test project via
    /// <c>InternalsVisibleTo PeakCan.Host.App.Tests</c>.
    /// </para>
    /// </summary>
    internal DbcService(ILogger<DbcService> logger, DbcOptions options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Test seam only. Sets <see cref="Current"/> directly so tests that
    /// exercise downstream consumers (TraceService → SignalViewModel) can
    /// install a canned <see cref="DbcDocument"/> without round-tripping
    /// through <see cref="LoadAsync"/>. Not part of the production API —
    /// visible to <c>PeakCan.Host.App.Tests</c> via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal void SetCurrentForTests(DbcDocument doc) => Current = doc;



    [LoggerMessage(Level = LogLevel.Information, Message = "DBC loaded from {Path} ({Count} messages)")]
    private static partial void LogLoadSucceeded(ILogger logger, string path, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DBC parse failed for {Path}: {Code} {Message}")]
    private static partial void LogLoadParseFailed(ILogger logger, string path, ErrorCode code, string message);

    // v1.6.6 PATCH Item 1: emitted when the file-size cap rejects the load.
    [LoggerMessage(Level = LogLevel.Warning, Message = "DBC size cap rejected {Path} ({Size} bytes > MaxFileSizeBytes {Cap})")]
    private static partial void LogLoadSizeFailed(ILogger logger, string path, long cap, long size);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DBC IO failed for {Path}")]
    private static partial void LogLoadIoFailed(ILogger logger, string path, Exception ex);
}