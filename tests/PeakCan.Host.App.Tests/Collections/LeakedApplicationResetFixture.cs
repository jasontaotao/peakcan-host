using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;

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
        if (app is not null)
        {
            try { app.Shutdown(); } catch { /* dispatcher may already be shutting down */ }
        }
        // _appInstance is the static backing field for
        // Application.Current (the property has no public setter).
        typeof(Application).GetField("_appInstance",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?.SetValue(null, null);
        // WPF's Application ctor guard is the _appCreatedInThisAppDomain
        // flag, NOT _appInstance — it survives Shutdown() and would make a
        // later `new Application()` in the same AppDomain throw "cannot
        // create more than one System.Windows.Application instance". Reset it
        // too, or the first app-creating test in a run silently breaks every
        // later one (real parallel-suite flake, see
        // MultiFrameSendWindowReopenRegressionTests + ScriptViewTests).
        typeof(Application).GetField("_appCreatedInThisAppDomain",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?.SetValue(null, false);
    }

    /// <summary>
    /// Merge the production Colors.xaml token dictionary into
    /// <paramref name="app"/> (idempotent — skipped when a token key is
    /// already present). Tests that construct tokenized views/windows
    /// (<c>{StaticResource TextSecondary/RowAlternate/Accent/...}</c>)
    /// without the real App.xaml must call this before constructing.
    /// </summary>
    public static void MergeTokenResources(Application app)
    {
        var merged = app.Resources.MergedDictionaries;
        if (!merged.Any(rd => rd.Contains("TextSecondary")))
        {
            merged.Add(LoadTokenDictionary());
        }
    }

    /// <summary>
    /// Run <paramref name="body"/> on the CURRENT thread with a fresh WPF
    /// Application whose resources include the production Colors.xaml token
    /// dictionary, then shut the Application down. Must run on an STA thread
    /// (the Application binds to that thread's dispatcher). Cleans any leaked
    /// Application first so <c>new Application()</c> cannot trip the
    /// AppDomain creation guard.
    /// </summary>
    public static void RunWithTokenResources(Action body)
    {
        CleanupLeakedApplication();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        MergeTokenResources(app);
        try
        {
            body();
        }
        finally
        {
            app.Shutdown();
        }
    }

    /// <summary>
    /// Value-returning variant of <see cref="RunWithTokenResources(Action)"/>.
    /// </summary>
    public static T RunWithTokenResources<T>(Func<T> body)
    {
        CleanupLeakedApplication();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        MergeTokenResources(app);
        try
        {
            return body();
        }
        finally
        {
            app.Shutdown();
        }
    }

    /// <summary>
    /// Load the production token dictionary (<c>Themes/Colors.xaml</c>) via
    /// <c>XamlReader</c> — no Application dependency. Path convention matches
    /// <c>ColorTokensTests</c> (5 levels up from <c>AppContext.BaseDirectory</c>).
    /// </summary>
    private static ResourceDictionary LoadTokenDictionary()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PeakCan.Host.App", "Themes", "Colors.xaml");
        var xaml = File.ReadAllText(Path.GetFullPath(path));
        return (ResourceDictionary)XamlReader.Parse(xaml);
    }
}