using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.Composition;

public partial class AppHostBuilder
{
    // Flow E: ViewModels batch 2 (v3.0 MINOR Task 7 + v3.2.0 MINOR + v3.5.0 MINOR + v3.6.0 MINOR + v3.6.4 PATCH + v3.7.0 MINOR + v3.10.0 MINOR + v3.11.0 MINOR + v0.8.0 + v3.0.8 + v1.2.12 PATCH Item 6 + earlier).
    // Extracted from Build() verbatim per W11 D5.

    /// <summary>
    /// Register ViewModels batch 2 (TraceViewer + TraceViewModel + SendViewModel + DbcViewModel + SignalChartViewModel + SignalViewModel + StatsViewModel + ScriptViewModel + supporting services).
    /// Extracted from Build() body as a private helper (W11 R3 mitigation).
    /// </summary>
    private void RegisterViewModelsBatch2(IServiceCollection services)
    {
        // === Range A: TraceViewer section (lines 120-223 original) ===

        // v3.0 MINOR Task 7: Trace Viewer (non-modal inspection window —
        // see docs/superpowers/specs/2026-07-03-trace-viewer-design.md).
        // ITraceViewerService follows the IReplayService precedent — singleton
        // so the ReplayTimeline + loaded ASC state is shared across consumers.
        // TraceViewerViewModel is a singleton so AppShellViewModel (also a
        // singleton) constructs with the same instance, preserving the
        // loaded trace + signal list + chart scrubber position across menu
        // round-trips. TraceViewerView itself is NOT registered with DI: WPF
        // Window ctor requires STA, and the AppShell shell already owns a
        // cached lazy field (_traceViewerView) matching the ShowReplayCommand
        // precedent — see AppShellViewModel.ShowTraceViewer for the resolve
        // path (resolves TraceViewerViewModel from DI on first show, news
        // a TraceViewerView with it).
        // v3.2.0 MINOR: TraceViewerViewModel is now backed by ITraceSessionRegistry
        // instead of a single ITraceViewerService. The per-load
        // TraceViewerService instances live inside the registry (created on
        // LoadAsync, disposed on UnloadAsync). Palette (Tableau-10) is wired
        // before the registry so the registry ctor can resolve colors.
        // ITraceViewerService is no longer registered as a singleton — the
        // registry owns its service instances.
        services.AddSingleton<PeakCan.Host.App.Services.Trace.ITracePalette,
                                       PeakCan.Host.App.Services.Trace.TableauPalette>();
        // v3.10.0 MINOR T4 (H5): bind ReplayOptions from configuration.
        // Mirrors DbcOptions / PathOptions / ScriptEngineOptions factory-closure
        // pattern so the operator can dial the ASC parser size cap via
        // appsettings.json:Replay:MaxFileSizeBytes without a recompile. When
        // the section is absent the 200 MB default (ReplayOptions.DefaultMaxFileSizeBytes)
        // is preserved, so legacy operators see no observable change.
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var maxBytes = config.GetValue<long?>("Replay:MaxFileSizeBytes")
                ?? ReplayOptions.DefaultMaxFileSizeBytes;
            return new ReplayOptions(MaxFileSizeBytes: maxBytes);
        });
        // v3.10.0 MINOR T4 (H5): inject the configured ReplayOptions into
        // the registry so each per-load TraceViewerService receives the
        // operator's appsettings.json override. Pre-fix, the registry's
        // 2-arg ctor used ReplayOptions.Default internally and the DI
        // binding above was silently discarded — configurability goal unmet.
        services.AddSingleton<PeakCan.Host.App.Services.Trace.ITraceSessionRegistry,
                                       PeakCan.Host.App.Services.Trace.TraceSessionRegistry>(sp =>
            new PeakCan.Host.App.Services.Trace.TraceSessionRegistry(
                sp.GetRequiredService<PeakCan.Host.App.Services.Trace.ITracePalette>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<ReplayOptions>()));
        // TraceViewerViewModel requires ILogger<T> + DbcService + ITraceSessionRegistry.
        // DbcService is registered above (singleton, AddSingleton with factory);
        // the logger is auto-wired by Microsoft.Extensions.Hosting.
        services.AddSingleton<TraceViewerViewModel>();
        // v3.5.0 MINOR: persists Trace Viewer multi-trace sessions to .tmtrace
        // bundle files. Consumed by TraceViewerViewModel.SaveSessionAsync /
        // OpenSessionAsync commands.
        services.AddSingleton<PeakCan.Host.App.Services.Trace.TraceSessionLibrary>();
        // v3.11.0 MINOR T2 (H7): shared BuildSnapshot logic for Trace +
        // Replay VMs. Both VMs delegate the scalar envelope + content-hash
        // computation to this helper; VM-specific Sources / Playback /
        // Viewports stay on the caller. Singleton because it owns no
        // state (only a hasher reference + a logger).
        services.AddSingleton<PeakCan.Host.App.Services.Trace.TraceSessionSnapshotBuilder>();
        // v3.6.4 PATCH: hash-based .asc relocation. IAscContentHasher
        // computes SHA-256 of an .asc's contents (stored alongside the
        // path in the bundle); IAscLocator walks user-known directories
        // when the recorded path is missing and recovers the file via
        // hash match. The search-dir list lives at
        // %APPDATA%/PeakCan.Host/asc-search-dirs.json (a future MINOR
        // can add a Settings UI; this PATCH keeps the surface minimal).
        services.AddSingleton<PeakCan.Host.Core.Services.IAscContentHasher,
                                       PeakCan.Host.Core.Services.Sha256AscContentHasher>();
        services.AddSingleton<PeakCan.Host.Core.Services.IAscLocator,
                                       PeakCan.Host.Core.Services.FileSystemAscLocator>();
        // v3.6.0 MINOR T3: most-recently-used list backing the AppShell
        // File ▸ Open Recent menu. Singleton so AppShell and any future
        // consumer (e.g. keyboard shortcut handler) observe the same
        // ordering. Persists to %APPDATA%/PeakCan.Host/recent-sessions.json
        // via internal DefaultPath (mirrors TraceSessionLibrary pattern).
        services.AddSingleton<PeakCan.Host.App.Services.Trace.RecentSessionsService>();

        // v3.6.0 MINOR T2: auto-save + auto-restore prompt. Wired into
        // App.OnExit (flush) and App.OnStartup (prompt) so users get
        // their session back across app restarts without manual Save.
        services.AddSingleton<
            PeakCan.Host.App.Services.Trace.TraceSessionAutoSaver>();
        services.AddSingleton<
            PeakCan.Host.App.Services.Trace.ITraceViewerViewModelProvider,
            PeakCan.Host.App.Services.Trace.ServiceProviderTraceViewerViewModelProvider>();
        // v3.7.0 MINOR Chunk 3: Replay auto-save + restore-prompt. Shares
        // the same IAutoSavePrefsStore / IMessageBoxPrompt singletons as
        // Trace (one opt-out flag for both tabs).
        services.AddSingleton<
            PeakCan.Host.App.Services.Trace.ReplaySessionAutoSaver>();
        services.AddSingleton<
            PeakCan.Host.App.Services.Trace.IReplayViewModelProvider,
            PeakCan.Host.App.Services.Trace.ServiceProviderReplayViewModelProvider>();
        services.AddSingleton<
            PeakCan.Host.App.Services.Trace.IAutoSavePrefsStore>(sp =>
            new PeakCan.Host.App.Services.Trace.FileAutoSavePrefsStore(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PeakCan.Host", "auto-save-prefs.json"),
                sp.GetRequiredService<ILogger<PeakCan.Host.App.Services.Trace.FileAutoSavePrefsStore>>()));
        services.AddSingleton<
            PeakCan.Host.App.Services.Trace.IMessageBoxPrompt,
            PeakCan.Host.App.Services.Trace.WpfMessageBoxPrompt>();

        // === Range B: TraceViewModel + SendViewModel + DbcViewModel + SignalChartViewModel + SignalViewModel + StatsViewModel + ScriptViewModel ===

        services.AddSingleton<TraceViewModel>();
        // A4 orphan PATCH (v3.0.8): SendViewModel needs a
        // Func<long> that returns the current rate-limit rejected
        // frame count. Resolved by pattern-matching the registered
        // SendService against RateLimitedSendService. The null branch
        // is defensive-only — current DI always wraps the SendService
        // in a RateLimitedSendService decorator (line 175-180 below
        // registers a factory that always returns the decorator, even
        // when MaxFramesPerSecond=0 the decorator instance is still
        // constructed and its RejectedFrameCount stays at 0). The
        // pattern-match is future-proofing against a DI refactor that
        // might bypass the decorator for callers that opt out of rate
        // limiting.
        services.AddSingleton<SendViewModel>(sp =>
        {
            var sendSvc = sp.GetRequiredService<PeakCan.Host.App.Services.SendService>();
            Func<long>? rejectedCountProvider = sendSvc is PeakCan.Host.App.Services.RateLimitedSendService rateLimited
                ? () => rateLimited.RejectedFrameCount
                : null;
            return new SendViewModel(
                sendSvc,
                sp.GetRequiredService<ILogger<SendViewModel>>(),
                sp.GetRequiredService<ICyclicSendService>(),
                sp.GetService<SendFrameLibrary>(),
                dbcSend: sp.GetRequiredService<DbcSendViewModel>(),
                multiFrameVm: sp.GetRequiredService<MultiFrameSendViewModel>(),
                rateLimitRejectedCountProvider: rejectedCountProvider);
        });
        // v1.2.12 PATCH Item 6: also register as IHostedService so the
        // host disposes it on shutdown (same rationale as RecordViewModel).
        services.AddHostedService(sp => sp.GetRequiredService<SendViewModel>());
        services.AddSingleton<DbcViewModel>();
        // v0.8.0: signal chart VM must be registered before SignalViewModel
        // (SignalViewModel depends on it via constructor injection).
        services.AddSingleton<SignalChartViewModel>();
        services.AddSingleton<SignalViewModel>();
        // v1.2.12 PATCH Item 6: also register as IHostedService so the
        // host disposes the System.Threading.Timer drain on shutdown.
        // Without this, the timer's strong ref to the VM keeps the VM
        // alive across STA-WPF xunit fixtures and after shell teardown.
        services.AddHostedService(sp => sp.GetRequiredService<SignalViewModel>());
        services.AddSingleton<StatsViewModel>();
        services.AddSingleton<ScriptViewModel>();
    }
}