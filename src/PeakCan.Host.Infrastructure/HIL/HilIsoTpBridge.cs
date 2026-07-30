using PeakCan.Host.Core;
using PeakCan.Host.Core.Uds.IsoTp;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Subscribes to ICanChannel.FrameReceived and forwards frames to IsoTpLayer.ProcessFrame.
/// Required because IsoTpLayer does NOT auto-subscribe to ICanChannel.FrameReceived.
/// </summary>
internal sealed class HilIsoTpBridge : IDisposable
{
    private readonly IsoTpLayer _isoTp;
    private readonly IDisposable _subscription;

    public HilIsoTpBridge(ICanChannel channel, IsoTpLayer isoTp)
    {
        _isoTp = isoTp;
        _subscription = new FrameReceivedSubscription(channel, OnFrame);
    }

    private void OnFrame(CanFrame frame)
    {
        try { _isoTp.ProcessFrame(frame); }
        catch (ArgumentException)
        {
            // ProcessFrame 对畸形帧抛 ArgumentException — 吞掉避免破坏接收路径
            // （与 IsoTpSinkAdapter.OnFrame 一致的防御策略）
            // ⚠️ 不声明 ex 变量：TreatWarningsAsErrors=true 下 CS0168 会变成编译错误
        }
    }

    public void Dispose() => _subscription.Dispose();
}
