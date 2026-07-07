using System.Globalization;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Path;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Statistics;
using Serilog;

namespace PeakCan.Host.App.Composition;

/// <summary>
/// Composes the WPF process: a file-rotating Serilog logger, the
/// <see cref="ChannelRouter"/> + <see cref="BusStatisticsCollector"/> from
/// Infrastructure, the App-layer services and view-models, and the
/// <see cref="AppShell"/> window.
/// <para>
/// <see cref="Build"/> is idempotent only with respect to DI: it may be
/// called once at startup, and the returned <see cref="IHost"/> owns the
/// Serilog lifetime (it is disposed when the host is disposed).
/// </para>
/// <para>
/// Side effects on <see cref="Log.Logger"/>: this method sets the global
/// static Serilog logger. Tests that need a clean Serilog state must
/// reset it themselves; the production app does not care.
/// </para>
/// </summary>
/// <remarks>
/// v1.3.1 PATCH Item 3: <see cref="AppHostBuilder"/> is an instance class
/// (not static) so it can carry optional configuration state across
/// fluent builder method calls. v1.3.0 MINOR Item 5 introduced
/// <see cref="WithUdsSecurityLockoutConfig"/>, the first fluent setter
/// requiring per-builder state. Future setters will follow the same
/// pattern.
/// <para>
/// <b>Lifecycle:</b> create one builder per application instance. Call
/// <see cref="Build"/> exactly once. The returned <see cref="IHost"/>
/// owns the Serilog lifetime and the DI container; dispose the host
/// (not the builder) when the app shuts down. Do not reuse a builder
/// after <see cref="Build"/> has been called.
/// </para>
/// <para>
/// <b>Pattern alignment:</b> follows the
/// <see href="https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host">
/// Microsoft.Extensions.Hosting IHost builder pattern</see>. The fluent
/// <c>With*</c> setters configure optional services; <see cref="Build"/>
/// resolves them into the DI container and starts the host. The DI
/// factory branches on optional state (e.g.
/// <c>_udsSecurityLockoutConfig is { } lockoutConfig</c>) to preserve
/// the default policy for legacy callers that do not invoke the
/// corresponding <c>With*</c> setter.
/// </para>
/// </remarks>
public class AppHostBuilder
{
    /// <summary>
    /// PEAK PCAN-USB FD first-channel handle. Per the inline amendment to
    /// Task 12, MVP probes a single hardcoded handle and does not
    /// enumerate; v1.1 will add multi-channel enumeration.
    /// </summary>
    public const ushort PcanUsbFdFirstHandle = 0x51;

    // v1.3.0 MINOR Item 5: optional UDS SecurityAccess lockout policy.
    // Set via WithUdsSecurityLockoutConfig; null means use the default
    // (UdsSecurityLockoutConfig.Default = 3 attempts / 5 s) inside the
    // UdsClient ctor.
    private PeakCan.Host.Core.Uds.UdsSecurityLockoutConfig? _udsSecurityLockoutConfig;

    /// <summary>
    /// v1.3.0 MINOR Item 5: configure the UDS SecurityAccess lockout
    /// policy. Must be called before <see cref="Build"/>.
    /// <para>
    /// When this builder method is not called, the default policy
    /// (<see cref="PeakCan.Host.Core.Uds.UdsSecurityLockoutConfig.Default"/>:
    /// 3 attempts / 5 s) is used. This preserves backward compatibility
    /// with v1.2.x callers.
    /// </para>
    /// </summary>
    /// <param name="config">Lockout policy (MaxAttempts + LockoutDuration).</param>
    /// <returns>The same builder, for fluent chaining.</returns>
    public AppHostBuilder WithUdsSecurityLockoutConfig(PeakCan.Host.Core.Uds.UdsSecurityLockoutConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _udsSecurityLockoutConfig = config;
        return this;
    }

