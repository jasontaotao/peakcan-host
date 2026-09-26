using A2lEditor.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Model;
using A2lEditor.Core.Layout;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.App.ViewModels.Xcp;
using PeakCan.Host.App.Tests.TestKit;
using PeakCan.Host.Core.Xcp.Calibration;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Core.Xcp.Receive;
using PeakCan.Host.Core.Xcp.Scheduling;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Xcp;

/// <summary>
/// S5-T4 写回面板 VM 测试（spec D5）：行内写值链路（真实 writer + 内存从机）、
/// 参数集导出（指纹绑定、数值面来自卡片）、批量下发（差异项写入）、无连接拒绝。
/// </summary>
public sealed class XcpWritebackViewModelTests
{
    private static readonly CanId MasterId = new(0x18FFF667, FrameFormat.Extended);
    private static readonly string[] DefaultMeasured = ["DOWNLOAD", "UPLOAD", "SET_MTA"];

    private static readonly Lazy<(A2lDocument Doc, ContractSet Contracts, List<(string Name, uint Addr, ValueContract C)> Cals)> Fixture =
        new(() =>
        {
            var path = FindSharedRealA2L();
            var parsed = Asap2PackageApi.ParseFile(path);
            Assert.NotNull(parsed.Value);
            var doc = parsed.Value!;
            var contracts = Asap2PackageApi.Contracts(doc);
            var cals = new List<(string, uint, ValueContract)>();
            foreach (var c in contracts.All)
            {
                if (c.Category != A2lObjectCategory.Characteristic || c.DataType is null)
                    continue;
                if (c.Segments.Count != 1 || c.TotalByteLength != 1)
                    continue;
                if (!XcpAddressMap.TryTranslate(doc, c.Segments[0].Address, out var physical))
                    continue;
                cals.Add((c.ObjectName, (uint)physical, c));
                if (cals.Count == 2)
                    break;
            }
            Assert.True(cals.Count >= 2, "fixture 至少要有两个可写的 1B 标定对象");
            return (doc, contracts, cals);
        });

