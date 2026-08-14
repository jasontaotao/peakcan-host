using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Core.Tests.HIL;

public class HilFrameSinkContractTests
{
    private sealed class RecordingSink : IHilFrameSink
    {
        public int Written { get; private set; }
        public void Write(CanFrame frame) => Written++;
        public void Dispose() { }
    }

    private sealed class SpyHasSink : IHasFrameSink
    {
        public IHilFrameSink? Sink { get; private set; }
        public int DrainCalls { get; private set; }
        public void SetFrameSink(IHilFrameSink? sink) => Sink = sink;
        public Task WaitForFrameDrainAsync(CancellationToken ct = default) { DrainCalls++; return Task.CompletedTask; }
    }

    [Fact]
    public void Sink_Write_RecordsFrame()
    {
        using var sink = new RecordingSink();
        sink.Write(new CanFrame(new CanId(1, FrameFormat.Standard), ReadOnlyMemory<byte>.Empty,
            FrameFlags.None, ChannelId.None, new Timestamp(0)));
        Assert.Equal(1, sink.Written);
    }

    [Fact]
    public async Task HasSink_SetAndDrain_Work()
    {
        var spy = new SpyHasSink();
        using var sink = new RecordingSink();
        spy.SetFrameSink(sink);
        Assert.Same(sink, spy.Sink);
        await spy.WaitForFrameDrainAsync();
        Assert.Equal(1, spy.DrainCalls);
    }

    [Fact]
    public void Factory_Create_ReturnsSinkOrNull()
    {
        IHilFrameSinkFactory factory = new StubFactory();
        using var s = factory.Create("case", 0);
        Assert.NotNull(s);
        Assert.Null(new NullFactory().Create("case", 0));
    }

    private sealed class StubFactory : IHilFrameSinkFactory
    {
        public IHilFrameSink? Create(string caseName, int caseIndex) => new RecordingSink();
    }
    private sealed class NullFactory : IHilFrameSinkFactory
    {
        public IHilFrameSink? Create(string caseName, int caseIndex) => null;
    }
}
