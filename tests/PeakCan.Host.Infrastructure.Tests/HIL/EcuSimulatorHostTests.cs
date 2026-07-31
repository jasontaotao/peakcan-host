using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.Uds.IsoTp;
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
        var channel = new FakeCanChannel();
        var sm = new EcuStateMachine(new[]
        {
            new EcuStateTransition
            {
                FromState = "default",
                ServiceId = 0x27,
                SubFunction = 0x01,
                Response = new StaticResponse(new byte[] { 0x67, 0x01, 0x11, 0x22, 0x33, 0x44 }),
                ToState = "seedSent",
            },
            new EcuStateTransition
            {
                FromState = "seedSent",
                ServiceId = 0x27,
                SubFunction = 0x02,
                Response = new StaticResponse(new byte[] { 0x67, 0x02 }),
                ToState = "unlocked",
            },
        });
        var host = new EcuSimulatorHost(channel, CreateEcuCanIds(), sm);

        using var cts = new CancellationTokenSource();
        var runTask = host.RunAsync(cts.Token);
        await Task.Delay(50);

        // Step 1: request seed
        var seedResponse = await SendRequestAndReceiveResponse(channel, new byte[] { 0x27, 0x01 });
        Assert.Contains((byte)0x67, seedResponse.Data.ToArray());
        Assert.Contains((byte)0x11, seedResponse.Data.ToArray());

        // Step 2: send key
        var keyResponse = await SendRequestAndReceiveResponse(channel, new byte[] { 0x27, 0x02, 0x11, 0x22, 0x33, 0x44 });
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
