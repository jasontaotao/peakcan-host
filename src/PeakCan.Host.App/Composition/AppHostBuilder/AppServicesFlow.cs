using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Nodes;
using PeakCan.Host.App.Services.Ui;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.Devices;
using PeakCan.HIL.Core.Path;
using PeakCan.HIL.Core.Replay;
using PeakCan.Host.Infrastructure.Peak;
using PeakCan.Host.Infrastructure.Zlg;
using Polly;

namespace PeakCan.Host.App.Composition;

public partial class AppHostBuilder
{
    // Flow C: App services (v1.2.11 PATCH Item 5/6 + v1.2.12 PATCH Items 5/11 + v1.6.5 PATCH Item 1 + v1.6.6 PATCH Item 1 + v1.6.10 PATCH Item 2 + v1.7.0 MINOR Item 1 + v0.5.0 + v1.4.0 MINOR + v1.5.1 PATCH Item 2 + v2.0.6 PATCH Bug-2 + earlier).
    // Extracted from Build() verbatim per W11 D5.

    /// <summary>
    /// Register the App-layer services (Trace, Send, Dbc, Path, Script, Statistics, Record, Replay, CyclicSend, CyclicDbcSend, DbcSend).
    /// Extracted from Build() body as a private helper (W11 R3 mitigation).
    /// </summary>
    private void RegisterAppServices(IServiceCollection services)
    {
        // P0-2/P0-3: secondary-window lifecycle host — DI singleton so
        // AppShellViewModel + SendViewModel share one cache (Multi-frame
        // opens a single window from either entry).
        services.AddSingleton<WindowStateStore>();
        services.AddSingleton<WindowHostService>(sp =>
            new WindowHostService(sp.GetRequiredService<WindowStateStore>()));
        // P1-1: device providers — one per supported CAN-box family (PEAK + ZLG).
        services.AddSingleton<ICanDeviceProvider, PeakCanDeviceProvider>();
        services.AddSingleton<ICanDeviceProvider, ZlgCanDeviceProvider>();
        // v1.2.12 PATCH Item 11: TraceService now takes an ILogger<TraceService>
        // so its OnError path is observable in Release builds (Debug.WriteLine
        // was previously stripped). Production DI resolves the logger from
        // the host's LoggingServiceCollection; NullLogger fallback exists
        // for tests that do not assert on log output.
        services.AddSingleton<TraceService>(sp =>
            new TraceService(
                sp.GetRequiredService<TraceViewModel>(),
                sp.GetRequiredService<ILogger<TraceService>>()));
        // TraceService is a BackgroundService; its 50ms drain loop lives in
        // ExecuteAsync and only fires when the host starts it. Without this
        // AddHostedService line, frames pile up in the bounded channel and
        // TotalFrameCount stays 0 even though fan-out delivers them. Same
        // bug pattern as the IHost.StartAsync miss in App.xaml.cs.
        services.AddHostedService(sp => sp.GetRequiredService<TraceService>());
        // v1.6.5 PATCH Item 1: token-bucket send-rate-limit decorator.
        // Register CoreSendService (raw, exempt from rate-limit) and a
        // SendService factory that wraps it with RateLimitedSendService.
        // UI callers (CanApi, SendViewModel, DbcSendViewModel,
        // CyclicSendService, CyclicDbcSendService) resolve SendService
        // via C# polymorphism and receive the decorator; Replay +
        // IsoTp resolve CoreSendService directly (see below).
        services.AddSingleton<CoreSendService>();
        services.AddSingleton<SendService>(sp => new RateLimitedSendService(
            inner: sp.GetRequiredService<CoreSendService>(),
            maxFramesPerSecond: sp.GetRequiredService<IConfiguration>()
                .GetValue<int>("Send:MaxFramesPerSecond"),
            logger: sp.GetRequiredService<ILogger<RateLimitedSendService>>()));
        // v1.6.6 PATCH Item 1: DBC size + message-count caps, opt-in via
        // appsettings.json:Dbc section. DbcOptions.Unlimited when both
        // caps are 0 or absent — back-compat baseline preserved.
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>().GetSection("Dbc");
            return new DbcOptions(
                MaxFileSizeBytes: config.GetValue<long>("MaxFileSizeBytes"),
                MaxMessageCount: config.GetValue<int>("MaxMessageCount"));
        });
        services.AddSingleton<DbcService>(sp =>
            new DbcService(
                sp.GetRequiredService<ILogger<DbcService>>(),
                sp.GetRequiredService<DbcOptions>()));
        // v1.6.10 PATCH Item 2: opt-in extension of v1.6.4 PATCH hardcoded
        // allowlist — config-driven via Path:AllowedRoots:[] in appsettings.json.
        services.AddSingleton(sp =>
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
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>().GetSection("Script");
            return new PeakCan.Host.App.Services.Scripting.ScriptEngineOptions(
                MaxHeapSizeMB: config.GetValue<int?>("MaxHeapSizeMB") ?? 64,
                MaxNewSpaceSizeMB: config.GetValue<int?>("MaxNewSpaceSizeMB") ?? 16,
                MaxOldSpaceSizeMB: config.GetValue<int?>("MaxOldSpaceSizeMB") ?? 48);
        });
        services.AddSingleton<StatisticsService>();
        // StatisticsService is a BackgroundService; its 1Hz snapshot loop
        // needs IHostedService registration too. Without this, Stats view
        // never refreshes — matches TraceService bug pattern.
        services.AddHostedService(sp => sp.GetRequiredService<StatisticsService>());
        // v0.5.0: frame recording (ASC/CSV) and cyclic send.
        services.AddSingleton<RecordService>();
        // v1.2.12 PATCH Item 5: RecordService is a BackgroundService; its
        // writer thread + 1Hz flush need IHostedService registration.
        // Without this, ExecuteAsync never starts and frames never reach disk.
        services.AddHostedService(sp => sp.GetRequiredService<RecordService>());
        services.AddSingleton<CyclicSendService>();
        // v1.2.11 PATCH Item 3: register cyclic service as its interface
        // so SendViewModel can be tested with a fake via ICyclicSendService.
        services.AddSingleton<ICyclicSendService>(sp =>
            sp.GetRequiredService<CyclicSendService>());
        // v1.2.11 PATCH Item 5: named-frame library persistence.
        services.AddSingleton<SendFrameLibrary>();

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
        services.AddSingleton<ReplayFrameSinkAdapter>(sp =>
            new ReplayFrameSinkAdapter(sp.GetRequiredService<CoreSendService>()));
        services.AddSingleton<IReplayFrameSink>(sp =>
            sp.GetRequiredService<ReplayFrameSinkAdapter>());
        // D3-R1: clock abstraction for replay — WallClockReplayClock uses
        // real DateTime.UtcNow + Timer + Task.Delay. Tests inject a fake.
        services.AddSingleton<IReplayClock, WallClockReplayClock>();
        services.AddSingleton<IReplayService, ReplayService>();
        // v1.4.0 MINOR Send DBC: stateless DbcEncodeService singleton
        // shared by DbcSendViewModel.
        services.AddSingleton<DbcEncodeService>();

        // J1939 节点仿真（spec §10，Task 18）。NodeHostService 为 app 生命周期 singleton
        // （Dispose 后不可重启——Task 16 绑定注 1），容器关闭时随 host Dispose（停全部节点）。
        // ChannelRouter 已由 CoreInfrastructureFlow 注册 singleton（brief 要求先 grep——已存在，
        // 不重复注册）；J1939NodeContext 闭包按 Task 17 落地 ctor 形状补 dbcService/dbcEncoder
        // （计划原稿写于其前，缺这两参）。单帧发送闭包直接转发 CoreSendService.SendAsync
        // （返回 ValueTask<Result<Unit>>，与 J1939TpLayer 注册同款 try/catch → Fail 转译）。
        services.AddSingleton<NodeConfigLibrary>();
        services.AddSingleton<NodeHostService>(sp =>
        {
            var layer = sp.GetRequiredService<PeakCan.HIL.Core.J1939.J1939TpLayer>();
            var sendService = sp.GetRequiredService<CoreSendService>();
            var dbc = sp.GetRequiredService<DbcService>();
            var encoder = sp.GetRequiredService<DbcEncodeService>();
            var router = sp.GetRequiredService<PeakCan.Host.Infrastructure.Channel.ChannelRouter>();
            var logger = sp.GetRequiredService<ILogger<NodeHostService>>();
            return new NodeHostService(
                (config, runtime) => new PeakCan.Host.App.Services.Nodes.J1939.J1939NodeContext(
                    config, runtime, layer,
                    (frame, ct) =>
                    {
                        try { return sendService.SendAsync(frame, ct); }
                        catch (Exception ex) { return ValueTask.FromResult(PeakCan.HIL.Core.Result<Unit>.Fail(PeakCan.HIL.Core.ErrorCode.InvalidState, ex.Message)); }
                    },
                    dbc,
                    encoder,
                    router,
                    sp.GetRequiredService<ILogger<PeakCan.Host.App.Services.Nodes.J1939.J1939NodeContext>>()),
                router: router,
                logger: logger);
        });
        // v1.5.1 PATCH Item 2 (Periodic DBC send): independent service
        // per Decision 7 — does NOT share code with CyclicSendService.
        // Register concrete type first so the IReplayService-style
        // "interface and concrete resolve to same instance" pattern is
        // preserved.
        services.AddSingleton<CyclicDbcSendService>();
        services.AddSingleton<ICyclicDbcSendService>(sp =>
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
        services.AddSingleton<DbcSendViewModel>(sp =>
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

        // v3.53.1 PATCH P1a: API Key secure storage via Windows Credential Manager
        // (DPAPI-encrypted; NEVER plaintext appsettings.json per v3.52.0 hard-boundary).
        // Sprint 17 §3.3: wrap WCM as the primary store in a ChainedCredentialStore so
        // the WPF path also sees file/env credentials (WCM → env var → ~/.hil/credentials).
        services.AddSingleton<PeakCan.HIL.Core.Analysis.ICredentialStore>(sp =>
        {
            var winStore = new PeakCan.Host.App.Services.CredentialStore.WindowsCredentialManagerStore(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<
                    PeakCan.Host.App.Services.CredentialStore.WindowsCredentialManagerStore>>());
            return new PeakCan.Host.Infrastructure.HIL.Analysis.ChainedCredentialStore(
                winStore, new PeakCan.Host.Infrastructure.HIL.Analysis.SimpleCredentialStore());
        });
        // "LlmClient" HttpClient — shared by Chat provider + HIL analysis.
        services.AddHttpClient("LlmClient", (sp, client) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PeakCan.HIL.Core.Analysis.LlmOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds * 5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("peakcan-host/3.61.0");
        })
        .AddTransientHttpErrorPolicy(builder => builder
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1))));
        // Sprint 19 Inc 8: HIL test failure analysis service (headless/CLI sibling).
        services.AddHttpClient<PeakCan.HIL.Core.HIL.Analysis.IHilAnalysisService,
            PeakCan.Host.Infrastructure.HIL.Analysis.HilAnalysisService>((sp, client) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PeakCan.HIL.Core.Analysis.LlmOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds * 5);
        })
        .AddPolicyHandler(Polly.Extensions.Http.HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => (int)r.StatusCode == 429)
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1))));
        // AI Chat: OpenAiCompatibleChatProvider, supports multi-vendor.
        services.AddSingleton<PeakCan.HIL.Core.Analysis.Chat.IChatProvider>(sp =>
        {
            var factory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var http = factory.CreateClient("LlmClient");
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PeakCan.HIL.Core.Analysis.LlmOptions>>().Value;
            var creds = sp.GetRequiredService<PeakCan.HIL.Core.Analysis.ICredentialStore>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PeakCan.HIL.Core.Analysis.Chat.OpenAiCompatibleChatProvider>>();
            return new PeakCan.HIL.Core.Analysis.Chat.OpenAiCompatibleChatProvider(
                http, opts, creds, "PeakCan/deepseek/default", logger);
        });
    }
}