using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.6.0 MINOR T2: result of an <see cref="TraceSessionAutoSaver.TryLoadAutoSnapshotAsync"/>
/// attempt. <see cref="Dto"/> is <c>null</c> when no auto-save bundle
/// exists yet (first run after install) or when the on-disk file is
/// corrupt. <see cref="SavedAt"/> is the bundle's recorded timestamp
/// (handy for the restore prompt's "A session was auto-saved ..."
/// text).
/// </summary>
public sealed record AutoLoadResult(TraceSessionBundleDto? Dto, string SourceFile, DateTimeOffset SavedAt)
{
    public static AutoLoadResult None { get; } = new(null, "", DateTimeOffset.MinValue);
}

/// <summary>User's answer to the auto-restore prompt.</summary>
public enum RestoreAnswer
{
    /// <summary>No auto-save file existed.</summary>
    NoFile,
    /// <summary>User previously opted out via "No, don't ask again".</summary>
    NeverRestore,
    /// <summary>User clicked No this time.</summary>
    No,
    /// <summary>User clicked Yes.</summary>
    Yes,
    /// <summary>Prompt or apply threw an exception.</summary>
    ApplyFailed,
}

/// <summary>
/// v3.6.0 MINOR T2: outcome of <see cref="TraceSessionAutoSaver.ApplyAutoSnapshotAsync"/>.
/// <see cref="Applied"/> is true when the bundle was loaded into the
/// VM. <see cref="PromptShown"/> is true when the user was actually
/// shown the MessageBox (false when the prompt was suppressed by
/// <see cref="AutoSavePrefs.NeverRestore"/> or skipped because no
/// bundle existed).
/// </summary>
public sealed record RestoreOutcome(bool Applied, bool PromptShown, RestoreAnswer Answer);

/// <summary>
/// v3.6.0 MINOR T2: owner-bound modal prompt abstraction. The default
/// WPF implementation (<see cref="WpfMessageBoxPrompt"/>) dispatches
/// to the WPF STA thread; tests inject a fake that returns a canned
/// <see cref="MessageBoxResult"/>.
/// </summary>
public interface IMessageBoxPrompt
{
    /// <summary>Show a Yes/No modal owned by <paramref name="owner"/>.
    /// Implementation may marshal to the WPF STA thread; tests inject
    /// a fake that returns a canned value.</summary>
    Task<MessageBoxResult> ShowAsync(
        string title,
        string message,
        Window? owner);

    /// <summary>
    /// v3.10.0 MINOR T1 (C1): show an information-only OK modal owned
    /// by <paramref name="owner"/>. Returns <see cref="MessageBoxResult.OK"/>
    /// unconditionally (the WPF <see cref="MessageBox"/> API does not
    /// surface a distinct result for OK-only modals). Mirrors the
    /// existing <see cref="ShowAsync"/> Yes/No pattern, but uses
    /// <see cref="MessageBoxButton.OK"/> + <see cref="MessageBoxImage.Warning"/>.
    /// </summary>
    Task<MessageBoxResult> ShowInformationAsync(
        string title,
        string message,
        Window? owner);
}

/// <summary>
/// v3.10.0 MINOR T2 (C3): Trace-specific thin subclass of
/// <see cref="SessionAutoSaver{TVm}"/>. Owns only the config that
/// differentiates Trace from Replay (session service, "has content"
/// check, restore prompt title, auto-save path). All orchestration
/// lives in the base class.
/// <para>
/// v3.x (会话状态剥离 Task 4): 改为直连 <see cref="ITraceSessionService"/>
/// （原 VM provider 已删除）。TraceViewerViewModel 已 transient，auto-save
/// 不再经 provider 拿 VM，直接对 singleton service 取快照 / 恢复会话。
/// </para>
/// </summary>
public sealed partial class TraceSessionAutoSaver : SessionAutoSaver<ITraceSessionService>
{
    private readonly ITraceSessionService _session;

    /// <summary>Production ctor: defaults the auto-save path to
    /// <c>%APPDATA%/PeakCan.Host/trace-session-auto.tmtrace</c>.</summary>
    public TraceSessionAutoSaver(
        ITraceSessionService session,
        TraceSessionLibrary library,
        IAutoSavePrefsStore prefs,
        IMessageBoxPrompt prompt,
        ILogger<TraceSessionAutoSaver> logger)
        : base(library, prefs, prompt, logger, DefaultPath())
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>Test ctor with explicit path.</summary>
    public TraceSessionAutoSaver(
        ITraceSessionService session,
        TraceSessionLibrary library,
        IAutoSavePrefsStore prefs,
        IMessageBoxPrompt prompt,
        ILogger<TraceSessionAutoSaver> logger,
        string autoSavePath)
        : base(library, prefs, prompt, logger, autoSavePath)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    // v3.x (会话状态剥离 Task 4): service 直接注入并持有（singleton），
    // 容器 dispose 后仍可安全取快照——不再依赖 provider 的 GetService 语义。
    protected override ITraceSessionService? GetActiveVm() => _session;

    protected override bool HasContentToSave(ITraceSessionService session) =>
        session.HasContent;

    /// <summary>
    /// v3.x (会话状态剥离 Task 4): 快照改由 <see cref="ITraceSessionService"/>
    /// 生成（VM transient 后，原经 <c>vm.BuildSnapshotAsync()</c> 的转发已随
    /// VM provider 一并删除）。
    /// </summary>
    protected override TraceSessionBundleDto BuildSnapshot(ITraceSessionService session) =>
        session.BuildSnapshot();

    protected override Task<IReadOnlyList<string>> ApplySnapshotToVmAsync(
        ITraceSessionService session, string sourceFile) =>
        session.OpenSessionAsync(sourceFile);

    protected override string RestorePromptTitle =>
        "Restore previous trace session?";

    protected override string FormatRestoreMessage(DateTimeOffset savedAt) =>
        savedAt == DateTimeOffset.MinValue
            ? "A previously-saved trace session was found. Restore it?"
            : $"A trace session was auto-saved {savedAt:yyyy-MM-dd HH:mm}. Restore it?";

    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PeakCan.Host", "trace-session-auto.tmtrace");
    }

    // v3.10.0 T2 R1: log hooks are plain methods (not partial), so
    // they can override the base virtual hooks across inheritance.
    // They forward to [LoggerMessage] source-gen partials declared at
    // the bottom of this file.
    // v3.x (会话状态剥离 Task 5 final, Minor #6): OnNoVm 已删——GetActiveVm 返回
    // 注入的 non-null service，base TrySaveAutoSnapshotAsync 的 vm is null 分支
    // 不可达（Replay 子类仍需 OnNoVm，此处不复写 base 默认 hook）。
    protected override void OnSaved(string path, int sourcesCount) =>
        LogSaved(Logger, path, sourcesCount);
    protected override void OnSaveFailed(Exception ex, string path) =>
        LogSaveFailed(Logger, ex, path);
    protected override void OnMissing(string path, int count) =>
        LogMissing(Logger, path, count);
    protected override void OnApplyFailed(Exception ex) =>
        LogApplyFailed(Logger, ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Auto-saved {SourcesCount} sources to {Path}")]
    private static partial void LogSaved(ILogger logger, string path, int sourcesCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auto-save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auto-restore: {Count} sources missing from {Path}")]
    private static partial void LogMissing(ILogger logger, string path, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ApplyAutoSnapshotAsync threw")]
    private static partial void LogApplyFailed(ILogger logger, Exception ex);
}
