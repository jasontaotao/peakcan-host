using System;
using System.Collections.Generic;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core.Dbc;
using Xunit;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// spec §6 首次实现预算守卫：选中 FD 64 字节消息（64×8 格）的 VM 投影 ≤30ms，
/// 包含生产选中路径里的 <c>SyncLive</c> 实时投影一遍；不含 WPF 实际渲染。
/// <para>
/// 把阈值临时改成 0.0 只能证明断言接了线；真正钉住工作量的是结构断言
/// （64 行 × 8 格、实时值已应用）。
/// </para>
/// </summary>
public class DbcLayoutPerformanceTests
{
    // 与 DbcLayoutViewModelTests 同口径：清掉兄弟测试类可能泄漏的 Application，
    // 避免 OnLoaded → RunOnUi 封送到无人泵的 dispatcher。
    public DbcLayoutPerformanceTests() => LeakedApplicationReset.CleanupLeakedApplication();

    private static Signal Sig(string name, ushort start, byte len)
        => new(name, start, len, ByteOrder.LittleEndian, DbcValueType.Unsigned,
               1, 0, 0, 0, "", Array.Empty<string>(), false, false, null);

    private static DbcViewModel NewVm(Message message, LiveFrameSlot? liveSlot = null)
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = new DbcViewModel(
            svc, new SignalViewModel(), NullLogger<DbcViewModel>.Instance,
            liveSlot: liveSlot);
        var doc = new DbcDocument(
            "", new List<Node>(), new List<Message> { message },
            new Dictionary<uint, Message>(),
            new Dictionary<string, ValueTable>());
        svc.GetType().GetEvent(nameof(DbcService.DbcLoaded))!.RaiseMethod(svc, doc);
        return vm;
    }

    [Fact]
    public void Selecting_Fd512CellMessage_Projects_Within_Thirty_Milliseconds()
    {
        var signals = new List<Signal>(8);
        for (var i = 0; i < 8; i++) signals.Add(Sig($"S{i}", (ushort)(i * 64), 64));
        var message = new Message(0x100, "Perf", 64, "ECU", signals, false, null);

        var slot = new LiveFrameSlot();
        using var vm = NewVm(message, slot);
        var row = vm.Messages.Single();

        // 预置一帧：生产选中路径会同步跑一遍 SyncLive 实时投影（512 格赋值）。
        var payload = new byte[64];
        payload[0] = 0x05;
        slot.SetTarget(0x100);
        slot.Publish(0x100, payload);

        // JIT / 首次绑定预热，不参与计时。
        for (var i = 0; i < 4; i++)
        {
            vm.SelectedMessage = row;
            vm.StopLiveTimerForTest();
            vm.SelectedMessage = null;
            slot.SetTarget(0x100);
            slot.Publish(0x100, payload);
        }

        var samples = new double[20];
        for (var i = 0; i < samples.Length; i++)
        {
            var sw = Stopwatch.StartNew();
            vm.SelectedMessage = row;
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
            vm.StopLiveTimerForTest();
            vm.SelectedMessage = null;
            slot.SetTarget(0x100);
            slot.Publish(0x100, payload);
        }

        Array.Sort(samples);
        var median = samples[samples.Length / 2];

        vm.SelectedMessage = row;
        vm.StopLiveTimerForTest();
        vm.LayoutRows.Should().HaveCount(64);
        vm.LayoutRows.Should().OnlyContain(r => r.Cells.Count == 8);
        vm.VisibleSignals.Should().OnlyContain(r => r.LiveValue != "—",
            "选中路径必须包含 SyncLive 的实时投影，否则性能守卫低估真实成本");
        median.Should().BeLessThanOrEqualTo(30.0,
            "spec §6 首次实现预算：FD 512 格 ≤30ms（中位数，含实时投影，不含真实渲染）");
    }
}
