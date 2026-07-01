using FluentAssertions;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Unit-level tests for <see cref="DispatcherExtensions.RunOnUi"/> that
/// run in the standard xunit MTA pool. The STA-only path (real WPF
/// <c>Application</c> singleton) is exercised by the production smoke
/// test (load a real DBC in the published exe) because WPF allows
/// exactly one <c>Application</c> per <c>AppDomain</c> and STA tests
/// reliably deadlock when previous tests have left the singleton alive.
/// <para>
/// These tests pin the two paths that xunit can reach without an
/// <c>Application</c>:
/// </para>
/// <list type="bullet">
///   <item><b>No Application</b> (xunit baseline) — both <c>RunOnUi</c>
///     and <c>RunOnUiPost</c> run inline on the caller.</item>
///   <item><b>Null action</b> — both throw <c>ArgumentNullException</c>.</item>
/// </list>
/// <para>
/// The production cross-thread hop is asserted indirectly by
/// <see cref="DbcViewModelTests"/>: those tests raise <c>DbcLoaded</c>
/// on the xunit thread, and the (now fixed) helper guarantees the
/// inline path because no <c>Application</c> exists. The 3 production
/// VMs all share the same helper, so a fix in one place covers all
/// three call sites.
/// </para>
/// </summary>
public class DispatcherExtensionsTests
{
    [Fact]
    public void RunOnUi_Inline_When_Application_Is_Null()
    {
        // xunit runs on MTA with no WPF Application. The action must run
        // synchronously on the calling thread.
        var callingThread = Environment.CurrentManagedThreadId;
        var seenThread = 0;
        var ran = false;

        ((Action)(() =>
        {
            ran = true;
            seenThread = Environment.CurrentManagedThreadId;
        })).RunOnUi();

        ran.Should().BeTrue();
        seenThread.Should().Be(callingThread, "no Application → inline on caller");
    }

    [Fact]
    public void RunOnUiPost_Inline_When_Application_Is_Null()
    {
        var callingThread = Environment.CurrentManagedThreadId;
        var seenThread = 0;
        var ran = false;

        ((Action)(() =>
        {
            ran = true;
            seenThread = Environment.CurrentManagedThreadId;
        })).RunOnUiPost();

        ran.Should().BeTrue();
        seenThread.Should().Be(callingThread, "no Application → inline on caller");
    }

    [Fact]
    public void RunOnUi_Throws_On_Null_Action()
    {
        Action nullAction = null!;
        var act = () => nullAction.RunOnUi();
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void RunOnUiPost_Throws_On_Null_Action()
    {
        Action nullAction = null!;
        var act = () => nullAction.RunOnUiPost();
        act.Should().Throw<ArgumentNullException>();
    }
}
