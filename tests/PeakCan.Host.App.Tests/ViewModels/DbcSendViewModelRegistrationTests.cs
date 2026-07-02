using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// v2.0.6 PATCH Bug-2 regression: the Send tab's DBC-mode sub-panel
/// (ComboBox, DataGrid, Send button) is wired via
/// <c>SendView.xaml:DataContext="{Binding DbcSend}"</c>. If the DI
/// container doesn't have <see cref="DbcSendViewModel"/> registered,
/// SendViewModel's optional ctor parameter defaults to null and the
/// entire sub-panel silently renders with no items. Pin the
/// registration so a future refactor can't accidentally remove the
/// single AddSingleton line that makes DBC mode usable.
/// </summary>
public sealed class DbcSendViewModelRegistrationTests
{
    [Fact]
    public void DbcSendViewModel_IsRegisteredInDi_AndWiredIntoSendViewModel()
    {
        using var host = new AppHostBuilder().Build();

        // Bug-2 root cause: DbcSendViewModel was never registered. The
        // single resolution below would throw with
        // InvalidOperationException("No service for type
        // 'DbcSendViewModel' has been registered") pre-v2.0.6.
        var dbcSend = host.Services.GetRequiredService<DbcSendViewModel>();
        dbcSend.Should().NotBeNull();

        // Singleton check — same instance on second resolution.
        var dbcSend2 = host.Services.GetRequiredService<DbcSendViewModel>();
        dbcSend2.Should().BeSameAs(dbcSend);

        // SendViewModel.DbcSend must resolve to the same instance, not null.
        // Pre-v2.0.6 this assertion fails with "Expected SendViewModel.DbcSend
        // to be same as <instance>, but found null".
        var sendVm = host.Services.GetRequiredService<SendViewModel>();
        sendVm.DbcSend.Should().BeSameAs(dbcSend,
            "SendViewModel.DbcSend must resolve to the registered DbcSendViewModel instance, not null");
    }
}