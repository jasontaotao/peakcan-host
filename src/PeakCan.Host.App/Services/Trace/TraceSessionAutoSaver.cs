using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.ViewModels;

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
/// v3.6.0 MINOR T2: thin seam so the auto-saver doesn't depend on
/// <see cref="IServiceProvider"/> directly. The default implementation
/// (<see cref="ServiceProviderTraceViewerViewModelProvider"/>) resolves
/// the singleton <see cref="TraceViewerViewModel"/> on demand; tests
/// inject a fake that returns a stub VM.
/// </summary>
public interface ITraceViewerViewModelProvider
{
    /// <summary>Return the live <see cref="TraceViewerViewModel"/>, or
    /// <c>null</c> if the DI container has been disposed (e.g. during
    /// <c>App.OnExit</c> teardown).</summary>
    TraceViewerViewModel? GetCurrent();
}

/// <summary>
/// v3.6.0 MINOR T2: DI-backed <see cref="ITraceViewerViewModelProvider"/>.
/// Singleton — owns no state.
/// </summary>
public sealed class ServiceProviderTraceViewerViewModelProvider : ITraceViewerViewModelProvider
{
    private readonly IServiceProvider _services;
    public ServiceProviderTraceViewerViewModelProvider(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public TraceViewerViewModel? GetCurrent()
    {
        // GetService (not GetRequiredService) so a disposed container
        // returns null rather than throwing — the auto-saver treats
        // null as a no-op early-out.
        return _services.GetService<TraceViewerViewModel>();
    }
}

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
}

/// <summary>
/// v3.6.0 MINOR T2: persists the user's Trace Viewer session to a
/// well-known location on app close, and offers to restore it on next
/// startup. Singleton — owns the auto-save file path and the
/// user-supplied opt-out flag.
/// <para>
/// <b>Lifecycle:</b>
/// <list type="number">
/// <item><see cref="App.OnExit"/> calls
/// <see cref="TrySaveAutoSnapshotAsync"/> BEFORE
/// <c>_host.StopAsync</c> so a clean shutdown always writes the
/// current bundle (5s cap so we don't blow the 10s shutdown budget
/// if the disk is slow).</item>
/// <item><see cref="App.OnStartup"/> chains
/// <see cref="ApplyAutoSnapshotAsync"/> AFTER <c>shell.Show()</c> so
/// the modal has an owner window and is visible to the user.</item>
/// </list>
/// </para>
/// <para>
/// <b>Fail-safe contract:</b> every public method swallows
/// <see cref="Exception"/> internally and logs at Warning — auto-save
/// must never crash the host. The caller's catch is purely defensive.
/// </para>
/// </summary>
public sealed partial class TraceSessionAutoSaver
{
    private readonly ITraceViewerViewModelProvider _vmProvider;
    private readonly TraceSessionLibrary _library;
    private readonly IAutoSavePrefsStore _prefs;
    private readonly IMessageBoxPrompt _prompt;
    private readonly ILogger<TraceSessionAutoSaver> _logger;

    /// <summary>The on-disk location of the auto-save bundle. Always
    /// under <c>%APPDATA%/PeakCan.Host/</c> regardless of the user's
    /// chosen save folder, so the bundle survives across projects.</summary>
    public string AutoSavePath { get; }

    /// <summary>Production ctor: defaults the auto-save path to
    /// <c>%APPDATA%/PeakCan.Host/trace-session-auto.tmtrace</c>.</summary>
    public TraceSessionAutoSaver(
        ITraceViewerViewModelProvider vmProvider,
        TraceSessionLibrary library,
        IAutoSavePrefsStore prefs,
        IMessageBoxPrompt prompt,
        ILogger<TraceSessionAutoSaver> logger)
        : this(vmProvider, library, prefs, prompt, logger, DefaultPath()) { }

