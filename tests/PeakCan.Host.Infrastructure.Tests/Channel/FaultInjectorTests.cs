using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.Infrastructure.Tests.Channel;

public class FaultInjectorTests
{
    private static readonly int[] s_corruptIndex0 = { 0 };

    private static FaultInjector CreateInjector(out VirtualChannel inner)
    {
        inner = new VirtualChannel();
        return new FaultInjector(inner);
    }

    [Fact]
    public async Task WriteAsync_passes_through_when_no_faults()
    {
        var injector = CreateInjector(out var inner);
        var received = new List<CanFrame>();
        inner.FrameReceived += f => received.Add(f);

        await inner.ConnectAsync(BaudRate.Can500kbps, false);
        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard), new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }), FrameFlags.None, ChannelId.None, new Timestamp(0));

        var result = await injector.WriteAsync(frame);
        Assert.True(result.IsSuccess);

        // Give consumer time to process
        await Task.Delay(100);
        Assert.Single(received);
        Assert.Equal(0x123u, received[0].Id.Raw);

        await injector.DisposeAsync();
    }

    [Fact]
    public async Task Drop_fault_drops_frame()
    {
        var injector = CreateInjector(out var inner);
        var received = new List<CanFrame>();
        inner.FrameReceived += f => received.Add(f);

        await inner.ConnectAsync(BaudRate.Can500kbps, false);
        injector.AddFault(new FaultRule { Type = FaultType.Drop, TargetCanId = 0x123 });

        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard), new ReadOnlyMemory<byte>(new byte[] { 1 }), FrameFlags.None, ChannelId.None, new Timestamp(0));
        await injector.WriteAsync(frame);

        await Task.Delay(100);
        Assert.Empty(received);

        await injector.DisposeAsync();
    }

    [Fact]
    public async Task Corrupt_fault_modifies_data()
    {
        var injector = CreateInjector(out var inner);
        var received = new List<CanFrame>();
        inner.FrameReceived += f => received.Add(f);

        await inner.ConnectAsync(BaudRate.Can500kbps, false);
        injector.AddFault(new FaultRule
        {
            Type = FaultType.Corrupt,
            TargetCanId = 0x123,
            CorruptByteIndices = s_corruptIndex0,
            CorruptXorMask = 0xFF
        });

        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard), new ReadOnlyMemory<byte>(new byte[] { 0xAA }), FrameFlags.None, ChannelId.None, new Timestamp(0));
        await injector.WriteAsync(frame);

        await Task.Delay(100);
        Assert.Single(received);
        Assert.Equal(0x55, received[0].Data.ToArray()[0]); // 0xAA ^ 0xFF

        await injector.DisposeAsync();
    }

    [Fact]
    public async Task Duplicate_fault_sends_two_frames()
    {
        var injector = CreateInjector(out var inner);
        var received = new List<CanFrame>();
        inner.FrameReceived += f => received.Add(f);

        await inner.ConnectAsync(BaudRate.Can500kbps, false);
        injector.AddFault(new FaultRule { Type = FaultType.Duplicate, TargetCanId = 0x123 });

        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard), new ReadOnlyMemory<byte>(new byte[] { 5 }), FrameFlags.None, ChannelId.None, new Timestamp(0));
        await injector.WriteAsync(frame);

        await Task.Delay(100);
        Assert.Equal(2, received.Count);

        await injector.DisposeAsync();
    }

    [Fact]
    public async Task Delay_fault_adds_latency()
    {
        var injector = CreateInjector(out var inner);
        var received = new List<CanFrame>();
        inner.FrameReceived += f => received.Add(f);

        await inner.ConnectAsync(BaudRate.Can500kbps, false);
        injector.AddFault(new FaultRule { Type = FaultType.Delay, TargetCanId = 0x123, DelayMs = 100 });

        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard), new ReadOnlyMemory<byte>(new byte[] { 1 }), FrameFlags.None, ChannelId.None, new Timestamp(0));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await injector.WriteAsync(frame);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 90, $"Expected >= 100ms, got {sw.ElapsedMilliseconds}ms");

        await Task.Delay(100);
        Assert.Single(received);

        await injector.DisposeAsync();
    }

    [Fact]
    public async Task Id_and_ReadLoopError_transparent()
    {
        var injector = CreateInjector(out var inner);

        // Id transparent
        Assert.Equal(inner.Id, injector.Id);

        // ReadLoopError add/remove should not throw
        var handler = new Action<ReadLoopError>(_ => { });
        injector.ReadLoopError += handler;
        injector.ReadLoopError -= handler;

        await injector.DisposeAsync();
    }
}
