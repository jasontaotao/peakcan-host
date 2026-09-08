using PeakCan.HIL.Core.HIL;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.Cli;
using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class EcuSimulatorHostTests
{
    private static CanIdConfig CreateEcuCanIds() => new()
    {
        RequestId = 0x7E8,  // ECU sends responses on 0x7E8
        ResponseId = 0x7E0  // ECU receives requests on 0x7E0
    };

    private static EcuStateMachine CreateSimpleStateMachine() => new(new[]
    {
        new EcuStateTransition
        {
            FromState = null,
            ServiceId = 0x3E,
            SubFunction = 0x00,
            Response = new StaticResponse(new byte[] { 0x7E }),
            ToState = null,
        }
    });

    private static async Task<CanFrame> SendRequestAndReceiveResponse(
        FakeCanChannel channel, byte[] udsPayload, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<CanFrame>();
        void Handler(CanFrame f)
        {
            if (f.Id.Raw == 0x7E8) tcs.TrySetResult(f);
        }
        channel.FrameReceived += Handler;

        try
        {
            // ISO-TP single-frame PCI: first nibble = 0 (SF), second nibble = length
            var pci = (byte)(udsPayload.Length & 0x0F);
            var frameData = new byte[1 + udsPayload.Length];
            frameData[0] = pci;
            udsPayload.CopyTo(frameData, 1);

            var requestFrame = new CanFrame(
                new CanId(0x7E0, FrameFormat.Standard),
                new ReadOnlyMemory<byte>(frameData),
                FrameFlags.None, ChannelId.None, new Timestamp(0));

            await channel.WriteAsync(requestFrame);

            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(2));
        }
        finally
        {
            channel.FrameReceived -= Handler;
        }
    }

    [Fact]
    public async Task Simulator_ConnectAndRun_StatefulVirtualEcuCreated()
    {
        var channel = new FakeCanChannel();
        var sm = CreateSimpleStateMachine();

        // Capture baseline BEFORE creating the ECU so the delta is deterministic.
        var countBefore = StatefulVirtualEcu.InstanceCount;
        var host = new EcuSimulatorHost(channel, CreateEcuCanIds(), sm);
        var countAfterCreate = StatefulVirtualEcu.InstanceCount;
        Assert.Equal(countBefore + 1, countAfterCreate);

        using var cts = new CancellationTokenSource();
        var runTask = host.RunAsync(cts.Token);
        // Give it a moment to connect.
        await Task.Delay(50);

        Assert.True(channel.IsConnected);

        cts.Cancel();
        await runTask;
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Simulator_Cancellation_DisconnectsChannel()
    {
        var channel = new FakeCanChannel();
        var sm = CreateSimpleStateMachine();
        var host = new EcuSimulatorHost(channel, CreateEcuCanIds(), sm);

        using var cts = new CancellationTokenSource();
        var runTask = host.RunAsync(cts.Token);
        await Task.Delay(50);

        Assert.True(channel.IsConnected);

        cts.Cancel();
        await runTask;

        Assert.False(channel.IsConnected);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Simulator_Dispose_ReleasesStatefulVirtualEcu()
    {
        var channel = new FakeCanChannel();
        var sm = CreateSimpleStateMachine();

        var countBefore = StatefulVirtualEcu.InstanceCount;
        var host = new EcuSimulatorHost(channel, CreateEcuCanIds(), sm);
        Assert.Equal(countBefore + 1, StatefulVirtualEcu.InstanceCount);

        await host.DisposeAsync();

        Assert.Equal(countBefore, StatefulVirtualEcu.InstanceCount);
    }

    [Fact]
    public void Simulator_CanIdConflict_PrintsWarning()
    {
        var channel = new FakeCanChannel();
        // Conflict: RequestId == ResponseId (ECU would send and receive on same ID).
        var conflictCanIds = new CanIdConfig { RequestId = 0x7E8, ResponseId = 0x7E8 };
        var sm = CreateSimpleStateMachine();

        var warnings = new List<string>();
        var logger = new TestLogger<StatefulVirtualEcu>(warnings);

        // Should not throw — just log a warning.
        var host = new EcuSimulatorHost(channel, conflictCanIds, sm, logger);

        Assert.Contains(warnings, w => w.Contains("conflict", StringComparison.OrdinalIgnoreCase));
        host.Dispose();
    }

    private static readonly string[] _simulateArgs = { "--dbc", "x.dbc", "--ecu", "ecu.json", "--hw", "USB1", "--simulate" };
    private static readonly string[] _simulateNoEcuArgs = { "--dbc", "x.dbc", "--hw", "USB1", "--simulate" };

    [Fact]
    public void CliArgs_SimulateFlag_ParsedCorrectly()
    {
        var cli = CliArgsParser.Parse(_simulateArgs);

        Assert.True(cli.Simulate);
        Assert.Equal("ecu.json", cli.EcuScriptPath);
        Assert.Equal("USB1", cli.HardwareChannel);
        Assert.Equal("x.dbc", cli.DbcPath);
    }

    [Fact]
    public void CliArgs_SimulateWithoutEcu_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CliArgsParser.Parse(_simulateNoEcuArgs));

        Assert.Contains("--ecu", ex.Message);
    }

    [Fact]
    public async Task Simulator_E2E_FakeChannelReceivesUdsRequest_EcuResponds()
    {
        var channel = new FakeCanChannel();
        var sm = CreateSimpleStateMachine();
        var host = new EcuSimulatorHost(channel, CreateEcuCanIds(), sm);

        using var cts = new CancellationTokenSource();
        var runTask = host.RunAsync(cts.Token);
        await Task.Delay(50);

        var response = await SendRequestAndReceiveResponse(channel, new byte[] { 0x3E, 0x00 });

        Assert.Equal(0x7E8u, response.Id.Raw);
        var data = response.Data.ToArray();
        Assert.Contains((byte)0x7E, data); // positive response SID for 0x3E

        cts.Cancel();
        await runTask;
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Simulator_E2E_SecurityAccess_FullFlow()
    {
        // M3.1（spec §6.3）: SID 0x27 由 host 侧 SecurityAccessServer 全状态机
        // 接管——动态 seed + 默认 XOR 0xAA key 算法；脚本 StaticResponse 不再
        // 是 0x27 响应来源（hil-core 冻结，host 侧权威）。
        var channel = new FakeCanChannel();
        var sm = new EcuStateMachine(Array.Empty<EcuStateTransition>());
        var host = new EcuSimulatorHost(channel, CreateEcuCanIds(), sm);

        using var cts = new CancellationTokenSource();
        var runTask = host.RunAsync(cts.Token);
        await Task.Delay(50);

        // Step 1: request seed (dynamic, 4 bytes)
        var seedResponse = await SendRequestAndReceiveResponse(channel, new byte[] { 0x27, 0x01 });
        var data = seedResponse.Data.ToArray();
        var seedStart = Array.IndexOf(data, (byte)0x67);
        Assert.True(seedStart >= 0, $"no positive seed response in {Convert.ToHexString(data)}");
        Assert.Equal(0x01, data[seedStart + 1]);
        var seed = data[(seedStart + 2)..(seedStart + 6)];
        Assert.Equal(4, seed.Length);

        // Step 2: send key = seed XOR 0xAA (server default algorithm)
        var key = seed.Select(b => (byte)(b ^ 0xAA)).ToArray();
        var keyRequest = new byte[2 + key.Length];
        keyRequest[0] = 0x27; keyRequest[1] = 0x02;
        key.CopyTo(keyRequest, 2);
        var keyResponse = await SendRequestAndReceiveResponse(channel, keyRequest);
        Assert.Contains((byte)0x67, keyResponse.Data.ToArray());

        cts.Cancel();
        await runTask;
        await host.DisposeAsync();
    }
}

/// <summary>Minimal ILogger that captures warning/error messages for assertions.</summary>
internal sealed class TestLogger<T> : ILogger<T>
{
    private readonly List<string> _messages;
    public TestLogger(List<string> messages) { _messages = messages; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
            _messages.Add(formatter(state, exception));
    }
}
