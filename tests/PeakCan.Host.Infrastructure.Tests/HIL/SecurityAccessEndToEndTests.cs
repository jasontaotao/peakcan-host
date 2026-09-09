using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.HIL;
using FakeTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

/// <summary>
/// M3.3（spec §8 Phase 3）：host UdsClient（含 lockout）↔ StatefulVirtualEcu
/// 全状态机端到端。双侧共享同一 <see cref="FakeTimeProvider"/>（spec D5），
/// 锁定窗口由虚拟推进跨越——零真实等待。
/// </summary>
public class SecurityAccessEndToEndTests
{
    private const uint ReqId = 0x7E0;
    private const uint RespId = 0x7E1;

    private sealed record Rig(FakeTimeProvider Clock, VirtualChannel Channel,
        UdsClient Client, StatefulVirtualEcu Ecu, SecurityAccessServer Server) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Ecu.Dispose();
        }
    }

    private static Rig NewRig(SecurityAccessServerConfig? serverCfg = null)
    {
        var clock = new FakeTimeProvider();
        var channel = new VirtualChannel();
        // ECU 视角：RequestId = ECU 发送（0x7E1），ResponseId = ECU 接收（0x7E0）
        var ecuCanIds = new CanIdConfig { RequestId = RespId, ResponseId = ReqId, IsExtendedFrame = false };
        var server = new SecurityAccessServer(serverCfg, timeProvider: clock);
        var ecu = new StatefulVirtualEcu(channel, ecuCanIds,
            new EcuStateMachine(new[]
            {
                // 脚本侧哑转移：0x10 0x01 应答 0x50 0x01（0x10 钩子同时触发 server 重锁副作用）
                new EcuStateTransition { FromState = null, ServiceId = 0x10, SubFunction = 0x01, Response = new StaticResponse(new byte[] { 0x50, 0x01, 0x00, 0x32, 0x00, 0xC8 }) },
                new EcuStateTransition { FromState = null, ServiceId = 0x3E, SubFunction = 0x00, Response = new StaticResponse(new byte[] { 0x7E, 0x00 }) },
            }),
            securityServer: server);
        var clientIsoTp = new global::PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer(
            new CanIdConfig { RequestId = ReqId, ResponseId = RespId, IsExtendedFrame = false },
            async frame => { await channel.WriteAsync(frame).ConfigureAwait(false); });
        channel.FrameReceived += clientIsoTp.ProcessFrame;
        var client = new UdsClient(clientIsoTp, new XorAAKeyAlgorithm(), timeProvider: clock);
        channel.ConnectAsync(BaudRate.Can500kbps, fd: false).GetAwaiter().GetResult();
        return new Rig(clock, channel, client, ecu, server);
    }

    private sealed class XorAAKeyAlgorithm : IKeyDerivationAlgorithm
    {
        public byte[] ComputeKey(byte[] seed, byte securityLevel)
            => seed.Select(b => (byte)(b ^ 0xAA)).ToArray();
    }

    [Fact]
    public async Task FullHandshake_RequestSeed_SendKey_Unlocks_Server()
    {
        using var rig = NewRig();
        // RequestSeed（仅取 seed，不触发 SendKey）
        var seed = await rig.Client.RequestSeedAsync(0x01, CancellationToken.None);
        Assert.Equal(4, seed.Length);
        Assert.False(rig.Server.IsAuthenticated(1));

        // SendKey（XOR 0xAA 与 server 端种子算法一致）
        var key = seed.Select(b => (byte)(b ^ 0xAA)).ToArray();
        await rig.Client.SecurityAccessAsync(0x01, key, CancellationToken.None);
        Assert.True(rig.Server.IsAuthenticated(1));
    }

    [Fact]
    public async Task Three_Wrong_Keys_Lock_Server_And_Client_Sees_Nrc35()
    {
        using var rig = NewRig();
        for (var i = 0; i < 3; i++)
        {
            await rig.Client.RequestSeedAsync(0x01, CancellationToken.None);
            var ex = await Assert.ThrowsAsync<UdsNegativeResponseException>(
                () => rig.Client.SecurityAccessAsync(0x01, new byte[] { 9, 9, 9, 9 }, CancellationToken.None));
            Assert.True((byte)ex.ResponseCode == 0x35, $"iter {i}: nrc=0x{(byte)ex.ResponseCode:X2} serverLocked={rig.Server.IsLocked(1)}");
        }
        Assert.True(rig.Server.IsLocked(1));
    }

    [Fact]
    public async Task Lockout_Flow_36_Then_37_With_Virtual_Time_Recovery()
    {
        using var rig = NewRig();
        // 3 wrong keys → server Delayed
        for (var i = 0; i < 3; i++)
        {
            await rig.Client.RequestSeedAsync(0x01, CancellationToken.None);
            await Assert.ThrowsAsync<UdsNegativeResponseException>(
                () => rig.Client.SecurityAccessAsync(0x01, new byte[] { 9, 9, 9, 9 }, CancellationToken.None));
        }

        // Delayed 内首次 requestSeed → 0x36（server 视角）；client 侧 host 强制
        // IsLocked 在线上之前就抛 UdsSecurityLockedException（语义对照表 §1）
        var clientEx = await Assert.ThrowsAsync<UdsSecurityLockedException>(
            () => rig.Client.SecurityAccessAsync(0x01, key: null, CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(5), clientEx.RemainingDelay);

        // 虚拟推进跨过锁定窗口（零真实等待）→ 恢复正常握手成功
        rig.Clock.Advance(TimeSpan.FromSeconds(5) + TimeSpan.FromMilliseconds(1));
        Assert.False(rig.Server.IsLocked(1));
        var seed = await rig.Client.RequestSeedAsync(0x01, CancellationToken.None);
        await rig.Client.SecurityAccessAsync(0x01, seed.Select(b => (byte)(b ^ 0xAA)).ToArray(), CancellationToken.None);
        Assert.True(rig.Server.IsAuthenticated(1));
    }

    [Fact]
    public async Task Always37_Behavior_End_To_End()
    {
        using var rig = NewRig(new SecurityAccessServerConfig
        {
            LockoutSeedBehavior = LockoutSeedBehavior.Always37,
        });
        for (var i = 0; i < 3; i++)
        {
            await rig.Client.RequestSeedAsync(0x01, CancellationToken.None);
            await Assert.ThrowsAsync<UdsNegativeResponseException>(
                () => rig.Client.SecurityAccessAsync(0x01, new byte[] { 9, 9, 9, 9 }, CancellationToken.None));
        }
        // client host 强制在进入线上前抛出锁定——绕过 host 强制直发 requestSeed
        // 请求（模拟非配合 tester）需要 server 直接喂请求：Delayed 内 requestSeed → 0x37
        var rsp = rig.Server.HandleRequest(new byte[] { 0x27, 0x01 });
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x37 }, rsp);
    }

    [Fact]
    public async Task Session_Change_To_Default_Relocks_E2E()
    {
        using var rig = NewRig();
        var seed = await rig.Client.RequestSeedAsync(0x01, CancellationToken.None);
        await rig.Client.SecurityAccessAsync(0x01, seed.Select(b => (byte)(b ^ 0xAA)).ToArray(), CancellationToken.None);
        Assert.True(rig.Server.IsAuthenticated(1));

        // 0x10 0x01 → default session → server relocks
        await rig.Client.DiagnosticSessionControlAsync(0x01, CancellationToken.None);
        Assert.False(rig.Server.IsAuthenticated(1));

        // 再握手成功（新 seed，非全零）
        var seed2 = await rig.Client.RequestSeedAsync(0x01, CancellationToken.None);
        Assert.Equal(4, seed2.Length);
        Assert.False(seed2.All(b => b == 0));
        await rig.Client.SecurityAccessAsync(0x01, seed2.Select(b => (byte)(b ^ 0xAA)).ToArray(), CancellationToken.None);
        Assert.True(rig.Server.IsAuthenticated(1));
    }
}