    public IHost Build()
    {
        // v3.9.0 MINOR P5: create the IHostBuilder FIRST so its
        // IConfiguration (populated from appsettings.json +
        // environment variables + command line) is available to
        // Serilog's ReadFrom.Configuration. Pre-fix, the LoggerConfiguration
        // was self-contained and didn't read from the host's config.
        // The order matters: Serilog reads the Serilog section from
        // the configuration the host built, so the host's appsettings.json
        // must be loaded BEFORE CreateLogger is called.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

        // v3.9.0 MINOR P5: the log directory is now created by Serilog
        // itself when it opens the WriteTo.File sink (the sink's path
        // is configured via appsettings.json's Serilog:WriteTo:Args:path).
        // The default appsettings.json ships
        // LocalAppData/PeakCan.Host/logs/peak-.log as the path, which
        // matches the v3.8.0-v3.8.8 hardcoded behavior.
        Log.Logger = new LoggerConfiguration()
            // v3.9.0 MINOR P5: ReadFrom.Configuration replaces the
            // hardcoded MinimumLevel.Information() + WriteTo.File(...)
            // chain. The operator can now edit appsettings.json's
            // Serilog section to override MinLevel (e.g. bump to
            // "Debug" for production debugging) + add sinks + add
            // enrichers without recompiling. The default appsettings.json
            // ships a Serilog section that mirrors the prior hardcoded
            // behavior (MinimumLevel=Information, WriteTo=File with
            // rollingInterval=Day, retainedFileCountLimit=14) so the
            // observable behavior is unchanged when the operator
            // doesn't edit the config.
            //
            // Migration note: the hardcoded WriteTo.File(rollingInterval:Day,
            // retainedFileCountLimit:14) call is REMOVED. If the operator
            // needs a different rolling interval or retention, they edit
            // the Serilog:WriteTo section in appsettings.json. The
            // formatProvider (CultureInfo.InvariantCulture) and the
            // logPath pattern (LocalAppData/PeakCan.Host/logs/peak-.log)
            // are preserved in the default appsettings.json.
            .ReadFrom.Configuration(builder.Configuration)
            .CreateLogger();
        builder.Logging.ClearProviders().AddSerilog(Log.Logger, dispose: true);

        // v1.5.0 MINOR: expose the host's IConfiguration as a singleton so
        // the AppShellViewModel can persist SelectedChannel to
        // Channel:SelectedHandle in appsettings.json. Host.CreateApplicationBuilder
        // already populates builder.Configuration with appsettings.json +
        // environment variables + command line.
        builder.Services.AddSingleton<IConfiguration>(builder.Configuration);

        // Core infrastructure
        // v1.2.12 PATCH Item 11: ChannelRouter now accepts an ILogger<ChannelRouter>
        // so the secondary OnError catch (which auto-detaches misbehaving sinks)
        // is observable in Release builds. The logger is optional in the ctor
        // (NullLogger fallback) but production DI always wires one.
        builder.Services.AddSingleton<ChannelRouter>(sp =>
            new ChannelRouter(sp.GetRequiredService<ILogger<ChannelRouter>>()));
        builder.Services.AddSingleton<BusStatisticsCollector>();
        // v3.5.2 PATCH: ITimerFactory seam so RecordService +
        // StatisticsService can be unit-tested with a deterministic
        // FakeTimerFactory (no wall-clock dependency). v3.5.4 PATCH:
        // switched to CyclicTimerFactory so the same singleton handles
        // both IPeriodicTimer (RecordService/StatisticsService/TraceService)
        // and ICyclicTimer (CyclicSendService/CyclicDbcSendService). The
        // factory is stateless — only the dispatch shape differs.
        builder.Services.AddSingleton<PeakCan.Host.Core.Services.ITimerFactory,
                                      PeakCan.Host.Core.Services.CyclicTimerFactory>();
        // Task 18: extracted PEAK SDK probe call into a swappable
        // service so the App assembly has no Peak.Can.Basic dependency
        // (enforced by LayeringRulesTests.App_Should_Not_Depend_On_Peak_Can_Basic).
        builder.Services.AddSingleton<PeakCan.Host.Core.IChannelProbe,
                                       PeakCan.Host.Infrastructure.Peak.PeakChannelProbe>();

        // v0.4.0: multi-channel enumerator. Probes PCAN-USB 1–16.
        builder.Services.AddSingleton<PeakCan.Host.Core.IChannelEnumerator,
                                       PeakCan.Host.Infrastructure.Peak.PeakChannelEnumerator>();

        // Task T3 (H4): the App-layer VM no longer news PeakCanChannel
        // directly; it asks the factory for an ICanChannel. Production DI
        // binds the PEAK implementation; tests inject a fake to drive the
        // connect/disconnect state machine without hardware.
        builder.Services.AddSingleton<PeakCan.Host.Core.IChannelFactory,
                                      PeakCan.Host.Infrastructure.Peak.PeakCanChannelFactory>();

        // v0.4.0: IPcanReader abstracts the PEAK SDK read calls so
        // PeakCanChannel's read loop can be unit-tested without hardware.
        builder.Services.AddSingleton<PeakCan.Host.Infrastructure.Peak.IPcanReader,
                                      PeakCan.Host.Infrastructure.Peak.PcanReader>();

        // App services
        // v1.2.12 PATCH Item 11: TraceService now takes an ILogger<TraceService>
        // so its OnError path is observable in Release builds (Debug.WriteLine
        // was previously stripped). Production DI resolves the logger from
        // the host's LoggingServiceCollection; NullLogger fallback exists
        // for tests that do not assert on log output.
        builder.Services.AddSingleton<TraceService>(sp =>
            new TraceService(
                sp.GetRequiredService<TraceViewModel>(),
                sp.GetRequiredService<ILogger<TraceService>>()));
        // TraceService is a BackgroundService; its 50ms drain loop lives in
        // ExecuteAsync and only fires when the host starts it. Without this
        // AddHostedService line, frames pile up in the bounded channel and
        // TotalFrameCount stays 0 even though fan-out delivers them. Same
        // bug pattern as the IHost.StartAsync miss in App.xaml.cs.
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TraceService>());
        // v1.6.5 PATCH Item 1: token-bucket send-rate-limit decorator.
        // Register CoreSendService (raw, exempt from rate-limit) and a
        // SendService factory that wraps it with RateLimitedSendService.
        // UI callers (CanApi, SendViewModel, DbcSendViewModel,
        // CyclicSendService, CyclicDbcSendService) resolve SendService
        // via C# polymorphism and receive the decorator; Replay +
        // IsoTp resolve CoreSendService directly (see below).
        builder.Services.AddSingleton<CoreSendService>();
        builder.Services.AddSingleton<SendService>(sp => new RateLimitedSendService(
            inner: sp.GetRequiredService<CoreSendService>(),
            maxFramesPerSecond: sp.GetRequiredService<IConfiguration>()
                .GetValue<int>("Send:MaxFramesPerSecond"),
            logger: sp.GetRequiredService<ILogger<RateLimitedSendService>>()));
        // v1.6.6 PATCH Item 1: DBC size + message-count caps, opt-in via
        // appsettings.json:Dbc section. DbcOptions.Unlimited when both
        // caps are 0 or absent — back-compat baseline preserved.
        builder.Services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>().GetSection("Dbc");
            return new DbcOptions(
                MaxFileSizeBytes: config.GetValue<long>("MaxFileSizeBytes"),
                MaxMessageCount: config.GetValue<int>("MaxMessageCount"));
        });
        builder.Services.AddSingleton<DbcService>(sp =>
            new DbcService(
                sp.GetRequiredService<ILogger<DbcService>>(),
                sp.GetRequiredService<DbcOptions>()));
        // v1.6.10 PATCH Item 2: opt-in extension of v1.6.4 PATCH hardcoded
        // allowlist — config-driven via Path:AllowedRoots:[] in appsettings.json.
        builder.Services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>().GetSection("Path");
            var allowedRoots = config.GetSection("AllowedRoots").Get<string[]>()
                ?? new[] { PathNormalizer.LocalAppDataPeakCanRoot };
            return new PathOptions(allowedRoots);
        });
        // v1.7.0 MINOR Item 1: V8 isolate resource caps for the script
        // engine. Bound from appsettings.json:Script section — see
        // ScriptEngineOptions for default values (64 MB heap, 16 MiB
        // new, 48 MiB old). Mirrors DbcOptions factory-closure pattern.
        builder.Services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>().GetSection("Script");
            return new PeakCan.Host.App.Services.Scripting.ScriptEngineOptions(
                MaxHeapSizeMB: config.GetValue<int?>("MaxHeapSizeMB") ?? 64,
                MaxNewSpaceSizeMB: config.GetValue<int?>("MaxNewSpaceSizeMB") ?? 16,
                MaxOldSpaceSizeMB: config.GetValue<int?>("MaxOldSpaceSizeMB") ?? 48);
        });
        builder.Services.AddSingleton<StatisticsService>();
        // StatisticsService is a BackgroundService; its 1Hz snapshot loop
        // needs IHostedService registration too. Without this, Stats view
        // never refreshes — matches TraceService bug pattern.
        builder.Services.AddHostedService(sp => sp.GetRequiredService<StatisticsService>());
        // v0.5.0: frame recording (ASC/CSV) and cyclic send.
        builder.Services.AddSingleton<RecordService>();
        // v1.2.12 PATCH Item 5: RecordService is a BackgroundService; its
        // writer thread + 1Hz flush need IHostedService registration.
        // Without this, ExecuteAsync never starts and frames never reach disk.
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RecordService>());
        builder.Services.AddSingleton<CyclicSendService>();
        // v1.2.11 PATCH Item 3: register cyclic service as its interface
        // so SendViewModel can be tested with a fake via ICyclicSendService.
        builder.Services.AddSingleton<ICyclicSendService>(sp =>
            sp.GetRequiredService<CyclicSendService>());
        // v1.2.11 PATCH Item 5: named-frame library persistence.
        builder.Services.AddSingleton<SendFrameLibrary>();

        // v1.4.0 MINOR Replay: ReplayFrameSinkAdapter wraps SendService so
        // replay frames traverse the same outbound path as the SendView
        // (the adapter's XML doc explains why SendService and not
        // ChannelRouter — ChannelRouter is receive-only fan-out).
        // v1.6.5 PATCH Item 1: REPLAY IS EXEMPT from rate-limit —
        // timeline-driven playback must honor ASC timestamps; gating
        // it would scramble the timeline. Inject CoreSendService (raw)
        // instead of letting DI auto-resolve SendService (which would
        // be the RateLimitedSendService decorator).
        // Register the concrete type first so the IReplayFrameSink factory
        // and any direct IReplayService consumer share the same instance.
        builder.Services.AddSingleton<ReplayFrameSinkAdapter>(sp =>
            new ReplayFrameSinkAdapter(sp.GetRequiredService<CoreSendService>()));
        builder.Services.AddSingleton<IReplayFrameSink>(sp =>
            sp.GetRequiredService<ReplayFrameSinkAdapter>());
        builder.Services.AddSingleton<IReplayService, ReplayService>();
        // v1.4.0 MINOR Send DBC: stateless DbcEncodeService singleton
        // shared by DbcSendViewModel.
        builder.Services.AddSingleton<DbcEncodeService>();
        // v1.5.1 PATCH Item 2 (Periodic DBC send): independent service
        // per Decision 7 — does NOT share code with CyclicSendService.
        // Register concrete type first so the IReplayService-style
        // "interface and concrete resolve to same instance" pattern is
        // preserved.
        builder.Services.AddSingleton<CyclicDbcSendService>();
        builder.Services.AddSingleton<ICyclicDbcSendService>(sp =>
            sp.GetRequiredService<CyclicDbcSendService>());
        // v2.0.6 PATCH Bug-2: DbcSendViewModel must be registered as a
        // singleton so SendViewModel.DbcSend resolves to a real instance
        // in production (its ctor param defaults to null and DI never
        // wired it up). Without this, SendView.xaml's
        // DataContext="{Binding DbcSend}" evaluates to null, the DBC
        // sub-panel's ComboBox / DataGrid / Send button all bind to
        // nothing, and "DBC mode" appears empty even after a successful
        // DBC load. Dependencies (DbcEncodeService, SendService,
        // DbcService, ICyclicDbcSendService) are registered above.
        builder.Services.AddSingleton<DbcSendViewModel>(sp =>
        {
            var sendSvc = sp.GetRequiredService<PeakCan.Host.App.Services.SendService>();
            Func<long>? rejectedCountProvider = sendSvc is PeakCan.Host.App.Services.RateLimitedSendService rateLimited
                ? () => rateLimited.RejectedFrameCount
                : null;
            return new DbcSendViewModel(
                sp.GetRequiredService<DbcEncodeService>(),
                sendSvc,
                sp.GetRequiredService<DbcService>(),
                sp.GetRequiredService<ICyclicDbcSendService>(),
                // v3.1.0 MINOR: real ILogger<> (W1 silent-log fix).
                sp.GetRequiredService<ILogger<DbcSendViewModel>>(),
                rateLimitRejectedCountProvider: rejectedCountProvider);
        });
        // v2.1.0 MINOR: multi-frame sequence send. SequenceSendService
        // wraps SendService for concurrent/sequential frame dispatch;
        // MultiFrameSendViewModel drives the non-modal window's UI.
        // The Window itself is NOT DI-registered — WPF Window
        // construction requires STA + live Application, so DI
        // resolution throws on the test thread. SendViewModel
        // lazy-creates the Window on first OpenMultiFrameSend call.
        // v2.1.1 PATCH: SequenceSendService now also depends on
        // DbcEncodeService + DbcService for DBC-row encoding; the
        // MultiFrameSendViewModel depends on DbcService for the
        // message picker. Both already registered above.
        builder.Services.AddSingleton<PeakCan.Host.App.Services.MultiFrame.SequenceSendService>(sp =>
            new PeakCan.Host.App.Services.MultiFrame.SequenceSendService(
                sp.GetRequiredService<PeakCan.Host.App.Services.SendService>(),
                sp.GetRequiredService<PeakCan.Host.Core.Dbc.DbcEncodeService>(),
                sp.GetRequiredService<PeakCan.Host.App.Services.DbcService>()));
        // v2.1.2 PATCH: SequenceLibrary persists named sequences to
        // %APPDATA%\PeakCan.Host\sequences.json. Wired into the
        // multi-frame VM factory so SaveCurrent / LoadSaved / DeleteSaved
        // commands reach the library.
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Sequence.SequenceLibrary>();
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.MultiFrameSendViewModel>(sp =>
        {
            var sendSvc = sp.GetRequiredService<PeakCan.Host.App.Services.SendService>();
            Func<long>? rejectedCountProvider = sendSvc is PeakCan.Host.App.Services.RateLimitedSendService rateLimited
                ? () => rateLimited.RejectedFrameCount
                : null;
            return new PeakCan.Host.App.ViewModels.MultiFrameSendViewModel(
                sp.GetRequiredService<PeakCan.Host.App.Services.MultiFrame.SequenceSendService>(),
                sp.GetRequiredService<PeakCan.Host.App.Services.DbcService>(),
                sp.GetRequiredService<PeakCan.Host.App.Services.Sequence.SequenceLibrary>(),
                // v3.1.0 MINOR: real ILogger<> (W1 silent-log fix).
                sp.GetRequiredService<ILogger<PeakCan.Host.App.ViewModels.MultiFrameSendViewModel>>(),
                rateLimitRejectedCountProvider: rejectedCountProvider);
        });
        // v1.2.11 PATCH Item 6: Recording tab VM (wraps RecordService).
        // v1.2.12 PATCH Item 6: also register as IHostedService so the
        // host disposes it on shutdown — the VM's DispatcherTimer would
        // otherwise keep ticking (and keep the VM alive) until process
        // exit, leaking in STA-WPF xunit fixtures and across shell
        // navigation in production.
        builder.Services.AddSingleton<RecordViewModel>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RecordViewModel>());
        // v2.1.4 PATCH: Replay tab VM. Closes the v1.4.0 MINOR orphan —
        // ReplayView + IReplayService were wired but ReplayViewModel
        // itself was never registered, so AppShell could not navigate to
        // the tab. Standard AddSingleton matches the RecordViewModel
        // precedent (no IHostedService — ReplayVM has no Dispose-time
        // background timer that needs host shutdown).
        builder.Services.AddSingleton<ReplayViewModel>();

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
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Trace.ITracePalette,
                                       PeakCan.Host.App.Services.Trace.TableauPalette>();
        // v3.10.0 MINOR T4 (H5): bind ReplayOptions from configuration.
        // Mirrors DbcOptions / PathOptions / ScriptEngineOptions factory-closure
        // pattern so the operator can dial the ASC parser size cap via
        // appsettings.json:Replay:MaxFileSizeBytes without a recompile. When
        // the section is absent the 200 MB default (ReplayOptions.DefaultMaxFileSizeBytes)
        // is preserved, so legacy operators see no observable change.
        builder.Services.AddSingleton(sp =>
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
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Trace.ITraceSessionRegistry,
                                       PeakCan.Host.App.Services.Trace.TraceSessionRegistry>(sp =>
            new PeakCan.Host.App.Services.Trace.TraceSessionRegistry(
                sp.GetRequiredService<PeakCan.Host.App.Services.Trace.ITracePalette>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<ReplayOptions>()));
        // TraceViewerViewModel requires ILogger<T> + DbcService + ITraceSessionRegistry.
        // DbcService is registered above (singleton, AddSingleton with factory);
        // the logger is auto-wired by Microsoft.Extensions.Hosting.
        builder.Services.AddSingleton<TraceViewerViewModel>();
        // v3.5.0 MINOR: persists Trace Viewer multi-trace sessions to .tmtrace
        // bundle files. Consumed by TraceViewerViewModel.SaveSessionAsync /
        // OpenSessionAsync commands.
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Trace.TraceSessionLibrary>();
        // v3.11.0 MINOR T2 (H7): shared BuildSnapshot logic for Trace +
        // Replay VMs. Both VMs delegate the scalar envelope + content-hash
        // computation to this helper; VM-specific Sources / Playback /
        // Viewports stay on the caller. Singleton because it owns no
        // state (only a hasher reference + a logger).
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Trace.TraceSessionSnapshotBuilder>();
        // v3.6.4 PATCH: hash-based .asc relocation. IAscContentHasher
        // computes SHA-256 of an .asc's contents (stored alongside the
        // path in the bundle); IAscLocator walks user-known directories
        // when the recorded path is missing and recovers the file via
        // hash match. The search-dir list lives at
        // %APPDATA%/PeakCan.Host/asc-search-dirs.json (a future MINOR
        // can add a Settings UI; this PATCH keeps the surface minimal).
        builder.Services.AddSingleton<PeakCan.Host.Core.Services.IAscContentHasher,
                                       PeakCan.Host.Core.Services.Sha256AscContentHasher>();
        builder.Services.AddSingleton<PeakCan.Host.Core.Services.IAscLocator,
                                       PeakCan.Host.Core.Services.FileSystemAscLocator>();
        // v3.6.0 MINOR T3: most-recently-used list backing the AppShell
        // File ▸ Open Recent menu. Singleton so AppShell and any future
        // consumer (e.g. keyboard shortcut handler) observe the same
        // ordering. Persists to %APPDATA%/PeakCan.Host/recent-sessions.json
        // via internal DefaultPath (mirrors TraceSessionLibrary pattern).
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Trace.RecentSessionsService>();

        // v3.6.0 MINOR T2: auto-save + auto-restore prompt. Wired into
        // App.OnExit (flush) and App.OnStartup (prompt) so users get
        // their session back across app restarts without manual Save.
        builder.Services.AddSingleton<
            PeakCan.Host.App.Services.Trace.TraceSessionAutoSaver>();
        builder.Services.AddSingleton<
            PeakCan.Host.App.Services.Trace.ITraceViewerViewModelProvider,
            PeakCan.Host.App.Services.Trace.ServiceProviderTraceViewerViewModelProvider>();
        // v3.7.0 MINOR Chunk 3: Replay auto-save + restore-prompt. Shares
        // the same IAutoSavePrefsStore / IMessageBoxPrompt singletons as
        // Trace (one opt-out flag for both tabs).
        builder.Services.AddSingleton<
            PeakCan.Host.App.Services.Trace.ReplaySessionAutoSaver>();
        builder.Services.AddSingleton<
            PeakCan.Host.App.Services.Trace.IReplayViewModelProvider,
            PeakCan.Host.App.Services.Trace.ServiceProviderReplayViewModelProvider>();
        builder.Services.AddSingleton<
            PeakCan.Host.App.Services.Trace.IAutoSavePrefsStore>(sp =>
            new PeakCan.Host.App.Services.Trace.FileAutoSavePrefsStore(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PeakCan.Host", "auto-save-prefs.json"),
                sp.GetRequiredService<ILogger<PeakCan.Host.App.Services.Trace.FileAutoSavePrefsStore>>()));
        builder.Services.AddSingleton<
            PeakCan.Host.App.Services.Trace.IMessageBoxPrompt,
            PeakCan.Host.App.Services.Trace.WpfMessageBoxPrompt>();

        // v0.7.0: file dialog abstraction for testability.
        builder.Services.AddSingleton<PeakCan.Host.Core.IFileDialogService,
                                       PeakCan.Host.App.Services.WpfFileDialogService>();
        // M11: DBC lookup + signal decode runs off the SDK read thread on
        // its own worker. Registered as both a singleton (so SinkWiringService
        // gets the same instance the host starts) and a hosted service
        // (so BackgroundService.StartAsync fires the worker loop).
        // v1.2.11 PATCH Item 2: factory takes TraceViewModel for fan-out
        // (worker fills entry.Decoded after looking up PendingDecode).
        // v1.2.12 PATCH Item 11: factory now also takes ILogger so OnError
        // is observable in Release builds.
        builder.Services.AddSingleton<DbcDecodeBackgroundService>(sp =>
            new DbcDecodeBackgroundService(
                sp.GetRequiredService<DbcService>(),
                sp.GetRequiredService<SignalViewModel>(),
                sp.GetRequiredService<TraceViewModel>(),
                sp.GetRequiredService<ILogger<DbcDecodeBackgroundService>>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DbcDecodeBackgroundService>());

        // v1.0.0: Scripting engine.
        // ScriptEngine has a circular dependency with ScriptUtilities (ScriptEngine
        // needs ScriptUtilities for logging, ScriptUtilities needs ScriptEngine for
        // output routing). Break the cycle by registering ScriptEngine first with a
        // factory that lazily resolves ScriptUtilities.
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.ScriptEngine>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<PeakCan.Host.App.Services.Scripting.ScriptEngine>>();
            var canApi = sp.GetService<PeakCan.Host.App.Services.Scripting.CanApi>();
            var dbcApi = sp.GetService<PeakCan.Host.App.Services.Scripting.DbcApi>();
            // v1.7.0 MINOR Item 1: V8 isolate resource caps.
            var scriptEngineOptions = sp.GetRequiredService<PeakCan.Host.App.Services.Scripting.ScriptEngineOptions>();
            // ScriptUtilities will be resolved lazily to break the cycle.
            PeakCan.Host.App.Services.Scripting.ScriptUtilities? utilities = null;
            var engine = new PeakCan.Host.App.Services.Scripting.ScriptEngine(
                logger, canApi, dbcApi, null, scriptEngineOptions);
            // Now create ScriptUtilities with the engine reference.
            utilities = new PeakCan.Host.App.Services.Scripting.ScriptUtilities(
                sp.GetRequiredService<ILogger<PeakCan.Host.App.Services.Scripting.ScriptUtilities>>(),
                engine);
            // Update the engine's utilities field via reflection.
            var field = typeof(PeakCan.Host.App.Services.Scripting.ScriptEngine)
                .GetField("_utilities", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(engine, utilities);
            return engine;
        });
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.CanApi>();
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.DbcApi>();
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.ScriptUtilities>(sp =>
        {
            var engine = sp.GetRequiredService<PeakCan.Host.App.Services.Scripting.ScriptEngine>();
            return new PeakCan.Host.App.Services.Scripting.ScriptUtilities(
                sp.GetRequiredService<ILogger<PeakCan.Host.App.Services.Scripting.ScriptUtilities>>(),
                engine);
        });

        // v1.1.0: UDS diagnostic stack.
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.UdsTimer>();
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer>(sp =>
        {
            var config = new PeakCan.Host.Core.Uds.IsoTp.CanIdConfig
            {
                RequestId = 0x7E0,  // Default UDS physical request ID
                ResponseId = 0x7E8  // Default UDS physical response ID
            };
            // v1.6.5 PATCH Item 1: IsoTpLayer IS EXEMPT from rate-limit.
            // ISO 15765-2 has its own STmin pacing (consecutive-frame
            // transmit timing) that the protocol layer enforces; gating
            // it via the rate-limit decorator would break the transport
            // state machine. Inject CoreSendService (raw) directly.
            var sendService = sp.GetRequiredService<CoreSendService>();
            // v1.2.12 PATCH Item 2: async send callback. The previous
            // `.AsTask().Wait()` blocked the SDK read thread and deadlocked
            // the whole UDS diagnostic surface when SendService hung.
            // ConfigureAwait(false) avoids STA capture on the WPF UI thread;
            // exceptions are logged and swallowed inside the layer.
            var isoLogger = sp.GetRequiredService<ILogger<PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer>>();
            return new PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer(config, async frame =>
            {
                try
                {
                    await sendService.SendAsync(frame).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is PeakCan.Host.Core.Uds.IsoTp.IsoTpSendFailedException))
                {
                    // v1.2.13 PATCH Item 5: the layer's SendCanFrameAsync now
                    // throws IsoTpSendFailedException itself (after logging
                    // via LogIsoTpSendFailed). Skip the duplicate log here
                    // so each send failure is recorded exactly once (id
                    // 3001). The `when` filter is defense-in-depth for the
                    // (rare) case where SendService.SendAsync itself raises
                    // an IsoTpSendFailedException that the layer has not
                    // seen.
                    PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer.LogIsoTpSendFailed(
                        isoLogger, ex, frame.Id.Raw);
                }
            }, isoLogger);
        });
        // v1.1.0: SecurityAccess KeyProvider default. OEM overrides this at deploy time.
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.IKeyDerivationAlgorithm, PeakCan.Host.Core.Uds.PlaceholderKeyAlgorithm>();
        // v1.1.0: DID + Routine databases (load from %APPDATA%\PeakCan.Host\ on construction).
        // v1.6.10 PATCH Item 2: factory wires PathOptions so the 3-arg ctor
        // (Task 5) receives the config-driven allowlist instead of the
        // hardcoded Default.
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.Database.DidDatabase>(sp =>
            new PeakCan.Host.Core.Uds.Database.DidDatabase(
                PeakCan.Host.Core.Uds.Database.DidDatabaseDefaults.DefaultJsonPath,
                sp.GetRequiredService<ILogger<PeakCan.Host.Core.Uds.Database.DidDatabase>>(),
                sp.GetRequiredService<PathOptions>()));
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.Database.RoutineDatabase>(sp =>
            new PeakCan.Host.Core.Uds.Database.RoutineDatabase(
                PeakCan.Host.Core.Uds.Database.RoutineDatabaseDefaults.DefaultJsonPath,
                sp.GetRequiredService<ILogger<PeakCan.Host.Core.Uds.Database.RoutineDatabase>>(),
                sp.GetRequiredService<PathOptions>()));
        // v1.1.0: UdsClient now requires an IKeyDerivationAlgorithm via the 3-arg ctor.
        // v1.2.13 PATCH Item 2: also pass ILogger<UdsSession> so S3 keepalive
        // failures are observable in production (logger-aware ctor was added
        // in v1.2.12 but never wired — this closes the known-deferred item).
        // v1.3.0 MINOR Item 5: when WithUdsSecurityLockoutConfig was called,
        // thread the policy through the new lockout-config ctor overload;
        // otherwise fall through to the legacy 3-arg ctor (defaults preserved).
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.UdsClient>(sp =>
        {
            var isoTp = sp.GetRequiredService<PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer>();
            var keyAlgorithm = sp.GetRequiredService<PeakCan.Host.Core.Uds.IKeyDerivationAlgorithm>();
            var sessionLogger = sp.GetService<ILogger<PeakCan.Host.Core.Uds.UdsSession>>();
            if (_udsSecurityLockoutConfig is { } lockoutConfig)
            {
                return new PeakCan.Host.Core.Uds.UdsClient(
                    isoTp, keyAlgorithm, lockoutConfig,
                    timer: null, sessionLogger: sessionLogger);
            }
            return new PeakCan.Host.Core.Uds.UdsClient(isoTp, keyAlgorithm, sessionLogger: sessionLogger);
        });
        // v1.2.0: 4-panel orchestrator holds Session/Did/Routine/Dtc panel VMs;
        // each panel VM is registered as a singleton below and DI auto-resolves
        // the new UdsViewModel ctor (SessionPanelViewModel, DidPanelViewModel,
        // RoutinePanelViewModel, DtcPanelViewModel).
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.SessionPanelViewModel>();
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.DidPanelViewModel>();
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.RoutinePanelViewModel>();
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.DtcPanelViewModel>();
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.UdsViewModel>();

        // v2.0.0 MINOR: ODX-D DIAG-LAYER importer. In-memory databases +
        // Core parser/persistence plus App-layer service + VM glue.
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.Database.DtcDatabase>();
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.Odx.OdxParser>();
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.Odx.PdxReader>();
        builder.Services.AddSingleton<PeakCan.Host.App.Services.IOdxImportService,
            PeakCan.Host.App.Services.OdxImportService>();
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.OdxImportViewModel>();

        // ViewModels
        // v1.5.0 MINOR: AppShellViewModel ctor takes an optional IConfiguration
        // for SelectedChannel persistence. Wire via factory so the DI
        // container resolves the host's IConfiguration; this keeps the
        // existing parameterless AddSingleton call sites (test fakes) working.
        builder.Services.AddSingleton<AppShellViewModel>(sp => new AppShellViewModel(
            sp.GetRequiredService<ChannelRouter>(),
            sp.GetRequiredService<ILogger<AppShellViewModel>>(),
            sp.GetRequiredService<TraceViewModel>(),
            sp.GetRequiredService<SendService>(),
            sp.GetRequiredService<IChannelProbe>(),
            sp.GetRequiredService<IChannelFactory>(),
            sp.GetRequiredService<DbcViewModel>(),
            sp.GetRequiredService<SendViewModel>(),
            sp.GetRequiredService<SignalViewModel>(),
            sp.GetRequiredService<StatsViewModel>(),
            sp.GetRequiredService<ScriptViewModel>(),
            sp.GetRequiredService<UdsViewModel>(),
            sp.GetRequiredService<RecordViewModel>(),
            sp.GetRequiredService<ReplayViewModel>(),
            sp.GetRequiredService<PeakCan.Host.App.ViewModels.MultiFrameSendViewModel>(),
            sp.GetRequiredService<TraceViewerViewModel>(),
            sp.GetRequiredService<PeakCan.Host.App.Services.Trace.RecentSessionsService>(),
            sp.GetRequiredService<PeakCan.Host.Core.IFileDialogService>(),
            // v3.10.0 MINOR T1 (C1): IMessageBoxPrompt seam — replaces
            // the direct MessageBox.Show calls in OpenSessionAsync /
            // OpenRecentSessionAsync (WPFMessageBoxPrompt wired by DI
            // registration above; tests inject Substitute.For<...>()).
            sp.GetRequiredService<PeakCan.Host.App.Services.Trace.IMessageBoxPrompt>(),
            sp.GetService<PeakCan.Host.Core.IChannelEnumerator>(),
            sp.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton<TraceViewModel>();
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
        builder.Services.AddSingleton<SendViewModel>(sp =>
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
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SendViewModel>());
        builder.Services.AddSingleton<DbcViewModel>();
        // v0.8.0: signal chart VM must be registered before SignalViewModel
        // (SignalViewModel depends on it via constructor injection).
        builder.Services.AddSingleton<SignalChartViewModel>();
        builder.Services.AddSingleton<SignalViewModel>();
        // v1.2.12 PATCH Item 6: also register as IHostedService so the
        // host disposes the System.Threading.Timer drain on shutdown.
        // Without this, the timer's strong ref to the VM keeps the VM
        // alive across STA-WPF xunit fixtures and after shell teardown.
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SignalViewModel>());
        builder.Services.AddSingleton<StatsViewModel>();
        builder.Services.AddSingleton<ScriptViewModel>();

        // Windows: AppShell is a WPF Window whose ctor requires an STA thread
        // (xunit's MTA threadpool cannot instantiate it). Register via a
        // factory that the host resolves on demand; production callers must
        // resolve AppShell from the STA thread (App.OnStartup qualifies).
        // The factory wires the VM via DataContext so XAML bindings resolve.
        builder.Services.AddSingleton<AppShell>(sp => new AppShell
        {
            DataContext = sp.GetRequiredService<AppShellViewModel>()
        });

        // Task 13: hosted service that wires the App-layer sinks
        // (TraceService + BusStatisticsCollector) into ChannelRouter at
        // host startup. Closes the Task 12 gap where the two were
        // registered as singletons but never connected.
        builder.Services.AddHostedService<SinkWiringService>();
        return builder.Build();
    }
}
