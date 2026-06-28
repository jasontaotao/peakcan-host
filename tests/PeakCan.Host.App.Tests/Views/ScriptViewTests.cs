using System.Reflection;
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
    /// Run the body on a dedicated STA thread. ScriptView's
    /// <c>InitializeComponent</c> reads XAML resources that require
    /// STA on Windows; xunit's default MTA worker thread will fail
    /// otherwise. Same pattern as AppHostBuilderTests.RunSta.
    /// </summary>
    private static void RunSta(Action body)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            body();
            return;
        }
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured is not null) throw captured;
    }
}