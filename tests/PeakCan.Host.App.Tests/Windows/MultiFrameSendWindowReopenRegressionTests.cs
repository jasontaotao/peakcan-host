using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.MultiFrame;
using PeakCan.Host.App.Services.Ui;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.Windows;
using Xunit;

namespace PeakCan.Host.App.Tests.Windows;

/// <summary>
/// Regression guard for the reported crash "批量发送打开关闭后重开报
/// StackOverflowException".
/// <para>
/// Root cause (found by reproduction): <c>MultiFrameSendWindow.xaml</c> bound
/// the two "Mode" <see cref="RadioButton"/>s TwoWay to the SAME VM source
/// (<c>IsConcurrent</c>), the Sequential one through
/// <see cref="Composition.Converters.InverseBooleanConverter"/>. When the
/// radio group unchecks a sibling, WPF's
/// <c>RadioButton.UpdateRadioButtonGroup</c> calls
/// <c>SetCurrentValueInternal</c>, which round-trips the sibling's TwoWay
/// binding back into the shared source with the opposite value, which re-runs
/// both bindings, which re-triggers the group update — an infinite loop that
/// blew the stack on the reopened window. Fix: Sequential now binds its own
/// <see cref="MultiFrameSendViewModel.IsSequential"/> mirror whose setter
/// ignores the group's <c>false</c> write-back (see VM tests).
/// </para>
/// <para>
/// The crash only reproduced with a real shell (owner + Window menu churn
/// change the binding-init ordering), so this test drives a real
/// <see cref="AppShell"/> via a minimal DataContext shim instead of a bare
/// window. It requires STA + a live WPF render pass, hence
/// <see cref="WpfAppTestCollection"/>.
/// </para>
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public class MultiFrameSendWindowReopenRegressionTests
{
    /// <summary>Minimal DataContext shim so the real AppShell (Window menu
    /// binding <c>WindowHost.OpenWindows</c>) renders without the full
    /// AppShellViewModel dependency surface.</summary>
    public sealed class ShellShim
    {
        public WindowHostService WindowHost { get; set; }
        public ShellShim(WindowHostService host) => WindowHost = host;
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void Reopen_After_Close_Does_Not_StackOverflow()
    {
        var storePath = Path.Combine(Path.GetTempPath(), $"wf-reopen-{Guid.NewGuid():N}.json");
        var store = new WindowStateStore(NullLogger<WindowStateStore>.Instance, storePath);
        store.Set(WindowKey.MultiFrame, new WindowStateDto(330.6, 117.3, 900, 600, "Normal"));

        var vm = new MultiFrameSendViewModel(
            new SequenceSendService(new SendService(NullLogger<SendService>.Instance)));
        var host = new WindowHostService(store);

        Exception? threadErr = null;
        var t = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                // P2-3/P2-5: AppShell.xaml resolves {StaticResource} tokens
                // (Accent/CanvasBg/...) at parse time; merge the production
                // Colors.xaml so a bare test Application can render it.
                LeakedApplicationReset.MergeTokenResources(app);
                var shell = new AppShell { DataContext = new ShellShim(host), WindowStateStore = store };
                app.MainWindow = shell;
                shell.Show();
                PumpDispatcher();

                var win1 = host.Show(WindowKey.MultiFrame, () => new MultiFrameSendWindow(vm))!;
                win1.Show();
                PumpDispatcher();
                shell.Activate();
                PumpDispatcher();

                win1.Close();
                PumpDispatcher();

                var win2 = host.Show(WindowKey.MultiFrame, () => new MultiFrameSendWindow(vm))!;
                win2.Show();
                PumpDispatcher();
                shell.Activate();
                PumpDispatcher();

                // Pre-fix this sequence StackOverflowed on win2.Show() — the
                // Mode radio group's TwoWay bindings oscillated IsConcurrent
                // through the InverseBool converter. Reaching this line proves
                // the reopened window rendered without the loop.
                win2.Close();
                PumpDispatcher();
                shell.Close();
                app.Shutdown();
            }
            catch (Exception ex)
            {
                threadErr = ex;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        // LeakedApplicationReset must run around the STA body: this test creates
        // a real WPF Application, whose static singleton survives Shutdown() and
        // thread exit — a leak that has caused real parallel-suite flakes (see
        // Collections/LeakedApplicationReset.cs and ConverterSmokeTests).
        LeakedApplicationReset.CleanupLeakedApplication();
        t.Start();
        t.Join(TimeSpan.FromSeconds(60));
        LeakedApplicationReset.CleanupLeakedApplication();
        if (t.IsAlive)
        {
            throw new TimeoutException("STA thread did not complete — dispatcher deadlock or StackOverflow");
        }
        if (threadErr is not null) throw threadErr;
    }
}
