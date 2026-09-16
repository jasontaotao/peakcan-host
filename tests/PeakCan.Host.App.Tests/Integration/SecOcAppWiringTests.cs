using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.SecOc;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using PeakCan.Security.SecOc;
using Xunit;

namespace PeakCan.Host.App.Tests.Integration;

/// <summary>
/// 全链 E2E：coordinator 包装 → 真 SecOcChannel RX 验签 → verdict 表 →
/// joiner → TraceViewModel 徽章。覆盖"用户连接后 trace 每帧看到验签状态"的
/// 端到端行为（spec §5-D6.7 用户感知路径）。链路 Emit→SecOcChannel→
/// router→sink→AppendBatchCore 全部同步，无 race。
/// </summary>
public class SecOcAppWiringTests
{
    // 最小 fake（模式同 ChannelConnectionCoordinatorSecOcTests）：连接成功、
    // Write 记录（TX 环回用）、可手动 Emit 帧。
    private sealed class FakeCanChannel : ICanChannel
    {
        public ChannelId Id { get; }
        public bool IsConnected { get; private set; }
#pragma warning disable CS0067
        public event Action<CanFrame>? FrameReceived;
#pragma warning disable CS0067
        public event Action<ReadLoopError>? ReadLoopError;
#pragma warning restore CS0067
#pragma warning restore CS0067
        public List<CanFrame> Written { get; } = new();

        public FakeCanChannel(ChannelId id) => Id = id;

        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        {
            IsConnected = true;
            return Task.FromResult(Result<Unit>.Ok(default));
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        {
            Written.Add(frame);
            return ValueTask.FromResult(Result<Unit>.Ok(default));
        }

        public void Emit(CanFrame frame) => FrameReceived?.Invoke(frame);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static IReadOnlyDictionary<uint, SecOcPduConfig> OnePdu()
        => new Dictionary<uint, SecOcPduConfig>
        {
            [0x123] = new()
            {
                Profile = new SecOcProfile { DataId = 0x0A, FvLenBits = 16, MacLenBits = 24 },
                Key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
                Mode = SecOcPduMode.Both,
            },
        };

    private static ConnectionConfig Cfg(ushort handle = 0x51) => new(
        new ChannelInfo(handle, $"PCAN_USBBUS{handle - 0x50}"),
        BaudRate.CanFd1Mbps, IsFd: true);

    private static CanFrame MakeFrame(uint id, byte[] data, ushort handle = 0x51) => new(
        new CanId(id, FrameFormat.Standard), data, FrameFlags.None,
        new ChannelId(handle), Timestamp.FromMicroseconds(1_000_000UL));

    [Fact]
    public async Task ForgedProtectedFrame_AppearsInTrace_WithRejectedBadge()
    {
        // Arrange: 完整链（coordinator 用真 SecOcChannel wrap）。
        var raw = new FakeCanChannel(new ChannelId(0x51));
        var factory = Substitute.For<IChannelFactory>();
        factory.Create(Arg.Any<ChannelId>()).Returns(raw);
        var verdicts = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(verdicts);
        var coordinator = new ChannelConnectionCoordinator(
            factory, new ChannelRouter(NullLogger<ChannelRouter>.Instance),
            new SendService(NullLogger<SendService>.Instance),
            secOcVerdicts: verdicts, secOcPduProvider: OnePdu, secOcBadgeJoiner: joiner);
        await coordinator.ConnectAllAsync(new[] { Cfg() });

        var trace = new TraceViewModel { SecOcBadgeResolver = joiner.Join };
        var router = new ChannelRouter(NullLogger<ChannelRouter>.Instance);
        router.RegisterChannel(coordinator.Connections.Single().Channel);
        router.AttachSink(new FrameCaptureSink(trace));

        // Act: 伪造 MAC 帧上 RX 路径（16bit FV + 24bit MAC 全 0 → BadMac）。
        raw.Emit(MakeFrame(0x123, new byte[] { 0xAA, 0xBB, 0x00, 0x00, 0x00, 0x00, 0x00 }));

        // Assert: trace 徽章 = ✗ BadMac。
        trace.Entries.Should().ContainSingle();
        trace.Entries[0].SecOcBadge.Kind.Should().Be(SecOcBadgeKind.Rejected);
        trace.Entries[0].SecOcBadge.Text.Should().StartWith("✗");
    }

    [Fact]
    public async Task LegitSignedTx_RxRoundTrip_AppearsAccepted()
    {
        // TX 真签名 → 同帧 RX 环回 → 验签通过 → 徽章 ✓。
        var raw = new FakeCanChannel(new ChannelId(0x51));
        var factory = Substitute.For<IChannelFactory>();
        factory.Create(Arg.Any<ChannelId>()).Returns(raw);
        var verdicts = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(verdicts);
        var coordinator = new ChannelConnectionCoordinator(
            factory, new ChannelRouter(NullLogger<ChannelRouter>.Instance),
            new SendService(NullLogger<SendService>.Instance),
            secOcVerdicts: verdicts, secOcPduProvider: OnePdu, secOcBadgeJoiner: joiner);
        await coordinator.ConnectAllAsync(new[] { Cfg() });

        var trace = new TraceViewModel { SecOcBadgeResolver = joiner.Join };
        var router = new ChannelRouter(NullLogger<ChannelRouter>.Instance);
        router.RegisterChannel(coordinator.Connections.Single().Channel);
        router.AttachSink(new FrameCaptureSink(trace));

        var sent = MakeFrame(0x123, new byte[] { 0x01, 0x02 });
        await coordinator.Connections.Single().Channel.WriteAsync(sent);
        var onWire = raw.Written.Single();
        raw.Emit(onWire); // 环回：线上帧回 RX 路径（同密钥同 FV 首帧 → Accept）

        trace.Entries.Should().ContainSingle();
        trace.Entries[0].SecOcBadge.Kind.Should().Be(SecOcBadgeKind.Accepted);
        trace.Entries[0].SecOcBadge.Text.Should().Be("✓");
    }

    /// <summary>router sink → TraceViewModel.AppendBatchCore 桥（生产对应 App 的 frame→trace 管线）。</summary>
    private sealed class FrameCaptureSink : IFrameSink
    {
        private readonly TraceViewModel _trace;
        public FrameCaptureSink(TraceViewModel trace) => _trace = trace;
        public void OnFrame(CanFrame frame) => _trace.AppendBatchCore(new[] { frame });
        public void OnError(Exception ex) { }
    }
}
