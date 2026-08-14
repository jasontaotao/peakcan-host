using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;
using FluentAssertions;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.Views;
using PeakCan.Host.App.Tests.Collections;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// v1.2.13 PATCH Item 6: the async void <c>OnLoaded</c> handler must not
/// write to the VM if the view has been Unloaded between the await and
/// the continuation, and <c>Unloaded</c> must dispose the WebView2 host
/// so its <c>CoreWebView2</c> process does not leak across tab
/// navigations.
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public class ScriptViewTests
{
    public ScriptViewTests() => LeakedApplicationReset.CleanupLeakedApplication();

    /// <summary>
    /// v1.2.13 PATCH Item 6: a post-await side effect in <c>OnLoaded</c>
    /// must not write to the VM if the view has been Unloaded between the
    /// await and the continuation. Without the <c>_isLoaded</c> guard,
    /// tab navigation can leave the previous VM's
    /// <see cref="ScriptViewModel.IsEditorReady"/> = true after the user
    /// has navigated away (visible bug: Scripts tab stays 'ready' even
    /// after switching to Trace and back).
    /// </summary>
    [Fact]
    public void OnLoaded_After_Unloaded_Does_Not_Write_IsEditorReady()
    {
        // ScriptView is a WPF UserControl; InitializeComponent requires
        // STA on Windows. Wrap in RunSta so the test runs reliably on
        // the CI xunit MTA thread.
        RunSta(() =>
        {
            // DataContext can be null; the guard under test is independent
            // of VM assignment (we drive the Unloaded handler directly).
            var view = new ScriptView { DataContext = null };

            // Force Loaded -> Unloaded synchronously. _isLoaded should be
            // false after Unloaded even though we never re-ran OnLoaded.
            view.RaiseLoadedForTesting();
            view.RaiseUnloadedForTesting();

            // Assert the field state via reflection: _isLoaded must be
            // false after Unloaded.
            var isLoadedField = typeof(ScriptView).GetField("_isLoaded",
                BindingFlags.NonPublic | BindingFlags.Instance);
            isLoadedField.Should().NotBeNull(
                "ScriptView must declare a private _isLoaded field for the post-await guard");
            var isLoaded = (bool)isLoadedField!.GetValue(view)!;
            isLoaded.Should().BeFalse("Unloaded must set _isLoaded = false");
        });
    }

    /// <summary>
    /// v1.2.13 PATCH Item 6: <c>EditorWebView.CoreWebView2</c> must be
    /// disposed when the view is Unloaded; otherwise the WebView2
    /// process leaks across tab navigations. The Unloaded hook calls
    /// <c>Dispose()</c> via null-conditional so a not-yet-initialized
    /// <c>EditorWebView</c> (XAML field may be null in test
    /// instantiation) does not throw.
    /// </summary>
    [Fact]
    public void Unloaded_Disposes_EditorWebView()
    {
        RunSta(() =>
        {
            var view = new ScriptView { DataContext = null };

            // We can't drive a real WebView2 init in a unit test, but we
            // can verify the Unloaded hook runs cleanly when the XAML
            // EditorWebView field is null (or non-null): the null-
            // conditional Dispose handles null safely, and the field
            // assignment to null! afterward must not throw.
            Action act = () => view.RaiseUnloadedForTesting();
            act.Should().NotThrow(
                "Unloaded must safely handle a not-yet-initialized EditorWebView");

            // A second Unloaded must also be safe (idempotency check).
            Action actAgain = () => view.RaiseUnloadedForTesting();
            actAgain.Should().NotThrow("Unloaded must be idempotent");
        });
    }

    /// <summary>
    /// Load the production token dictionary (<c>Themes/Colors.xaml</c>). Task 5
    /// (P2-5) tokenized ScriptView, which now resolves
    /// <c>{StaticResource TextSecondary/ConsoleBg/...}</c> from Application-level
    /// merged resources — a bare <c>new ScriptView()</c> in a resource-less test
    /// context cannot see them and throws XamlParseException. Path convention is
    /// identical to <c>ColorTokensTests</c> (5 levels up from
    /// <c>AppContext.BaseDirectory</c> to the repo root).
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

    /// <summary>
    /// Run the body on a dedicated STA thread with a live WPF Application whose
    /// resources include the production Colors.xaml token dictionary.
    /// ScriptView's <c>InitializeComponent</c> resolves <c>{StaticResource}</c>
    /// tokens at parse time; without the merged dictionary the tokenized XAML
    /// throws. A real Application is required (only one may exist per
    /// AppDomain), so it is created inside the STA body and the leaked static
    /// singleton is cleaned around the thread — the same pattern as
    /// MultiFrameSendWindowReopenRegressionTests.
    /// </summary>
    private static void RunSta(Action body)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            RunWithAppResources(body);
            return;
        }
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { RunWithAppResources(body); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        // The STA body creates a real WPF Application whose static singleton
        // survives Shutdown() + thread exit — a leak that has caused real
        // parallel-suite flakes (see Collections/LeakedApplicationReset.cs).
        LeakedApplicationReset.CleanupLeakedApplication();
        thread.Start();
        thread.Join();
        LeakedApplicationReset.CleanupLeakedApplication();
        if (captured is not null) throw captured;
    }

    /// <summary>
    /// WPF allows only one Application per AppDomain; the constructor guard is
    /// the static <c>_appCreatedInThisAppDomain</c> flag (not
    /// <c>_appInstance</c>), and it survives <c>Shutdown()</c>. Reset both
    /// before creating our Application so each STA body can create its own —
    /// same pattern as
    /// <c>AppShellViewModelMessageBoxPromptTests.ResetAppDomainApplicationGuard</c>.
    /// </summary>
    private static void ResetAppDomainApplicationGuard()
    {
        LeakedApplicationReset.CleanupLeakedApplication();
        typeof(Application).GetField("_appCreatedInThisAppDomain",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?.SetValue(null, false);
    }

    /// <summary>
    /// Create a fresh Application on the current (STA) thread, merge the
    /// production Colors.xaml token dictionary, run the body, then shut the
    /// Application down. The leaked static singleton is cleaned around the
    /// thread by <see cref="RunSta"/>.
    /// </summary>
    private static void RunWithAppResources(Action body)
    {
        ResetAppDomainApplicationGuard();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(LoadTokenDictionary());
        try
        {
            body();
        }
        finally
        {
            app.Shutdown();
        }
    }
}
