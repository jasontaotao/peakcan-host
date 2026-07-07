using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.10.0 MINOR T2 (C3): Replay-specific thin subclass of
/// <see cref="SessionAutoSaver{TVm}"/>. Owns only the config that
/// differentiates Replay from Trace (provider, "has content" check,
/// restore prompt title, auto-save path). All orchestration lives in
/// the base class.
/// <para>
/// Mirrors <see cref="TraceSessionAutoSaver"/> for the Replay tab. Uses
/// the SAME <see cref="AutoSavePrefs"/> file as Trace — opting out of
/// auto-restore once opts out for BOTH tabs. The auto-save bundle file
/// is separate (<c>replay-session-auto.tmtrace</c> vs.
/// <c>trace-session-auto.tmtrace</c>).
/// </para>
/// </summary>
public sealed partial class ReplaySessionAutoSaver : SessionAutoSaver<ReplayViewModel>
{
    private readonly IReplayViewModelProvider _vmProvider;

    /// <summary>Production ctor: defaults the auto-save path to
    /// <c>%APPDATA%/PeakCan.Host/replay-session-auto.tmtrace</c>.</summary>
    public ReplaySessionAutoSaver(
        IReplayViewModelProvider vmProvider,
        TraceSessionLibrary library,
        IAutoSavePrefsStore prefs,
        IMessageBoxPrompt prompt,
        ILogger<ReplaySessionAutoSaver> logger)
        : base(library, prefs, prompt, logger, DefaultPath())
    {
        _vmProvider = vmProvider ?? throw new ArgumentNullException(nameof(vmProvider));
    }

    /// <summary>Test ctor with explicit path.</summary>
    public ReplaySessionAutoSaver(
        IReplayViewModelProvider vmProvider,
        TraceSessionLibrary library,
        IAutoSavePrefsStore prefs,
        IMessageBoxPrompt prompt,
        ILogger<ReplaySessionAutoSaver> logger,
        string autoSavePath)
        : base(library, prefs, prompt, logger, autoSavePath)
    {
        _vmProvider = vmProvider ?? throw new ArgumentNullException(nameof(vmProvider));
    }

    protected override ReplayViewModel? GetActiveVm() => _vmProvider.GetCurrent();

    protected override bool HasContentToSave(ReplayViewModel vm) =>
        !string.IsNullOrEmpty(vm.LoadedFilePath);

    protected override TraceSessionBundleDto BuildSnapshot(ReplayViewModel vm) =>
        vm.BuildSnapshot();

    protected override Task<IReadOnlyList<string>> ApplySnapshotToVmAsync(
        ReplayViewModel vm, string sourceFile) =>
        vm.OpenSessionAsync(sourceFile);

    protected override string RestorePromptTitle =>
        "Restore previous Replay session?";

    protected override string FormatRestoreMessage(DateTimeOffset savedAt) =>
        savedAt == DateTimeOffset.MinValue
            ? "A previously-saved Replay session was found. Restore it?"
            : $"A Replay session was auto-saved {savedAt:yyyy-MM-dd HH:mm}. Restore it?";

    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PeakCan.Host", "replay-session-auto.tmtrace");
    }

    // v3.10.0 T2 R1: log hooks are plain methods (not partial), so
    // they can override the base virtual hooks across inheritance.
    // They forward to [LoggerMessage] source-gen partials declared at
    // the bottom of this file.
    protected override void OnNoVm() => LogNoVm(Logger);
    protected override void OnSaved(string path, int sourcesCount)
    {
        // v3.10.0 T2: the base's OnSaved signature uses sourcesCount
        // for consistency with Trace; Replay historically exposed the
        // loaded .asc as "Source". Forward as-is.
        LogSaved(Logger, path, sourcesCount);
    }
    protected override void OnSaveFailed(Exception ex, string path) =>
        LogSaveFailed(Logger, ex, path);
    protected override void OnMissing(string path, int count) =>
        LogMissing(Logger, path, count);
    protected override void OnApplyFailed(Exception ex) =>
        LogApplyFailed(Logger, ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Replay auto-save skipped: no live ReplayViewModel")]
    private static partial void LogNoVm(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Replay auto-saved {Path} ({SourcesCount} source)")]
    private static partial void LogSaved(ILogger logger, string path, int sourcesCount);

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
