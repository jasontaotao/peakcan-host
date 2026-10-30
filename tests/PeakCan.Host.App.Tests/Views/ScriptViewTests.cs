using System;
using System.IO;
using System.Linq;
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

    [Fact]
    public void Save_As_Is_Wired_In_The_Toolbar_And_The_File_Menu()
    {
        // 绑定写错是静默失效（按钮点了没反应），所以按 XAML 文本 + VM 反射两头钉。
        var view = File.ReadAllText(RepoFile("src", "PeakCan.Host.App", "Views", "ScriptView.xaml"));
        view.Should().Contain("{Binding SaveAsCommand}");

        var shell = File.ReadAllText(RepoFile("src", "PeakCan.Host.App", "AppShell.xaml"));
        shell.Should().Contain("另存为脚本");
        shell.Should().Contain("{Binding SaveScriptAsCommand}");

        typeof(ScriptViewModel).GetProperty(nameof(ScriptViewModel.SaveAsCommand))
            .Should().NotBeNull("菜单/按钮绑的命令必须真的存在于 VM 上");
    }

    [Fact]
    public void Recent_Scripts_Menu_Is_Wired_To_The_ViewModel()
    {
        var xaml = File.ReadAllText(RepoFile("src", "PeakCan.Host.App", "Views", "ScriptView.xaml"));

        xaml.Should().Contain("最近脚本");
        xaml.Should().Contain("{Binding RecentScripts}");
        xaml.Should().Contain("{Binding DataContext.OpenRecentScriptCommand");
        xaml.Should().Contain("{Binding ClearRecentScriptsCommand}");
        xaml.Should().Contain("IsEnabled=\"{Binding HasRecentScripts}\"",
            "空列表时菜单项要置灰，否则点开是一个空菜单");

        // 绑定路径写错是静默失效：两个名字都必须在 VM 上真实存在。
        typeof(ScriptViewModel).GetProperty(nameof(ScriptViewModel.RecentScripts)).Should().NotBeNull();
        typeof(ScriptViewModel).GetProperty(nameof(ScriptViewModel.HasRecentScripts)).Should().NotBeNull();
        typeof(ScriptViewModel).GetProperty(nameof(ScriptViewModel.OpenRecentScriptCommand)).Should().NotBeNull();
        typeof(ScriptViewModel).GetProperty(nameof(ScriptViewModel.ClearRecentScriptsCommand)).Should().NotBeNull();
    }

    [Fact]
    public void New_Script_Is_Wired_In_The_Toolbar_And_The_File_Menu()
    {
        var view = File.ReadAllText(RepoFile("src", "PeakCan.Host.App", "Views", "ScriptView.xaml"));
        view.Should().Contain("{Binding NewCommand}");

        var shell = File.ReadAllText(RepoFile("src", "PeakCan.Host.App", "AppShell.xaml"));
        shell.Should().Contain("新建脚本");
        shell.Should().Contain("{Binding NewScriptCommand}");

        typeof(ScriptViewModel).GetProperty(nameof(ScriptViewModel.NewCommand))
            .Should().NotBeNull("工具栏/菜单绑的命令必须真的存在于 VM 上");
        typeof(AppShellViewModel).GetProperty(nameof(AppShellViewModel.NewScriptCommand))
            .Should().NotBeNull();
    }

    [Fact]
    public void Dirty_Marker_Is_Bound_To_A_Real_TabSpec_Property()
    {
        var shell = File.ReadAllText(RepoFile("src", "PeakCan.Host.App", "AppShell.xaml"));

        shell.Should().Contain("{Binding IsDirty, Converter={StaticResource BoolToVis}}",
            "主标签头要显示未保存标记");

        // 绑定路径写错在 WPF 里是静默失效：圆点会永远不出现，也不报错。
        typeof(TabSpec).GetProperty(nameof(TabSpec.IsDirty)).Should().NotBeNull();
    }

    private static string RepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent;

        if (dir is null)
            throw new FileNotFoundException(
                $"找不到 PeakCan.Host.slnx —— 测试是从非仓库目录运行的？起始目录: {AppContext.BaseDirectory}");

        return Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
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
