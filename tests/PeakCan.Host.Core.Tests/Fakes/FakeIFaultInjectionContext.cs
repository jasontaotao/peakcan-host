using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Core.Tests.Fakes;

/// <summary>
/// Fake implementation of IFaultInjectionContext + IAssertionContext for testing executors.
/// </summary>
public sealed class FakeIFaultInjectionContext : IAssertionContext, IFaultInjectionContext
{
    // IAssertionContext (minimal stub)
    public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => throw new NotImplementedException();
    public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => null;
    public double CurrentTimestamp => 0;
    public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct) => throw new NotImplementedException();

    // IFaultInjectionContext
    public List<FaultRule> AddedFaults { get; } = new();
    public Dictionary<string, IDisposable> TaggedFaults { get; } = new();
    public int ClearAllCallCount { get; private set; }
    public List<string> ClearedFaultIds { get; } = new();

    public IDisposable AddFault(FaultRule fault)
    {
        AddedFaults.Add(fault);
        return new FakeFaultHandle(() => AddedFaults.Remove(fault));
    }

    public void TagFault(string faultId, IDisposable handle)
        => TaggedFaults[faultId] = handle;

    public void ClearFaults(string? faultId = null)
    {
        if (faultId is null) ClearAllCallCount++;
        else ClearedFaultIds.Add(faultId);
    }

    private sealed class FakeFaultHandle : IDisposable
    {
        private readonly Action _remove;
        public FakeFaultHandle(Action remove) => _remove = remove;
        public void Dispose() => _remove();
    }
}