    /// <summary>Test ctor with explicit path.</summary>
    public TraceSessionAutoSaver(
        ITraceViewerViewModelProvider vmProvider,
        TraceSessionLibrary library,
        IAutoSavePrefsStore prefs,
        IMessageBoxPrompt prompt,
        ILogger<TraceSessionAutoSaver> logger,
        string autoSavePath)
    {
        _vmProvider = vmProvider ?? throw new ArgumentNullException(nameof(vmProvider));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _prefs = prefs ?? throw new ArgumentNullException(nameof(prefs));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        AutoSavePath = autoSavePath ?? throw new ArgumentNullException(nameof(autoSavePath));
        var dir = Path.GetDirectoryName(AutoSavePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Best-effort snapshot write. Returns <c>true</c> when the
    /// bundle was written; <c>false</c> when the VM was unavailable
    /// (DI disposed) or had no sources (nothing to save). Never
    /// throws — failures are logged at Warning so the caller's
    /// catch is purely defensive.
    /// </summary>
    public async Task<bool> TrySaveAutoSnapshotAsync(CancellationToken ct)
    {
        try
        {
            var vm = _vmProvider.GetCurrent();
            if (vm is null)
            {
                LogNoVm(_logger);
                return false;
            }
            // Read Sources.Count on the calling thread; BuildSnapshot
            // touches only in-memory VM state. Push the JSON
            // serialization + file write off-thread so the WPF STA
            // thread isn't blocked during the ~50ms worst case.
            var sourcesCount = vm.Sources.Count;
            if (sourcesCount == 0) return false;
            var dto = vm.BuildSnapshot();
            await Task.Run(() => _library.Save(dto, AutoSavePath), ct).ConfigureAwait(false);
            LogSaved(_logger, AutoSavePath, sourcesCount);
            return true;
        }
        catch (Exception ex)
        {
            LogSaveFailed(_logger, ex, AutoSavePath);
            return false;
        }
    }

    /// <summary>
    /// Read the auto-save bundle from disk. Returns
    /// <see cref="AutoLoadResult.None"/> when the file is missing or
    /// corrupt. Never throws — corrupt files are logged at Error
    /// inside <see cref="TraceSessionLibrary.Load"/>.
    /// </summary>
    public Task<AutoLoadResult> TryLoadAutoSnapshotAsync(CancellationToken ct)
    {
        // TraceSessionLibrary.Load is synchronous + log-on-error. Wrap
        // in Task.Run so the WPF STA thread isn't blocked; the load
        // is sub-millisecond for the typical < 10KB bundle.
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
    /// Idempotent against the NeverRestore flag: a subsequent call
    /// returns immediately without showing the modal. Returns
    /// <see cref="RestoreAnswer.NoFile"/> when no bundle exists;
    /// <see cref="RestoreAnswer.NeverRestore"/> when the user has
    /// previously opted out.
    /// <para>
    /// Fail-safe: prompt or apply exceptions return
    /// <see cref="RestoreOutcome"/> with
    /// <see cref="RestoreAnswer.ApplyFailed"/> so the caller can log
    /// the failure and continue.
    /// </para>
    /// </summary>
    public async Task<RestoreOutcome> ApplyAutoSnapshotAsync(
        TraceViewerViewModel vm,
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

            // Find the owner window by walking up from any live UI
            // element. vm itself isn't a FrameworkElement, so we
            // resolve via the active Application's MainWindow. Falls
            // back to null (unparented modal) which WPF still shows.
            var owner = Application.Current?.MainWindow;
            var title = "Restore previous trace session?";
            var message = load.SavedAt == DateTimeOffset.MinValue
                ? "A previously-saved trace session was found. Restore it?"
                : $"A trace session was auto-saved {load.SavedAt:yyyy-MM-dd HH:mm}. Restore it?";
            var answer = await _prompt.ShowAsync(title, message, owner).ConfigureAwait(true);

            if (answer != MessageBoxResult.Yes)
            {
                // "No" persists the NeverRestore flag so we never
                // prompt again. "Cancel" (which we never offer) is
                // treated identically — same UX outcome.
                await _prefs.SaveAsync(
                    new AutoSavePrefs(NeverRestore: true), ct).ConfigureAwait(true);
                return new RestoreOutcome(false, true, RestoreAnswer.No);
            }

            var missing = await vm.OpenSessionAsync(load.SourceFile).ConfigureAwait(true);
            if (missing.Count > 0)
                LogMissing(_logger, load.SourceFile, missing.Count);
            return new RestoreOutcome(true, true, RestoreAnswer.Yes);
        }
        catch (Exception ex)
        {
            LogApplyFailed(_logger, ex);
            return new RestoreOutcome(false, true, RestoreAnswer.ApplyFailed);
        }
    }

    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PeakCan.Host", "trace-session-auto.tmtrace");
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Auto-save skipped: no live TraceViewerViewModel")]
    private static partial void LogNoVm(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Auto-saved {SourcesCount} sources to {Path}")]
    private static partial void LogSaved(ILogger logger, string path, int sourcesCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auto-save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auto-restore: {Count} sources missing from {Path}")]
    private static partial void LogMissing(ILogger logger, string path, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ApplyAutoSnapshotAsync threw")]
    private static partial void LogApplyFailed(ILogger logger, Exception ex);
}