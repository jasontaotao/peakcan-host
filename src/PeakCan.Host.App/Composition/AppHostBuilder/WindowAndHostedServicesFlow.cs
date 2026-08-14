using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PeakCan.Host.App.Services.Ui;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Composition;

public partial class AppHostBuilder
{
    // Flow G: Window + hosted services (Task 13 + earlier).
    // AppShell STA factory + SinkWiringService hosted service.
    // Extracted from Build() verbatim per W11 D5.

    /// <summary>
    /// Register Window + hosted services (AppShell + SinkWiringService).
    /// Extracted from Build() body as a private helper (W11 R3 mitigation).
    /// </summary>
    private void RegisterWindowAndHostedServices(IServiceCollection services)
    {
        // Windows: AppShell is a WPF Window whose ctor requires an STA thread
        // (xunit's MTA threadpool cannot instantiate it). Register via a
        // factory that the host resolves on demand; production callers must
        // resolve AppShell from the STA thread (App.OnStartup qualifies).
        // The factory wires the VM via DataContext so XAML bindings resolve.
        //
        // P2-6 review r1: this factory is the SINGLE AppShell registration —
        // App.OnStartup resolves AppShell through it (not `new AppShell`),
        // so both persistence stores are injected in production.
        services.AddSingleton<AppShell>(sp =>
        {
            var windowStateStore = sp.GetRequiredService<WindowStateStore>();
            var layoutStore = sp.GetRequiredService<LayoutStateStore>();
            // P0-5/P2-6: fire-and-forget load of persisted geometry + layout.
            // Both LoadAsync methods have a synchronous body (they read the
            // file and return a completed Task), so _state is populated
            // before the shell's SourceInitialized → ApplyStoredState /
            // RestoreLayout runs. Fires once per host because AppShell and
            // both stores are singletons.
            _ = windowStateStore.LoadAsync(CancellationToken.None);
            _ = layoutStore.LoadAsync(CancellationToken.None);
            return new AppShell
            {
                DataContext = sp.GetRequiredService<AppShellViewModel>(),
                // P0-5: window-geometry persistence for the main shell.
                WindowStateStore = windowStateStore,
                // P2-6: layout persistence (right-panel width + tab selection).
                LayoutStateStore = layoutStore,
            };
        });

        // Task 13: hosted service that wires the App-layer sinks
        // (TraceService + BusStatisticsCollector) into ChannelRouter at
        // host startup. Closes the Task 12 gap where the two were
        // registered as singletons but never connected.
        //
        // P0 (flashing feature 2026-07-21): the IsoTpSinkAdapter is the
        // receive-wiring for the UDS stack. IsoTpLayer is already a singleton
        // (registered earlier in Build), so the adapter wraps that same
        // instance — the diagnostic UdsClient (and, later, the flashing
        // pipeline's UdsClient) all share one IsoTpLayer per CAN-ID pair.
        // DI resolves IsoTpLayer into the adapter's ctor automatically; the
        // order here does not matter for resolution but mirrors the
        // attach order in SinkWiringService.StartAsync for readability.
        services.AddSingleton<IsoTpSinkAdapter>();
        services.AddHostedService<SinkWiringService>();
    }
}