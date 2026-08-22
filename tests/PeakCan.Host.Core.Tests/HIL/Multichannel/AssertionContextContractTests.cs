// This test file uses direct-call compile-time assertions to verify that
// DIM overloads exist on the 3 host interfaces. The static local functions
// call the overloads but don't await ValueTask results (that's intentional --
// we're proving they compile, not testing runtime behavior).
#pragma warning disable CA2012

using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL.Multichannel;

/// <summary>
/// Contract verification: multi-channel overloads exist on the 3 host interfaces.
/// Uses direct-call compile-time assertions (stronger than reflection for DIM methods).
///
/// Design: each test uses a static local function that the compiler must resolve.
/// If the overloads are missing, the file fails to compile (RED).
/// At runtime, we call the function with a stub to avoid NRE.
/// </summary>
public sealed class AssertionContextContractTests
{
    [Fact]
    public void IAssertionContext_Has_MultiChannel_Overloads()
    {
        // Compile-time proof: these calls must resolve at compile time.
        // If overloads are missing, this file fails to compile (RED).
        static void VerifyOverloadsExist(IAssertionContext ctx, CanFrame f, CancellationToken ct, Action<DecodedFrame> cb)
        {
            ctx.SendFrameAsync("ch", f, ct);
            ctx.SubscribeDecodedFrames("ch", cb);
            ctx.GetRecentDecodedFrames("ch");
        }

        // Run with a stub to verify no runtime exception:
        var stub = new StubAssertionContext();
        VerifyOverloadsExist(stub, default, default, _ => { });
    }

    [Fact]
    public void IFrameStatistics_Has_ChannelName_Param()
    {
        // Compile-time proof: 4-param overloads must resolve.
        static void VerifyOverloadsExist(IFrameStatistics stats, CanId id)
        {
            stats.CountSince(id, 0, 1000, "ch1");
            stats.GetIntervalStats(id, 0, 1000, "ch1");
            // 3-param (no channelName) must still compile (backward compat):
            stats.CountSince(id, 0, 1000);
            stats.GetIntervalStats(id, 0, 1000);
        }

        var stub = new StubFrameStatistics();
        VerifyOverloadsExist(stub, new CanId(0x123, FrameFormat.Standard));
    }

    [Fact]
    public void IHasFrameSink_Has_ChannelName_Overload()
    {
        // Compile-time proof: 2-param overload must resolve.
        static void VerifyOverloadsExist(IHasFrameSink sink)
        {
            sink.SetFrameSink("ch1", null);
            // 1-param overload must still compile (backward compat):
            sink.SetFrameSink(null);
        }

        var stub = new StubHasFrameSink();
        VerifyOverloadsExist(stub);
    }

    // ── Stubs for runtime verification ──

    private sealed class StubAssertionContext : IAssertionContext
    {
        public double CurrentTimestamp => 0;
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames() => Array.Empty<DecodedFrame>();
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => null;
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct) => new(Result<Unit>.Ok(default));
        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => new StubDisposable();
    }

    private sealed class StubFrameStatistics : IFrameStatistics
    {
        public long Now => 0;
        public int CountSince(CanId id, long since, long now) => 0;
        public FrameIntervalStats GetIntervalStats(CanId id, long since, long now) => new(0, 0, 0, 0, 0);
    }

    private sealed class StubHasFrameSink : IHasFrameSink
    {
        public void SetFrameSink(IHilFrameSink? sink) { }
        public Task WaitForFrameDrainAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubDisposable : IDisposable
    {
        public void Dispose() { }
    }
}