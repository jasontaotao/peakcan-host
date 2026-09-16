using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.SecOc;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using PeakCan.Host.Core;
using PeakCan.Security.SecOc;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

public class ChannelConnectionCoordinatorSecOcTests
{
    private static readonly uint[] ProtectedSingle = { 0x123u };

    // 最小 fake：连接成功、Write 记录、可手动 Emit 帧（模式同 tests/PeakCan.Host.App.Tests/Windows/AppShellLayoutPersistenceTests.cs:133）。
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
                Key = new byte[16],
                Mode = SecOcPduMode.Both,
            },
        };

    private static ConnectionConfig Cfg(ushort handle = 0x51) => new(
        new ChannelInfo(handle, $"PCAN_USBBUS{handle - 0x50}"),
        BaudRate.CanFd1Mbps, IsFd: true);

    [Fact]
    public async Task Connect_WithSecOcPdus_WrapsChannel_AsISecureChannel()
    {
        var raw = new FakeCanChannel(new ChannelId(0x51));
        var factory = Substitute.For<IChannelFactory>();
        factory.Create(Arg.Any<ChannelId>()).Returns(raw);
        var coordinator = new ChannelConnectionCoordinator(
            factory, new ChannelRouter(NullLogger<ChannelRouter>.Instance),
            new SendService(NullLogger<SendService>.Instance),
            secOcVerdicts: new SecOcVerdictTable(),
            secOcPduProvider: OnePdu);

        var result = await coordinator.ConnectAllAsync(new[] { Cfg() });

        result.ConnectedCount.Should().Be(1);
        var connected = coordinator.Connections.Single().Channel;
        connected.Should().BeAssignableTo<ISecureChannel>();
        connected.IsConnected.Should().BeTrue(); // wrap 后 connect 透传 inner
    }

    [Fact]
    public async Task Connect_WithoutSecOcPdus_KeepsRawChannel_ZeroRegression()
    {
        var raw = new FakeCanChannel(new ChannelId(0x51));
        var factory = Substitute.For<IChannelFactory>();
        factory.Create(Arg.Any<ChannelId>()).Returns(raw);
        var coordinator = new ChannelConnectionCoordinator(
            factory, new ChannelRouter(NullLogger<ChannelRouter>.Instance),
            new SendService(NullLogger<SendService>.Instance));

        await coordinator.ConnectAllAsync(new[] { Cfg() });

        coordinator.Connections.Single().Channel.Should().BeSameAs(raw);
    }

    [Fact]
    public async Task Connect_SendServiceTx_SignsProtectedFrame()
    {
        // 真 SecOcChannel 签名：WriteAsync 产出 frame = data‖TruncFV‖TruncMAC（16/8+24/8=5 字节附加）。
        var raw = new FakeCanChannel(new ChannelId(0x51));
        var factory = Substitute.For<IChannelFactory>();
        factory.Create(Arg.Any<ChannelId>()).Returns(raw);
        var coordinator = new ChannelConnectionCoordinator(
            factory, new ChannelRouter(NullLogger<ChannelRouter>.Instance),
            new SendService(NullLogger<SendService>.Instance),
            secOcVerdicts: new SecOcVerdictTable(),
            secOcPduProvider: OnePdu);
        await coordinator.ConnectAllAsync(new[] { Cfg() });

        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard), new byte[] { 0xAA, 0xBB },
            FrameFlags.None, new ChannelId(0x51), Timestamp.FromMicroseconds(1UL));
        await coordinator.SendService.ActiveChannel!.WriteAsync(frame);

        raw.Written.Should().ContainSingle();
        raw.Written[0].Data.Length.Should().Be(2 + 2 + 3); // 2 data + 16bit FV + 24bit MAC
    }

    [Fact]
    public async Task Connect_RxForgedMac_RecordsVerdict_JoinerShowsRejected()
    {
        var raw = new FakeCanChannel(new ChannelId(0x51));
        var factory = Substitute.For<IChannelFactory>();
        factory.Create(Arg.Any<ChannelId>()).Returns(raw);
        var verdicts = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(verdicts);
        var coordinator = new ChannelConnectionCoordinator(
            factory, new ChannelRouter(NullLogger<ChannelRouter>.Instance),
            new SendService(NullLogger<SendService>.Instance),
            secOcVerdicts: verdicts,
            secOcPduProvider: OnePdu,
            secOcBadgeJoiner: joiner);
        await coordinator.ConnectAllAsync(new[] { Cfg() });

        // 伪造帧：数据 + 假 FV + 假 MAC（全 0）→ SecOcChannel 验签拒绝并记录。
        raw.Emit(new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0xAA, 0xBB, 0x00, 0x00, 0x00, 0x00, 0x00 },
            FrameFlags.None, new ChannelId(0x51), Timestamp.FromMicroseconds(1UL)));

        joiner.Join(new CanFrame(new CanId(0x123, FrameFormat.Standard), new byte[] { 1 },
            FrameFlags.None, new ChannelId(0x51), Timestamp.FromMicroseconds(1UL)))
            .Kind.Should().Be(PeakCan.Host.App.ViewModels.SecOcBadgeKind.Rejected);
    }

    [Fact]
    public async Task Connect_ProviderThrows_NoChannelsConnected_FailLoud()
    {
        // keyId 缺失等配置错误 → provider 抛 → ConnectAllAsync 直接上抛（VM 层显示错误）。
        var raw = new FakeCanChannel(new ChannelId(0x51));
        var factory = Substitute.For<IChannelFactory>();
        factory.Create(Arg.Any<ChannelId>()).Returns(raw);
        var coordinator = new ChannelConnectionCoordinator(
            factory, new ChannelRouter(NullLogger<ChannelRouter>.Instance),
            new SendService(NullLogger<SendService>.Instance),
            secOcPduProvider: () => throw new InvalidOperationException("keyId 'k1' not found"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ConnectAllAsync(new[] { Cfg() }));
        coordinator.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task DisconnectAll_ResetsJoiner()
    {
        var verdicts = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(verdicts);
        joiner.Configure(0x51, ProtectedSingle);
        var coordinator = new ChannelConnectionCoordinator(
            Substitute.For<IChannelFactory>(), new ChannelRouter(NullLogger<ChannelRouter>.Instance),
            new SendService(NullLogger<SendService>.Instance),
            secOcVerdicts: verdicts, secOcBadgeJoiner: joiner);

        await coordinator.DisconnectAllAsync();

        joiner.Join(new CanFrame(new CanId(0x123, FrameFormat.Standard), new byte[] { 1 },
            FrameFlags.None, new ChannelId(0x51), Timestamp.FromMicroseconds(1UL)))
            .Should().Be(PeakCan.Host.App.ViewModels.SecOcBadge.Offline);
    }
}