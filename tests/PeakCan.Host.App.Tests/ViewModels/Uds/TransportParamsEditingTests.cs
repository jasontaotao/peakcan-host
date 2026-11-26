using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.Uds;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.App.ViewModels.Uds.FlashPipeline;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.FlashPipeline;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Uds.IsoTp;
using Xunit;
using PeakCan.Host.Core.Uds.IsoTp;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

/// <summary>
/// 通讯参数面板的**可编辑**部分：校验、仲裁、应用、三态来源标注。
/// <para>
/// 重点验证三件容易做错的事：
/// 1. 非法输入必须在**改动任何状态之前**被拒（不能先应用再报错）。
/// 2. 落盘失败不能谎报成功。
/// 3. 来源标注必须是三态——「用户配置」不能被显示成「本地默认」。
/// </para>
/// </summary>
public sealed class TransportParamsEditingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"uds-edit-{Guid.NewGuid():N}");
    private string SettingsPath => Path.Combine(_dir, "uds-transport.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class RefusingFactory : ISecondaryFlashStackFactory
    {
        public ISecondaryFlashStack Build(FlashStepSnapshot securityStep, FlashProfile profile)
            => throw new NotImplementedException();
    }

    private (TransportParamsViewModel Vm, IsoTpLayer Iso, UdsClient Client, UdsTransportSettingsStore Store)
        NewVm()
    {
        var iso = new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { });
        var client = new UdsClient(iso, new UdsTimer());
        var store = new UdsTransportSettingsStore(
            NullLogger<UdsTransportSettingsStore>.Instance, SettingsPath);
        var flash = new FlashPanelViewModel(new RefusingFactory(), NullLogger<FlashPanelViewModel>.Instance);
        var vm = new TransportParamsViewModel(iso, client, flash, NullLogger<TransportParamsViewModel>.Instance);
        vm.AttachSettingsStore(store);
        return (vm, iso, client, store);
    }

    // ---- 表单载入 ----

    [Fact]
    public void AttachSettingsStore_Populates_The_Form_From_Persisted_Settings()
    {
        var store = new UdsTransportSettingsStore(NullLogger<UdsTransportSettingsStore>.Instance, SettingsPath);
        store.Save(UdsTransportSettings.Default with { RequestId = 0x714, ResponseId = 0x760 });

        var iso = new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { });
        var vm = new TransportParamsViewModel(iso, new UdsClient(iso, new UdsTimer()),
            new FlashPanelViewModel(new RefusingFactory(), NullLogger<FlashPanelViewModel>.Instance),
            NullLogger<TransportParamsViewModel>.Instance);

        vm.AttachSettingsStore(store);

        vm.EditRequestId.Should().Be("714");
        vm.EditResponseId.Should().Be("760");
    }

    // ---- 校验 ----

    [Fact]
    public void Apply_Rejects_NonHex_RequestId_Without_Touching_The_Live_Config()
    {
        var (vm, iso, _, _) = NewVm();
        vm.EditRequestId = "ZZZ";

        vm.ApplyCommand.Execute(null);

        vm.EditStatus.Should().Contain("请求 ID");
        iso.Config.RequestId.Should().Be(0x7E0, "校验失败不得改动运行时配置");
    }

    [Fact]
    public void Apply_Rejects_Identical_Request_And_Response_Id()
    {
        var (vm, _, _, _) = NewVm();
        vm.EditRequestId = "7E0";
        vm.EditResponseId = "7E0";

        vm.ApplyCommand.Execute(null);

        vm.EditStatus.Should().Contain("不能相同");
    }

    [Fact]
    public void Apply_Rejects_NonPositive_Timeout()
    {
        var (vm, _, _, _) = NewVm();
        vm.EditP2Ms = "0";

        vm.ApplyCommand.Execute(null);

        vm.EditStatus.Should().Contain("P2");
    }

    [Fact]
    public void Apply_Reports_The_OutOfRange_Id_From_The_Transport_Layer()
    {
        var (vm, iso, _, _) = NewVm();
        vm.EditRequestId = "800"; // 超出标准帧 11 位

        vm.ApplyCommand.Execute(null);

        vm.EditStatus.Should().Contain("范围");
        iso.Config.RequestId.Should().Be(0x7E0);
    }

    // ---- 应用成功 ----

    [Fact]
    public void Apply_Updates_Live_Config_And_Persists()
    {
        var (vm, iso, _, store) = NewVm();
        vm.EditRequestId = "714";
        vm.EditResponseId = "760";
        vm.EditP2StarMs = "8000";

        vm.ApplyCommand.Execute(null);

        vm.EditStatus.Should().Contain("已应用");
        iso.Config.RequestId.Should().Be(0x714);
        iso.Config.ResponseId.Should().Be(0x760);

        var reloaded = new UdsTransportSettingsStore(
            NullLogger<UdsTransportSettingsStore>.Instance, SettingsPath);
        reloaded.Load();
        reloaded.Current.RequestId.Should().Be(0x714, "应用后必须真的落盘");
        reloaded.Current.P2StarMs.Should().Be(8000);
    }

    [Fact]
    public void Apply_Sets_UserConfig_As_The_Timing_Source()
    {
        var (vm, _, client, _) = NewVm();
        vm.EditP2StarMs = "8000";

        vm.ApplyCommand.Execute(null);
        vm.Poll();

        client.Session.TimingSource.Should().Be(UdsTimingSource.UserConfig);
        vm.DiagP2Star.Should().Contain("用户配置",
            "用户配置的时序不能显示成「本地默认」——那是谎报");
    }

    [Fact]
    public void Apply_Writes_The_Effective_Timer_Not_Just_The_Display()
    {
        // HIGH 回归：真正决定请求超时的是 UdsClient._timer，不是 Session。
        // 只断言 Session/显示文案的测试在 timer 完全没被写时依然会绿。
        var (vm, _, client, _) = NewVm();
        vm.EditP2Ms = "2000";
        vm.EditP2StarMs = "9000";

        vm.ApplyCommand.Execute(null);

        client.TimerForTesting.P2Timeout.Should().Be(TimeSpan.FromMilliseconds(2000),
            "用户配的 P2 必须作用到真正生效的 timer 上");
        client.TimerForTesting.P2StarTimeout.Should().Be(TimeSpan.FromMilliseconds(9000));
    }

    [Fact]
    public void Apply_Timing_Only_Does_Not_Reset_The_Local_Session()
    {
        // 用户只改时序时不该被顺手踢回默认会话——ECU 其实还在编程会话里。
        var (vm, _, client, _) = NewVm();
        client.Session.SetSession(0x03, p2: 50, p2Star: 5000);
        vm.AttachSettingsStore(new UdsTransportSettingsStore(
            NullLogger<UdsTransportSettingsStore>.Instance, SettingsPath));

        vm.EditP2Ms = "2000";
        vm.ApplyCommand.Execute(null);

        client.Session.SessionType.Should().Be(0x03,
            "只改时序不应改变会话状态（寻址没变，ECU 也没复位）");
    }

    [Fact]
    public void Apply_Applies_NBs_And_NCr_To_The_Transport()
    {
        var (vm, iso, _, _) = NewVm();
        vm.EditNBsMs = "1500";
        vm.EditNCrMs = "2000";

        vm.ApplyCommand.Execute(null);

        iso.FlowControlTimeout.Should().Be(TimeSpan.FromMilliseconds(1500));
        iso.ReceiveTimeout.Should().Be(TimeSpan.FromMilliseconds(2000));
    }

    [Fact]
    public void Apply_Reports_Persistence_Failure_Without_Claiming_Success()
    {
        var (vm, _, _, _) = NewVm();
        // 用一个「目录」占据文件路径，让落盘必然失败。
        Directory.CreateDirectory(SettingsPath);
        vm.EditRequestId = "714";

        vm.ApplyCommand.Execute(null);

        vm.EditStatus.Should().Contain("保存到磁盘失败",
            "落盘失败必须如实告诉用户，不能只说「已应用」");
    }

    // ---- 重新载入 / 恢复默认 ----

    [Fact]
    public void Reload_Discards_Unsaved_Edits()
    {
        var (vm, _, _, store) = NewVm();
        store.Save(UdsTransportSettings.Default with { RequestId = 0x714 });
        vm.AttachSettingsStore(store);

        vm.EditRequestId = "999";
        vm.HasUnsavedEdits.Should().BeTrue("前置条件：有未保存编辑");

        vm.ReloadCommand.Execute(null);

        vm.EditRequestId.Should().Be("714", "重新载入要回到已保存的配置");
        vm.HasUnsavedEdits.Should().BeFalse();
    }

    [Fact]
    public void RestoreDefaults_Fills_The_Form_Without_Persisting()
    {
        var (vm, _, _, store) = NewVm();
        store.Save(UdsTransportSettings.Default with { RequestId = 0x714 });
        vm.AttachSettingsStore(store);

        vm.RestoreDefaultsCommand.Execute(null);

        vm.EditRequestId.Should().Be("7E0");
        vm.HasUnsavedEdits.Should().BeTrue("恢复默认只填表单，需要再点应用");
        store.Current.RequestId.Should().Be(0x714, "未点应用前不得改动已保存的配置");
    }

    // ---- 三态来源标注 ----

    [Fact]
    public void TimingSource_Shows_EcuNegotiated_When_The_Ecu_Reported_Values()
    {
        var (vm, _, client, _) = NewVm();
        client.Session.SetSession(0x02, p2: 25, p2Star: 7500);

        vm.Poll();

        vm.DiagP2Star.Should().Contain("ECU 0x10 协商");
    }

    [Fact]
    public void TimingSource_Shows_Default_Before_Any_Configuration()
    {
        var (vm, _, _, _) = NewVm();

        vm.Poll();

        vm.DiagP2.Should().Contain("本地默认");
    }

    // ---- 十六进制解析容错 ----

    [Theory]
    [InlineData("7E0", 0x7E0u)]
    [InlineData("0x7E0", 0x7E0u)]
    [InlineData(" 0X7e0 ", 0x7E0u)]
    public void TryParseHex_Tolerates_Prefix_And_Case(string input, uint expected)
    {
        TransportParamsViewModel.TryParseHex(input, out var value).Should().BeTrue();
        value.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("XYZ")]
    [InlineData("0x")]
    public void TryParseHex_Rejects_Invalid_Input(string input)
    {
        TransportParamsViewModel.TryParseHex(input, out _).Should().BeFalse();
    }
}
