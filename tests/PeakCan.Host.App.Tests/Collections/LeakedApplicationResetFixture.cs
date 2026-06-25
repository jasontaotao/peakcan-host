using System.Reflection;
using System.Windows;

namespace PeakCan.Host.App.Tests.Collections;

/// <summary>
/// Shared cleanup helper for the leaked-<see cref="Application.Current"/>
/// race that caused the v1.2.0 spec §9.1 flake.
/// <para>
/// <b>Why this exists (v1.2.1 PATCH Task 5):</b> the
/// <see cref="ViewModels.TraceViewModelTests.AppendBatch_On_StaThread_With_Application_Adds_All_Frames"/>
/// test creates a WPF <see cref="Application"/> on a dedicated STA thread
/// to exercise the production dispatcher path. When the STA thread exits
/// via <c>Thread.Join</c> the static <see cref="Application.Current"/>
/// singleton survives the thread — it points at a dispatcher whose owning
/// thread is no longer pumping. xUnit runs test classes in parallel, so
/// a sibling MTA test (e.g.
/// <see cref="ViewModels.SignalViewModelTests.ApplyFrame_Multiple_Signals_Adds_All_As_Entries"/>)
/// may observe the leaked singleton and route its inline path through
/// <c>Dispatcher.InvokeAsync</c> on the dead dispatcher — the queued
/// action never runs and <c>vm.Latest</c> stays empty.
/// </para>
/// <para>
/// <b>Usage:</b> every test class that constructs a WPF-dependent
/// view-model (Signal / Stats / Trace VMs use <see cref="DispatcherExtensions.RunOnUiPost"/>)
/// should call <see cref="CleanupLeakedApplication"/> in its constructor
/// (or in <c>InitializeAsync</c>) so a leak from any other collection's
/// parallel test is nulled out before the test runs. The helper is
/// idempotent.
/// </para>
/// </summary>
public static class LeakedApplicationReset
{
    /// <summary>
    /// Shut down any leaked <see cref="Application.Current"/> and clear
    /// the backing <c>_appInstance</c> field via reflection. Safe to
    /// call when <see cref="Application.Current"/> is null.
    /// </summary>
    public static void CleanupLeakedApplication()
    {
        var app = Application.Current;
        if (app is null) return;
        try { app.Shutdown(); } catch { /* dispatcher may already be shutting down */ }
        // _appInstance is the static backing field for
        // Application.Current (the property has no public setter).
        typeof(Application).GetField("_appInstance",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?.SetValue(null, null);
    }
}