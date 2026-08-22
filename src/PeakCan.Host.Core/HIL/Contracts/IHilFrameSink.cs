namespace PeakCan.HIL.Core.HIL.Contracts;

/// <summary>流式 CAN 帧记录器。Write 由 consumer 单线程调用；Dispose 后 Write 必须静默丢弃。
/// Write 内部不得向调用方抛异常（会杀 consumer loop）。</summary>
public interface IHilFrameSink : IDisposable
{
    void Write(CanFrame frame);
}

/// <summary>按 case 创建帧 sink。工厂由 HilRunnerService 一次性构造，跨 case 复用。</summary>
public interface IHilFrameSinkFactory
{
    /// <summary>为指定 case 创建 sink；返回 null = 该 case 不记录（预留 case 级跳过）。</summary>
    IHilFrameSink? Create(string caseName, int caseIndex);
}

/// <summary>IAssertionContext 的可选扩展：挂载/摘除帧 sink。</summary>
public interface IHasFrameSink
{
    void SetFrameSink(IHilFrameSink? sink);

    /// <summary>按逻辑名挂载/摘除帧 sink（channelName null/空 = 默认/唯一通道）。</summary>
    void SetFrameSink(string? channelName, IHilFrameSink? sink)
        => SetFrameSink(sink);

    /// <summary>有界等待 consumer 排空在途帧（channel 积压）。引擎线程在 case 结束、detach 之前调用；
    /// 500ms 上限或 ct 取消时直接返回（放弃排空，残余帧丢弃但文件仍合法）。</summary>
    Task WaitForFrameDrainAsync(CancellationToken ct = default);
}
