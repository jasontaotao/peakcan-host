using System.Collections.ObjectModel;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.Services.Uds;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public sealed class TraceViewerUdsHandoffTests
{
    private static TraceViewerViewModel ChatVm()
    {
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(Array.Empty<TraceSource>());
        var dbcService = Substitute.For<DbcService>(Substitute.For<ILogger<DbcService>>());
        var sessionLibrary = new TraceSessionLibrary(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"uds-handoff-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);
        return new TraceViewerViewModel(
            session, registry, dbcService, NullLogger<TraceViewerViewModel>.Instance, sessionLibrary);
    }

    [Fact]
    public void AcceptChatHandoff_prefills_without_sending()
    {
        var vm = ChatVm();
        var payload = new UdsChatHandoffPayload("问题", "按发送开始", Guid.NewGuid(), "summary", "tx");

        vm.AcceptChatHandoff(payload);

        vm.ChatInput.Should().Be("问题");
        vm.ChatPrefillHint.Should().Be("按发送开始");
        vm.RightPanelSelectedIndex.Should().Be(TraceViewerViewModel.ChatPanelTabIndex);
        vm.ChatInputFocusTicket.Should().BeGreaterThan(0);
        vm.ChatMessages.Should().BeEmpty("Handoff 只预填，不自动发送");

        var system = vm.BuildSystemMessageForTest();
        system.Should().Contain(payload.ReportId.ToString());
        system.Should().Contain("其它 trace/DBC/NM 数据不是证据");
    }

    [Fact]
    public void AcceptChatHandoff_does_not_clobber_an_existing_draft()
    {
        var vm = ChatVm();
        vm.ChatInput = "我还没发出去的草稿";

        vm.AcceptChatHandoff(new UdsChatHandoffPayload("问题", "提示", Guid.NewGuid(), "summary", "tx"));

        vm.ChatInput.Should().Be("我还没发出去的草稿", "用户输入不得被静默丢弃");
        vm.ChatPrefillHint.Should().Contain("草稿");
        vm.RightPanelSelectedIndex.Should().Be(TraceViewerViewModel.ChatPanelTabIndex);
    }

    [Fact]
    public void Confirmed_draft_replacement_swaps_in_the_handoff_prompt()
    {
        var vm = ChatVm();
        vm.ChatInput = "旧草稿";
        vm.ConfirmReplaceDraft = _ => true;

        vm.AcceptChatHandoff(new UdsChatHandoffPayload("新问题", "提示", Guid.NewGuid(), "summary", "tx"));

        vm.ChatInput.Should().Be("新问题");
        vm.ChatPrefillHint.Should().Be("提示");
    }

    [Fact]
    public void System_message_without_handoff_has_no_uds_rules()
    {
        var vm = ChatVm();
        vm.BuildSystemMessageForTest().Should().NotContain("UDS 离线分析 Handoff 规则");
    }
}
