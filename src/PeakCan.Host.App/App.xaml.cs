using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App;

/// <summary>
/// WPF application bootstrap. Builds the DI host in <see cref="OnStartup"/>,
/// stores the <see cref="IServiceProvider"/> on a static for ad-hoc resolution,
/// and shows the <see cref="AppShell"/> window with the
/// <see cref="AppShellViewModel"/> resolved from DI.
/// <para>
/// We intentionally do <i>not</i> set <c>StartupUri</c> in <c>App.xaml</c>:
/// the shell's <c>DataContext</c> must come from the DI container so that
/// its constructor (ChannelRouter, ILogger, …) is invoked.
/// </para>
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Process-wide DI service provider, set during <see cref="OnStartup"/>.
    /// Exposed for view-model code that needs ad-hoc resolution (rare —
    /// prefer constructor injection). Never null after startup.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var host = AppHostBuilder.Build();
        Services = host.Services;
        var shell = new AppShell
        {
            DataContext = Services.GetRequiredService<AppShellViewModel>(),
        };
        shell.Show();
    }
}
