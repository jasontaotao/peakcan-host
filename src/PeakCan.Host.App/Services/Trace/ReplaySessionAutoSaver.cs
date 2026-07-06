using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.7.0 MINOR Chunk 3: persists the user's Replay tab session to a
/// well-known location on app close, and offers to restore it on next
/// startup. Singleton — owns the auto-save file path.
/// <para>
/// Mirrors <see cref="TraceSessionAutoSaver"/> for the Replay tab. Uses
/// the SAME <see cref="AutoSavePrefs"/> file as Trace — opting out of
/// auto-restore once opts out for BOTH tabs (don't pester the user
/// twice per session). The auto-save bundle file is separate
/// (<c>replay-session-auto.tmtrace</c> vs. <c>trace-session-auto.tmtrace</c>).
/// </para>
/// <para>
/// <b>Lifecycle:</b>
/// <list type="number">
/// <item><see cref="App.OnExit"/> calls
/// <see cref="TrySaveAutoSnapshotAsync"/> AFTER the Trace auto-save
/// but BEFORE <c>_host.StopAsync</c> (5s cap).</item>
/// <item><see cref="App.OnStartup"/> chains
/// <see cref="ApplyAutoSnapshotAsync"/> AFTER the Trace restore
/// prompt (sequential, each independent).</item>
/// </list>
/// </para>
/// <para>
/// <b>Fail-safe contract:</b> every public method swallows
/// <see cref="Exception"/> internally and logs at Warning — auto-save
/// must never crash the host.
/// </para>
/// </summary>
public sealed partial class ReplaySessionAutoSaver
{
    private readonly IReplayViewModelProvider _vmProvider;
    private readonly TraceSessionLibrary _library;
    private readonly IAutoSavePrefsStore _prefs;
    private readonly IMessageBoxPrompt _prompt;
    private readonly ILogger<ReplaySessionAutoSaver> _logger;

    /// <summary>The on-disk location of the Replay auto-save bundle.
    /// Always under <c>%APPDATA%/PeakCan.Host/</c> regardless of the
    /// user's chosen save folder.</summary>
    public string AutoSavePath { get; }

    /// <summary>Production ctor: defaults the auto-save path to
    /// <c>%APPDATA%/PeakCan.Host/replay-session-auto.tmtrace</c>.</summary>
    public ReplaySessionAutoSaver(
        IReplayViewModelProvider vmProvider,
        TraceSessionLibrary library,
        IAutoSavePrefsStore prefs,
        IMessageBoxPrompt prompt,
        ILogger<ReplaySessionAutoSaver> logger)
        : this(vmProvider, library, prefs, prompt, logger, DefaultPath()) { }

    /// <summary>Test ctor with explicit path.</summary>
    public ReplaySessionAutoSaver(
        IReplayViewModelProvider vmProvider,
        TraceSessionLibrary library,
        IAutoSavePrefsStore prefs,
        IMessageBoxPrompt prompt,
        ILogger<ReplaySessionAutoSaver> logger,
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
    /// (DI disposed) or had no file loaded (nothing to save). Never
    /// throws — failures are logged at Warning.
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
            if (string.IsNullOrEmpty(vm.LoadedFilePath)) return false;
            // BuildSnapshot is sync (in-memory state). Push the JSON
            // serialization + file write off-thread so the WPF STA
            // thread isn't blocked during the ~50ms worst case.
            var dto = vm.BuildSnapshot();
            await Task.Run(() => _library.Save(dto, AutoSavePath), ct).ConfigureAwait(false);
            LogSaved(_logger, AutoSavePath, vm.LoadedFilePath!);
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
    /// corrupt. Never throws.
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
    /// Prompt the user to restore the previous auto-saved Replay
    /// session. Idempotent against the shared NeverRestore flag.
    /// </summary>
    public async Task<RestoreOutcome> ApplyAutoSnapshotAsync(
        ReplayViewModel vm,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vm);
        try
        {
            var prefs = await _prefs.LoadAsync(ct).ConfigureAwait(true);
            if (prefs.NeverRestore)
                return new RestoreOutcome(false, false, RestoreAnswer.NeverRestore);

            var load = await TryLoadAutoSnapshotAsync(ct).ConfigureAwait(true);
            if (load.Dto is null) return new RestoreOutcome(false, false, RestoreAnswer.NoFile);

            var owner = Application.Current?.MainWindow;
            var answer = await _prompt.ShowAsync(
                "Restore previous Replay session?",
                $"A Replay session was auto-saved {load.SavedAt:yyyy-MM-dd HH:mm}. Restore it?",
                owner).ConfigureAwait(true);

            if (answer != MessageBoxResult.Yes)
            {
                await _prefs.SaveAsync(
                    new AutoSavePrefs(NeverRestore: true), ct).ConfigureAwait(true);
                return new RestoreOutcome(false, true, RestoreAnswer.No);
            }

            var missing = await vm.OpenSessionAsync(load.SourceFile).ConfigureAwait(true);
            if (missing.Count > 0)
            {
                LogMissing(_logger, load.SourceFile, missing.Count);
            }
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
        return Path.Combine(appData, "PeakCan.Host", "replay-session-auto.tmtrace");
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Replay auto-save skipped: no live ReplayViewModel")]
    private static partial void LogNoVm(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Replay auto-saved {Path} for {Source}")]
    private static partial void LogSaved(ILogger logger, string path, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Replay auto-save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Replay auto-restore: {Count} .asc files missing from {Path}")]
    private static partial void LogMissing(ILogger logger, string path, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Replay ApplyAutoSnapshotAsync threw")]
    private static partial void LogApplyFailed(ILogger logger, Exception ex);
}

/// <summary>
/// v3.7.0 MINOR Chunk 3: thin seam so the Replay auto-saver doesn't
/// depend on <see cref="IServiceProvider"/> directly. Mirror of
/// <see cref="ITraceViewerViewModelProvider"/>.
/// </summary>
public interface IReplayViewModelProvider
{
    /// <summary>Return the live <see cref="ReplayViewModel"/>, or
    /// <c>null</c> if the DI container has been disposed.</summary>
    ReplayViewModel? GetCurrent();
}

/// <summary>
/// v3.7.0 MINOR Chunk 3: DI-backed <see cref="IReplayViewModelProvider"/>.
/// Singleton — owns no state.
/// </summary>
public sealed class ServiceProviderReplayViewModelProvider : IReplayViewModelProvider
{
    private readonly IServiceProvider _services;
    public ServiceProviderReplayViewModelProvider(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }
    public ReplayViewModel? GetCurrent()
    {
        return _services.GetService<ReplayViewModel>();
    }
}
