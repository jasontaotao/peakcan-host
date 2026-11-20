using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.10.0 MINOR T2 (C3): generic base class for the Trace / Replay
/// auto-save + auto-restore cycle. Subclasses are config-only thin
/// shells picking the VM type and deciding when the VM is worth
/// persisting.
/// </summary>
public abstract class SessionAutoSaver<TVm>
{
    private readonly TraceSessionLibrary _library;
    private readonly IAutoSavePrefsStore _prefs;
    private readonly IMessageBoxPrompt _prompt;
    private readonly ILogger _logger;

    /// <summary>Logger routed through to the base's default log hooks.
    /// <c>protected</c> so subclasses can forward to their own
    /// [LoggerMessage] partials.</summary>
    protected ILogger Logger => _logger;

    /// <summary>The on-disk location of the auto-save bundle.</summary>
    public string AutoSavePath { get; }

    protected SessionAutoSaver(
        TraceSessionLibrary library,
        IAutoSavePrefsStore prefs,
        IMessageBoxPrompt prompt,
        ILogger logger,
        string autoSavePath)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _prefs = prefs ?? throw new ArgumentNullException(nameof(prefs));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        AutoSavePath = autoSavePath ?? throw new ArgumentNullException(nameof(autoSavePath));
        var dir = Path.GetDirectoryName(AutoSavePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    protected abstract TVm? GetActiveVm();
    protected abstract bool HasContentToSave(TVm vm);
    protected abstract string RestorePromptTitle { get; }

    /// <summary>Default throws — subclasses that build a snapshot via a
    /// VM helper method override this.</summary>
    protected virtual TraceSessionBundleDto BuildSnapshot(TVm vm) =>
        throw new NotSupportedException(
            $"{GetType().Name} must override BuildSnapshot");

    /// <summary>
    /// 异步快照入口。默认复用同步实现（适合本身不捕获 UI 上下文的
    /// service）；拥有异步构建能力的 VM（如 Replay）必须覆盖，
    /// 否则在 UI 线程上 sync-over-async 会自锁。
    /// <para>
    /// 默认实现同步执行，<paramref name="ct"/> 无法中断正在进行的快照；
    /// 只有异步覆盖才真正观察取消（OnExit 的超时仍能约束外层等待）。
    /// </para>
    /// </summary>
    protected virtual Task<TraceSessionBundleDto> BuildSnapshotAsync(
        TVm vm, CancellationToken ct)
        => Task.FromResult(BuildSnapshot(vm));

    protected abstract Task<IReadOnlyList<string>> ApplySnapshotToVmAsync(
        TVm vm, string sourceFile);

    protected virtual string FormatRestoreMessage(DateTimeOffset savedAt) =>
        savedAt == DateTimeOffset.MinValue
            ? "A previously-saved session was found. Restore it?"
            : $"A session was auto-saved {savedAt:yyyy-MM-dd HH:mm}. Restore it?";

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1848",
        Justification = "Default hook delegates to a non-source-gen fallback; both subclasses override with [LoggerMessage] partials.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1873",
        Justification = "Default hook is only called when the log level is enabled.")]
    protected virtual void OnNoVm()
    {
        _logger.LogDebug("Auto-save skipped: no live {VmType}", typeof(TVm).Name);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1848",
        Justification = "Default hook delegates to a non-source-gen fallback; both subclasses override with [LoggerMessage] partials.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1873",
        Justification = "Default hook is only called when the log level is enabled.")]
    protected virtual void OnSaved(string path, int sourcesCount)
    {
        _logger.LogInformation("Auto-saved {SourcesCount} sources to {Path}",
            sourcesCount, path);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1848",
        Justification = "Default hook delegates to a non-source-gen fallback; both subclasses override with [LoggerMessage] partials.")]
    protected virtual void OnSaveFailed(Exception ex, string path)
    {
        _logger.LogWarning(ex, "Auto-save to {Path} failed", path);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1848",
        Justification = "Default hook delegates to a non-source-gen fallback; both subclasses override with [LoggerMessage] partials.")]
    protected virtual void OnMissing(string path, int count)
    {
        _logger.LogWarning("Auto-restore: {Count} sources missing from {Path}",
            count, path);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1848",
        Justification = "Default hook delegates to a non-source-gen fallback; both subclasses override with [LoggerMessage] partials.")]
    protected virtual void OnApplyFailed(Exception ex)
    {
        _logger.LogWarning(ex, "ApplyAutoSnapshotAsync threw");
    }

    /// <summary>
    /// Best-effort snapshot write. Returns <c>true</c> when the bundle
    /// was written; <c>false</c> when the VM was unavailable or had no
    /// content. Never throws.
    /// </summary>
    public async Task<bool> TrySaveAutoSnapshotAsync(CancellationToken ct)
    {
        try
        {
            var vm = GetActiveVm();
            if (vm is null)
            {
                OnNoVm();
                return false;
            }
            if (!HasContentToSave(vm)) return false;
            var dto = await BuildSnapshotAsync(vm, ct).ConfigureAwait(false);
            await Task.Run(() => _library.Save(dto, AutoSavePath), ct).ConfigureAwait(false);
            OnSaved(AutoSavePath, dto.Sources?.Count ?? 0);
            return true;
        }
        catch (Exception ex)
        {
            OnSaveFailed(ex, AutoSavePath);
            return false;
        }
    }

    /// <summary>
    /// Read the auto-save bundle from disk. Returns
    /// <see cref="AutoLoadResult.None"/> when the file is missing or corrupt.
    /// </summary>
    public Task<AutoLoadResult> TryLoadAutoSnapshotAsync(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            if (!File.Exists(AutoSavePath)) return AutoLoadResult.None;
            var dto = _library.Load(AutoSavePath);
            if (dto is null) return AutoLoadResult.None;
            return new AutoLoadResult(dto, AutoSavePath, dto.SavedAt);
        }, ct);
    }

    /// <summary>
    /// Prompt the user to restore the previous auto-saved session.
    /// Idempotent against the NeverRestore flag.
    /// </summary>
    public async Task<RestoreOutcome> ApplyAutoSnapshotAsync(
        TVm vm,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vm);
        try
        {
            var prefs = await _prefs.LoadAsync(ct).ConfigureAwait(true);
            if (prefs.NeverRestore)
                return new RestoreOutcome(false, false, RestoreAnswer.NeverRestore);

            var load = await TryLoadAutoSnapshotAsync(ct).ConfigureAwait(true);
            if (load.Dto is null)
                return new RestoreOutcome(false, false, RestoreAnswer.NoFile);

            var owner = Application.Current?.MainWindow;
            var answer = await _prompt.ShowAsync(
                RestorePromptTitle, FormatRestoreMessage(load.SavedAt), owner)
                .ConfigureAwait(true);

            if (answer != MessageBoxResult.Yes)
            {
                await _prefs.SaveAsync(
                    new AutoSavePrefs(NeverRestore: true), ct).ConfigureAwait(true);
                return new RestoreOutcome(false, true, RestoreAnswer.No);
            }

            var missing = await ApplySnapshotToVmAsync(vm, load.SourceFile)
                .ConfigureAwait(true);
            if (missing.Count > 0)
                OnMissing(load.SourceFile, missing.Count);
            return new RestoreOutcome(true, true, RestoreAnswer.Yes);
        }
        catch (Exception ex)
        {
            OnApplyFailed(ex);
            return new RestoreOutcome(false, true, RestoreAnswer.ApplyFailed);
        }
    }
}
