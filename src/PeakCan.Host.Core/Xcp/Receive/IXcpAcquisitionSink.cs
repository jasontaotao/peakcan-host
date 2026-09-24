using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Xcp.Receive;

/// <summary>
/// S2-T15 XCP 采集 sink（spec 决策 D4 + spec §3 Receive 空窗归因）：
/// <see cref="OnValues"/> 承接解码样本、<see cref="OnGap"/> 承接空窗/断流归因。
/// <para>
/// 队列纪律（照抄 IFrameSink 先例，src/PeakCan.Host.Infrastructure/Channel/IFrameSink.cs）：
/// 同步入队方法签名，实现 MUST NOT block —— 接收分发线程（XcpReceiveLoop）直接调用，
/// 重活（IO / 批量落盘 / 聚合落盘）必须由实现自行入队 + 后台消费；
/// 实现 MUST NOT throw —— 抛出的异常会被 XcpReceiveLoop 转为 CallbackFailed 归因
/// （T14-review 钉死的回调契约，不回退），但那会烧掉一条归因路径，实现不应依赖该兜底。
/// </para>
/// </summary>
public interface IXcpAcquisitionSink
{
    /// <summary>解码样本入队（接收分发线程调用；不阻塞、不抛）。</summary>
    void OnValues(XcpDaqSample sample);

    /// <summary>空窗/断流归因入队（接收分发线程或计划空窗状态机调用；不阻塞、不抛）。</summary>
    void OnGap(XcpAcquisitionGap gap);
}

/// <summary>空窗/断流归因种类。</summary>
public enum XcpAcquisitionGapKind
{
    /// <summary>包侧 MissingCause 并入（五值全通过，spec 定为并入）。</summary>
    MissingCauseAttributed,

    /// <summary>接收层逐帧归因直通（MalformedFrame/UnknownPid/UnmappedDto/无 cause 的 DecodeFailed/CallbackFailed）。</summary>
    ReceiveAttribution,

    /// <summary>计划内换表空窗开窗（Receive 层新增类型，携带预期时长上界）。</summary>
    PlanGapOpened,

    /// <summary>计划空窗超时未恢复 → 断流升级（MissingCause.AcquisitionInterrupted 槽位的生产者，spec 写死）。</summary>
    AcquisitionInterrupted,
}

/// <summary>
/// 空窗/断流归因条目。
/// <para>
/// T14-review 前瞻观察落纸（sink 聚合的边界条件）：XcpCanTransport DTO DropOldest
/// 丢帧无本地归因，空窗升级断流只能靠 PlanGapWindow 的时长上界兜底 ——
/// sink 聚合<b>不得假设每个空窗必有逐帧归因事件</b>。
/// </para>
/// </summary>
/// <param name="Kind">归因种类。</param>
/// <param name="Detail">人类可读归因细节。</param>
/// <param name="Cause">包侧 MissingCause（MissingCauseAttributed / AcquisitionInterrupted 时非 null；不改包枚举）。</param>
/// <param name="ExpectedMaxDuration">计划空窗的预期时长上界（仅 PlanGapOpened 携带，来源 T11 RotationGapWindow）。</param>
public sealed record XcpAcquisitionGap(
    XcpAcquisitionGapKind Kind,
    string Detail,
    A2lEditor.Core.Layout.MissingCause? Cause = null,
    TimeSpan? ExpectedMaxDuration = null)
{
    /// <summary>T14 逐帧归因 → 空窗通道的并入转换（包侧五值全通过）。</summary>
    public static XcpAcquisitionGap FromAttribution(XcpReceiveAttribution attribution) =>
        attribution.MissingCause is { } cause
            ? new XcpAcquisitionGap(XcpAcquisitionGapKind.MissingCauseAttributed, attribution.Detail, cause)
            : new XcpAcquisitionGap(XcpAcquisitionGapKind.ReceiveAttribution,
                $"{attribution.Kind}: {attribution.Detail}");

    /// <summary>计划空窗开窗事件（携带 T11 ExpectedMaxDuration 上界）。</summary>
    public static XcpAcquisitionGap PlanGapOpened(PlanGapWindow window) =>
        new(XcpAcquisitionGapKind.PlanGapOpened,
            $"plan gap window opened: odts {window.OdtCount}, entries {window.EntryCount}, " +
            $"expected max duration {window.ExpectedMaxDuration.TotalMilliseconds:F0} ms.",
            ExpectedMaxDuration: window.ExpectedMaxDuration);

    /// <summary>
    /// 断流归因：MissingCause.AcquisitionInterrupted 包枚举槽位首次有生产者
    /// （spec 写死条款：由 Receive 层生产，不改包枚举）。
    /// </summary>
    public static XcpAcquisitionGap AcquisitionInterrupted(string detail) =>
        new(XcpAcquisitionGapKind.AcquisitionInterrupted, detail,
            A2lEditor.Core.Layout.MissingCause.AcquisitionInterrupted);
}

/// <summary>
/// T14 回调出站口 → sink 的组合接线（保持 T14 回调契约：XcpReceiveOptions 的
/// 委托形状原样不动，只是委托体改为转发）。返回的委托可直接填入
/// XcpReceiveOptions.SampleDecoded / Attributed。
/// </summary>
public static class XcpAcquisitionSinkWiring
{
    /// <summary>
    /// 样本出站接线：先关计划空窗再入队 —— 恢复样本本身不算空窗内数据，
    /// 窗口必须先于样本闭合。
    /// </summary>
    public static Action<XcpDaqSample> SampleDecoded(IXcpAcquisitionSink sink, PlanGapWatcher? gapWatcher = null) =>
        sample =>
        {
            gapWatcher?.OnSampleReceived(sample);
            sink.OnValues(sample);
        };

    /// <summary>归因出站接线：逐帧归因并入空窗通道（包侧 MissingCause 五值全通过）。</summary>
    public static Action<XcpReceiveAttribution> Attributed(IXcpAcquisitionSink sink) =>
        attribution => sink.OnGap(XcpAcquisitionGap.FromAttribution(attribution));
}