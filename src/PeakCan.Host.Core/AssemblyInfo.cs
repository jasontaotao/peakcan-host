using System.Runtime.CompilerServices;

// v1.2.12 PATCH Item 2: expose the internal LogIsoTpSendFailed source-gen
// helper on IsoTpLayer so the App-layer factory (AppHostBuilder) can call
// it directly instead of duplicating the log call with a different event
// id. Single source of truth for the "IsoTpSendFailed" event (id 3001).
//
// Note: the App project is renamed from `PeakCan.Host.App` to `PeakCan.Host`
// via `<AssemblyName>PeakCan.Host</AssemblyName>` (see PeakCan.Host.App.csproj
// Task 20). InternalsVisibleTo must use the actual assembly name, not the
// project/namespace name — `PeakCan.Host.App` would silently no-op.
[assembly: InternalsVisibleTo("PeakCan.Host")]
// v1.2.13 PATCH Item 1 review fix: expose the internal
// _watchdogDisposalDeferredCount counter so the IsoTpLayerTests
// watchdog-churn regression test can assert the deferred-Dispose
// path was actually taken.
[assembly: InternalsVisibleTo("PeakCan.Host.Core.Tests")]
// v1.2.13 PATCH Item 2: expose UdsSession.SessionLogger so
// AppHostBuilderTests can assert the production DI graph threads
// ILogger<UdsSession> all the way through to UdsSession.
[assembly: InternalsVisibleTo("PeakCan.Host.App.Tests")]
// Sprint 2: expose internal executor classes to Infrastructure + Cli + their tests
// Note: use assembly name (not namespace). CLI assembly name = "peakcan-hil".
[assembly: InternalsVisibleTo("PeakCan.Host.Infrastructure")]
[assembly: InternalsVisibleTo("peakcan-hil")]
[assembly: InternalsVisibleTo("PeakCan.Host.Infrastructure.Tests")]
[assembly: InternalsVisibleTo("PeakCan.Host.Cli.Tests")]