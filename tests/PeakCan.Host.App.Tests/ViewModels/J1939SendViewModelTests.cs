using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.J1939;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Task 19: <see cref="J1939SendViewModel"/> send-mode routing matrix —
/// 单帧（Compose 直发 SendService）、BAM/RTS-CTS（路由到 J1939TpLayer）、
/// ≤8B 载荷的 TP 模式指引错误、RTS-CTS 的 DA 必填校验。
/// </summary>
public class J1939SendViewModelTests
{
    /// <summary>
    /// Captures every frame handed to <see cref="SendService.SendAsync"/>
    /// (the 单帧 route). Subclassing with a virtual override mirrors the
    /// established <c>FakeSendService</c> precedent in
    /// <see cref="SendViewModelTests"/> (SendService ctor needs a logger).
    /// </summary>
    private sealed class CapturingSendService : SendService
    {
        public CapturingSendService() : base(NullLogger<SendService>.Instance) { }

        public List<CanFrame> Sent { get; } = new();

        // 修订（有据，task-19）：brief 原稿 Ok(Unit.Value)——包内 Unit 为空结构体、
        // 无 Value 成员（CS0117，实测；SendFlow.cs Task 5 同款裁定，全仓先例均为 Ok(default)）。
        public override ValueTask<Result<Unit>> SendAsync(CanFrame frame, CancellationToken ct = default)
        {
            Sent.Add(frame);
            return ValueTask.FromResult(Result<Unit>.Ok(default));
        }
    }

    private static (J1939SendViewModel vm, CapturingSendService send, List<CanFrame> tpSent) Create()
    {
        var send = new CapturingSendService();
        var tpSent = new List<CanFrame>();
        var layer = new J1939TpLayer(
            (frame, _) => { tpSent.Add(frame); return ValueTask.FromResult(Result<Unit>.Ok(default)); },
            new J1939TpOptions { BamIntervalMs = 0 });
        var vm = new J1939SendViewModel(layer, send, NullLogger<J1939SendViewModel>.Instance);
        return (vm, send, tpSent);
    }

    [Fact]
    public void Single_Mode_Sends_Composed_Frame_With_Da()
    {
        var (vm, send, _) = Create();
        vm.PgnHex = "0x2600"; vm.Priority = "6"; vm.SourceAddressHex = "0x56";
        vm.DestinationAddressHex = "0xF4"; vm.ModeIndex = 0; vm.PayloadHex = "01 01 00";

        vm.SendCommand.Execute(null);

        send.Sent.Should().ContainSingle();
        send.Sent[0].Id.Raw.Should().Be(J1939Id.Compose(6, 0x002600, 0x56, 0xF4));
    }

    [Fact]
    public void Bam_Mode_Routes_To_Tp_Layer()
    {
        var (vm, send, tpSent) = Create();
        vm.PgnHex = "0x0200"; vm.Priority = "6"; vm.SourceAddressHex = "0xF4";
        vm.ModeIndex = 1; vm.PayloadHex = string.Join(" ", Enumerable.Range(1, 49).Select(i => i.ToString("X2", CultureInfo.InvariantCulture)));

        vm.SendCommand.Execute(null);

        tpSent.Should().NotBeEmpty();          // BAM CM + DT
        send.Sent.Should().BeEmpty();          // 不走单帧通道
    }

    [Fact]
    public void Payload_Under_9B_With_Tp_Mode_Shows_Guidance_Error()
    {
        var (vm, send, tpSent) = Create();
        vm.PgnHex = "0x0200"; vm.SourceAddressHex = "0xF4"; vm.ModeIndex = 1;
        vm.PayloadHex = "01 02 03";

        vm.SendCommand.Execute(null);

        vm.Status.Should().Contain("单帧");
        tpSent.Should().BeEmpty();
    }

    [Fact]
    public void RtsCts_Mode_Requires_Da()
    {
        var (vm, send, tpSent) = Create();
        vm.PgnHex = "0x0200"; vm.SourceAddressHex = "0xF4"; vm.DestinationAddressHex = "";
        vm.ModeIndex = 2; vm.PayloadHex = string.Join(" ", Enumerable.Range(1, 20).Select(i => i.ToString("X2", CultureInfo.InvariantCulture)));

        vm.SendCommand.Execute(null);

        vm.Status.Should().Contain("目标地址");
        tpSent.Should().BeEmpty();
    }
}
