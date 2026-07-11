using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Composition;

public partial class AppHostBuilder
{
    // Flow D: ViewModels batch 1 (v2.1.0 MINOR + v2.1.1 PATCH + v2.1.2 PATCH + v1.2.11 PATCH Item 6 + v1.2.12 PATCH Item 6 + v2.1.4 PATCH + v3.1.0 MINOR + earlier).
    // Extracted from Build() verbatim per W11 D5.

    /// <summary>
    /// Register ViewModels batch 1 (SequenceSend, MultiFrameSend, Record, Replay).
    /// Extracted from Build() body as a private helper (W11 R3 mitigation).
    /// </summary>
    private void RegisterViewModelsBatch1(IServiceCollection services)
    {
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
        services.AddSingleton<PeakCan.Host.App.Services.MultiFrame.SequenceSendService>(sp =>
            new PeakCan.Host.App.Services.MultiFrame.SequenceSendService(
                sp.GetRequiredService<PeakCan.Host.App.Services.SendService>(),
                sp.GetRequiredService<PeakCan.Host.Core.Dbc.DbcEncodeService>(),
                sp.GetRequiredService<PeakCan.Host.App.Services.DbcService>()));
        // v2.1.2 PATCH: SequenceLibrary persists named sequences to
        // %APPDATA%\PeakCan.Host\sequences.json. Wired into the
        // multi-frame VM factory so SaveCurrent / LoadSaved / DeleteSaved
        // commands reach the library.
        services.AddSingleton<PeakCan.Host.App.Services.Sequence.SequenceLibrary>();
        services.AddSingleton<PeakCan.Host.App.ViewModels.MultiFrameSendViewModel>(sp =>
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
        services.AddSingleton<RecordViewModel>();
        services.AddHostedService(sp => sp.GetRequiredService<RecordViewModel>());
        // v2.1.4 PATCH: Replay tab VM. Closes the v1.4.0 MINOR orphan —
        // ReplayView + IReplayService were wired but ReplayViewModel
        // itself was never registered, so AppShell could not navigate to
        // the tab. Standard AddSingleton matches the RecordViewModel
        // precedent (no IHostedService — ReplayVM has no Dispose-time
        // background timer that needs host shutdown).
        services.AddSingleton<ReplayViewModel>();
    }
}