    private static string FindSharedRealA2L()
    {
        var inOutput = System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "App_merge_INCA.a2l");
        if (System.IO.File.Exists(inOutput))
            return inOutput;
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(
                dir!.FullName, "PeakCan.Host.Core.Tests", "TestData", "App_merge_INCA.a2l");
            if (System.IO.File.Exists(candidate))
                return candidate;
        }
        throw new InvalidOperationException("共享真机 A2L fixture 缺失");
    }

    private static string Sha => "a" + new string('0', 63);

    private static (XcpCardPanelViewModel Cards, MemorySlaveTransport Slave, XcpTransportSpy Spy, XcpMaster Master,
        XcpWritebackViewModel Vm, List<(string Name, uint Addr, ValueContract C)> Cals) Make(
        Func<XcpMaster?>? masterProvider = null,
        System.Collections.Generic.IReadOnlyList<string>? measured = null)
    {
        var (doc, contracts, cals) = Fixture.Value;
        var cards = new XcpCardPanelViewModel();
        foreach (var (name, _, contract) in cals)
            cards.AddWatch(name, "CHARACTERISTIC", contract);

        var slave = new MemorySlaveTransport();
        var spy = new XcpTransportSpy(slave);
        var master = new XcpMaster(spy, new XcpMasterOptions(MasterId, TimeSpan.FromMilliseconds(100), 0));

        var vm = new XcpWritebackViewModel(
            cards,
            masterProvider ?? (() => master),
            snapshotFactory: () => new ContractSnapshot(1, Sha, new A2lFingerprint(0, 0, 0, 0, 0), "test", []),
            documentProvider: () => doc,
            measuredCommandsProvider: () => measured ?? DefaultMeasured);
        cards.AttachWriteback(vm.WriteSingleAsync);
        return (cards, slave, spy, master, vm, cals);
    }

    private static void FeedCard(XcpCardViewModel card, double value) =>
        card.Update(new XcpDaqSample(
            new PlannedDaqEntry(1, 0, 0, card.Name, 0, 2, 0, 0x2000, null), value, DateTimeOffset.UtcNow));

    [Fact]
    public async Task WriteSingleAsync_writes_via_writer_and_memory_updated()
    {
        var (cards, slave, _, _, vm, cals) = Make();
        var (name, addr, contract) = cals[0];
        var card = vm_cards(cards, name)!;

        var outcome = await vm.WriteSingleAsync(card, 200);

        outcome.Status.Should().Be(CalibrationWriteStatus.Written);
                var expected = new byte[contract.TotalByteLength];
        contract.Encode(200, expected);
        slave.Memory.AsSpan((int)(addr & 0xFFFF), expected.Length).ToArray().Should().Equal(expected);
    }

    [Fact]
    public async Task WriteSingleAsync_without_master_fails_visible()
    {
        var (cards, _, _, _, vm, cals) = Make(masterProvider: () => null);
        var (name, _, _) = cals[0];
        var card = vm_cards(cards, name)!;

        var outcome = await vm.WriteSingleAsync(card, 1);

        outcome.Status.Should().Be(CalibrationWriteStatus.WriteFailed);
        outcome.Detail.Should().Contain("采集未运行");
    }

    [Fact]
    public void Card_write_command_disabled_without_handler_or_value()
    {
        var (cards, _, _, _, _, cals) = Make();
        var card = cards.Cards.First(c => c.Name == cals[0].Name);
        card.CanWrite.Should().BeTrue("已接线且是 CHARACTERISTIC");
        card.WriteValueCommand.CanExecute(null).Should().BeFalse("无写入值文本");

        card.WriteValueText = "200";
        card.WriteValueCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Export_parameter_set_uses_card_numeric_values_and_binds_fingerprint()
    {
        var (cards, _, _, _, vm, cals) = Make();
        FeedCard(cards.Cards.First(c => c.Name == cals[0].Name), 200);
        FeedCard(cards.Cards.First(c => c.Name == cals[1].Name), 33);

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s5exp_{Guid.NewGuid():N}.json");
        vm.ParameterSetPath = path;
        await vm.ExportParameterSetCommand.ExecuteAsync(null);

        System.IO.File.Exists(path).Should().BeTrue();
        var parsed = CalibrationParameterSet.Parse(System.IO.File.ReadAllText(path));
        parsed.A2lSha256.Should().Be(Sha);
        parsed.Entries.Should().Contain(e => e.Name == cals[0].Name && e.Physical == 200);
        parsed.Entries.Should().Contain(e => e.Name == cals[1].Name && e.Physical == 33);
        vm.StatusText.Should().Contain("已导出 2 项");
    }

    [Fact]
    public async Task Apply_parameter_set_writes_diff_items_and_reports()
    {
        var (_, slave, _, _, vm, cals) = Make();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s5app_{Guid.NewGuid():N}.json");
        var set = CalibrationParameterSet.Export(
            new List<CalibrationEntry>
            {
                new(cals[0].Name, 11, null, null),
                new(cals[1].Name, 22, null, null),
            }, Sha, "test");
        System.IO.File.WriteAllBytes(path, set.ToJsonBytes());
        vm.ParameterSetPath = path;

        // 两阶段确认（S5 评审 P2-2）：首击只对账（零写流量），再击确认才写入。
        await vm.ApplyParameterSetCommand.ExecuteAsync(null);
        slave.DownloadCount.Should().Be(0, "首击只对账不写入");
        vm.StatusText.Should().Contain("差异");

        await vm.ApplyParameterSetCommand.ExecuteAsync(null);

        vm.StatusText.Should().Contain("写 2");
        slave.Memory[(int)(cals[0].Addr & 0xFFFF)].Should().NotBe(0);
        slave.Memory[(int)(cals[1].Addr & 0xFFFF)].Should().NotBe(0);
    }

    [Fact]
    public async Task Apply_without_confirm_only_reconciles_with_zero_write_traffic()
    {
        // S5 评审 P2-2 回归钉：首击 = 对账预览（零写流量）。
        var (_, slave, _, _, vm, cals) = Make();
        vm.ParameterSetPath = NewParamFile(cals);
        vm.PendingReport.Should().BeNull();

        await vm.ApplyParameterSetCommand.ExecuteAsync(null);

        vm.PendingReport.Should().NotBeNull();
        slave.DownloadCount.Should().Be(0);
        vm.StatusText.Should().Contain("2 项差异");
    }

    [Fact]
    public async Task Capability_gate_rejects_without_traffic()
    {
        // S5 评审 P2-1 回归钉：实测命令面无 DOWNLOAD → 拒绝且零线上流量。
        var (cards, slave, spy, _, vm, cals) = Make(measured: []);
        var (name, _, _) = cals[0];
        var card = vm_cards(cards, name)!;

        var outcome = await vm.WriteSingleAsync(card, 1);

        outcome.Status.Should().Be(CalibrationWriteStatus.Rejected);
        outcome.Detail.Should().Contain("能力对账");
        spy.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task Write_single_rejected_during_batch_apply()
    {
        // S5 评审 P1-2 回归钉：批量进行中卡片写值被拒（序列原子性保护）。
        var (cards, slave, _, _, vm, cals) = Make();
        vm.ParameterSetPath = NewParamFile(cals);
        await vm.ApplyParameterSetCommand.ExecuteAsync(null); // 首击 → pending
        vm.IsBusy.Should().BeFalse("首击对账完成后 IsBusy 复位");
        // 模拟批量进行中：直接验证 WriteSingleAsync 在 IsBusy 下的行为
        typeof(XcpWritebackViewModel)
            .GetProperty("IsBusy")!.SetValue(vm, true);
        try
        {
            var card = vm_cards(cards, cals[0].Name)!;
            var outcome = await vm.WriteSingleAsync(card, 1);
            outcome.Status.Should().Be(CalibrationWriteStatus.WriteFailed);
            outcome.Detail.Should().Contain("批量下发进行中");
        }
        finally
        {
            typeof(XcpWritebackViewModel).GetProperty("IsBusy")!.SetValue(vm, false);
        }
        await vm.ApplyParameterSetCommand.ExecuteAsync(null); // 清 pending
    }

    private static string NewParamFile(List<(string Name, uint Addr, ValueContract C)> cals)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s5app_{Guid.NewGuid():N}.json");
        var set = CalibrationParameterSet.Export(
            new List<CalibrationEntry>
            {
                new(cals[0].Name, 11, null, null),
                new(cals[1].Name, 22, null, null),
            }, Sha, "test");
        System.IO.File.WriteAllBytes(path, set.ToJsonBytes());
        return path;
    }

    [Fact]
    public async Task Card_row_write_updates_memory_via_handler()
    {
        var (cards, slave, _, _, vm, cals) = Make();
        var card = cards.Cards.First(c => c.Name == cals[0].Name);
        card.WriteValueText = "77";

        card.WriteValueCommand.Execute(null);
        await WaitUntilAsync(() => card.LastWriteStatus is not null);

        card.LastWriteStatus.Should().Contain("Written");
        slave.Memory[(int)(cals[0].Addr & 0xFFFF)].Should().Be(77);
    }

    private static XcpCardViewModel? vm_cards(XcpCardPanelViewModel cards, string name) =>
        cards.Cards.FirstOrDefault(c => c.Name == name);

    private static async System.Threading.Tasks.Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await System.Threading.Tasks.Task.Delay(20);
        condition().Should().BeTrue("2 s 内条件未成立");
    }
}
