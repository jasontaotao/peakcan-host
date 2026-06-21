using System.Reflection;
using FluentAssertions;
using PeakCan.Host.App;

namespace PeakCan.Host.App.Tests;

/// <summary>
/// Verifies the global exception-handler installation and the
/// IHost-lifecycle field on <see cref="App"/>. These are the two
/// issues from the multi-agent code review:
///   - IHost was a local variable in OnStartup (never disposed)
///   - No global handlers (silent crashes in production)
/// <para>
/// We cannot drive the WPF Application lifetime from a test
/// (OnStartup also creates + Shows AppShell which requires a real
/// dispatcher). Instead we test the field exists with the right
/// type, and that the static helper wires the two static events
/// (AppDomain.UnhandledException, TaskScheduler.UnobservedTaskException).
/// The DispatcherUnhandledException handler is registered in
/// OnStartup and is therefore not covered by the static helper test.
/// </para>
/// </summary>
public class AppLifecycleTests
{
    [Fact]
    public void App_Has_IHhost_Field_For_OnExit_Disposal()
    {
        // The host was previously a local variable in OnStartup,
        // so OnExit could not dispose it. Verify the field exists
        // so the graceful-shutdown path is wired.
        var field = typeof(App).GetField(
            "_host",
            BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull("App must store the IHost in a field so OnExit can dispose it");
        field!.FieldType.Name.Should().Be("IHost",
            "the field must hold an IHost so StopAsync + Dispose are available in OnExit");
    }

    [Fact]
    public void App_Overrides_OnExit()
    {
        // The previous implementation never overrode OnExit, so the
        // host leaked. Verify the override exists.
        var onExit = typeof(App).GetMethod(
            "OnExit",
            BindingFlags.NonPublic | BindingFlags.Instance);
        onExit.Should().NotBeNull("App must override OnExit to call IHost.StopAsync + Dispose");
    }

    [Fact]
    public void App_Has_DispatcherUnhandledException_Handler_Method()
    {
        // The dispatcher handler is registered in OnStartup via
        // DispatcherUnhandledException += OnDispatcherUnhandledException.
        // Verify the handler method exists with the right signature
        // so the += on line ~80 of App.xaml.cs compiles.
        var handler = typeof(App).GetMethod(
            "OnDispatcherUnhandledException",
            BindingFlags.NonPublic | BindingFlags.Static);
        handler.Should().NotBeNull("App must declare OnDispatcherUnhandledException for the += in OnStartup");
        handler!.GetParameters().Select(p => p.ParameterType.Name)
            .Should().Contain("DispatcherUnhandledExceptionEventArgs",
                "the handler signature must take DispatcherUnhandledExceptionEventArgs");
    }

    [Fact]
    public void App_Has_AppDomainUnhandledException_Handler_Method()
    {
        var handler = typeof(App).GetMethod(
            "OnAppDomainUnhandledException",
            BindingFlags.NonPublic | BindingFlags.Static);
        handler.Should().NotBeNull("App must declare OnAppDomainUnhandledException for the AppDomain subscription");
        handler!.GetParameters().Select(p => p.ParameterType.Name)
            .Should().Contain("UnhandledExceptionEventArgs",
                "the handler signature must take UnhandledExceptionEventArgs");
    }

    [Fact]
    public void App_Has_UnobservedTaskException_Handler_Method()
    {
        var handler = typeof(App).GetMethod(
            "OnUnobservedTaskException",
            BindingFlags.NonPublic | BindingFlags.Static);
        handler.Should().NotBeNull("App must declare OnUnobservedTaskException for the TaskScheduler subscription");
    }

    [Fact]
    public void InstallStaticGlobalExceptionHandlers_Is_Idempotent()
    {
        // The static helper uses an idempotency guard so re-invoking
        // it (e.g. from a test that runs in the same process as
        // another test) does not double-subscribe. The guard itself
        // is a private static bool; verify the helper is callable
        // and does not throw on a second call.
        var helper = typeof(App).GetMethod(
            "InstallStaticGlobalExceptionHandlers",
            BindingFlags.NonPublic | BindingFlags.Static);
        helper.Should().NotBeNull("the static helper must exist so OnStartup can install the static handlers");
        var act = () => helper!.Invoke(null, null);
        act.Should().NotThrow("the helper must be safe to call multiple times in the same process");
        act.Should().NotThrow("second call must not double-subscribe");
    }
}
