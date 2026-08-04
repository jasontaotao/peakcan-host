using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.Infrastructure.Tests.Channel;

public class ReceivePathFaultInjectorTests
{
    private static readonly byte[] TestData3 = new byte[] { 0x01, 0x02, 0x03 };
    private static readonly byte[] TestData2 = new byte[] { 0xAA, 0xBB };
    private static readonly int[] SingleZeroIndex = new[] { 0 };

    private static CanFrame CreateFrame(uint canId = 0x100, byte[]? data = null)
        => new(new CanId(canId, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(data ?? TestData3),
            FrameFlags.None, ChannelId.None, new Timestamp(0));

    private static VirtualChannel GetInnerChannel(ReceivePathFaultInjector injector)
    {
        var field = injector.GetType().GetField("_inner",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (VirtualChannel)field!.GetValue(injector)!;
    }

    private static async Task<ReceivePathFaultInjector> CreateConnectedInjector()
    {
        var channel = new VirtualChannel();
        var injector = new ReceivePathFaultInjector(channel);
        await channel.ConnectAsync(BaudRate.Can500kbps, false);
        return injector;
    }

    [Fact]
    public async Task FrameReceived_PassesThrough_WhenNoFaults()
    {
        var injector = await CreateConnectedInjector();
        var tcs = new TaskCompletionSource<CanFrame>();
        injector.FrameReceived += f => tcs.TrySetResult(f);

        var inner = GetInnerChannel(injector);
        await inner.WriteAsync(CreateFrame(0x100));

        var frame = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0x100u, frame.Id.Raw);
    }

    [Fact]
    public async Task FrameReceived_DropsFrame_WhenDropFaultMatches()
    {
        var injector = await CreateConnectedInjector();
        var tcs = new TaskCompletionSource<CanFrame>();
        injector.FrameReceived += f => tcs.TrySetResult(f);

        injector.AddReceiveFault(new FaultRule
        {
            Type = FaultType.Drop,
            TargetCanId = 0x100,
            Probability = 1.0,
        });

        var inner = GetInnerChannel(injector);
        await inner.WriteAsync(CreateFrame(0x100));

        // Frame should be dropped — task should not complete
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task FrameReceived_CorruptsFrame_WhenCorruptFaultMatches()
    {
        var injector = await CreateConnectedInjector();
        var tcs = new TaskCompletionSource<CanFrame>();
        injector.FrameReceived += f => tcs.TrySetResult(f);

        injector.AddReceiveFault(new FaultRule
        {
            Type = FaultType.Corrupt,
            TargetCanId = 0x200,
            CorruptByteIndices = SingleZeroIndex,
            CorruptXorMask = 0xFF,
        });

        var inner = GetInnerChannel(injector);
        await inner.WriteAsync(CreateFrame(0x200, TestData2));

        var frame = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var data = frame.Data.ToArray();
        Assert.Equal(0x55, data[0]); // 0xAA ^ 0xFF = 0x55
        Assert.Equal(0xBB, data[1]);
    }

    [Fact]
    public async Task FrameReceived_DuplicatesFrame_WhenDuplicateFaultMatches()
    {
        var injector = await CreateConnectedInjector();
        int count = 0;
        injector.FrameReceived += _ => Interlocked.Increment(ref count);

        injector.AddReceiveFault(new FaultRule
        {
            Type = FaultType.Duplicate,
            TargetCanId = 0x300,
        });

        var inner = GetInnerChannel(injector);
        await inner.WriteAsync(CreateFrame(0x300));

        // Wait for both frames to be dispatched
        await Task.Delay(200);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task FrameReceived_DelaysFrame_WhenDelayFaultMatches()
    {
        var injector = await CreateConnectedInjector();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tcs = new TaskCompletionSource<CanFrame>();
        injector.FrameReceived += f => tcs.TrySetResult(f);

        injector.AddReceiveFault(new FaultRule
        {
            Type = FaultType.Delay,
            TargetCanId = 0x400,
            DelayMs = 100,
        });

        var inner = GetInnerChannel(injector);
        await inner.WriteAsync(CreateFrame(0x400));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds >= 90, $"Expected >= 90ms delay, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task FrameReceived_IsolatesSubscriberExceptions()
    {
        var injector = await CreateConnectedInjector();
        int callCount = 0;
        injector.FrameReceived += _ => throw new InvalidOperationException("subscriber A failed");
        injector.FrameReceived += _ => Interlocked.Increment(ref callCount);

        injector.AddReceiveFault(new FaultRule
        {
            Type = FaultType.Drop,
            TargetCanId = 0x500,
            Probability = 0.0, // never drop, just pass through
        });

        var inner = GetInnerChannel(injector);
        await inner.WriteAsync(CreateFrame(0x500));

        await Task.Delay(200);
        Assert.Equal(1, callCount); // subscriber B still received despite A throwing
    }

    [Fact]
    public async Task FrameReceived_DoubleRemove_DoesNotBreakSubscription()
    {
        var injector = await CreateConnectedInjector();
        int count = 0;

        void Handler(CanFrame _) => Interlocked.Increment(ref count);

        injector.FrameReceived += Handler;
        injector.FrameReceived -= Handler;
        injector.FrameReceived -= Handler; // double remove
        injector.FrameReceived += Handler; // re-add

        var inner = GetInnerChannel(injector);
        await inner.WriteAsync(CreateFrame(0x600));

        await Task.Delay(200);
        Assert.Equal(1, count); // handler still works after double-remove
    }
}
