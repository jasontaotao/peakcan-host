using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Xcp.Receive;

/// <summary>
/// S2-T15 XCP 采集 sink（spec 决策 D4 + spec §3 Receive 空窗归因）：
/// <see cref="OnValues"/> 承接解码样本、<see cref="OnGap"/> 承接空窗/断流归因。
/// <para>
/// 队列纪律（照抄 IFrameSink 先例，src/PeakCan.Host.Infrastructure/Channel/IFrameSink.cs）：
/// 同步入队方法签名，实现 MUST NOT block —— 接收分发线程（XcpReceiveLoop）与计划空窗
/// 状态机线程直接调用，重活（IO / 批量落盘 / 聚合落盘）必须由实现自行入队 + 后台消费。
/// </para>
/// <para>
/// M-2：两条出站口的异常去向不同，实现者须知——
/// <see cref="OnValues"/> 抛异常：被 XcpReceiveLoop 转为 CallbackFailed 归因出站
/// （T14-review 钉死的回调契约，不回退）——异常有归因去向，但样本本身丢失；
/// <see cref="OnGap"/> 抛异常：本层之上再无归因出口，只得放弃该条归因
/// （T14-review Attributed 残余语义如实延续）——<b>静默丢弃、无归因去向</b>，
/// 仅爆炸半径限该条。实现不应依赖任何一侧的兜底。
/// </para>
/// </summary>
public interface IXcpAcquisitionSink
{
    /// <summary>解码样本入队（接收分发线程调用；不阻塞；异常 → CallbackFailed 归因）。</summary>
    void OnValues(XcpDaqSample sample);

    /// <summary>空窗/断流归因入队（接收分发线程/超时回调线程/轮转线程调用；不阻塞；异常 → 该条归因静默丢弃）。</summary>
    void OnGap(XcpAcquisitionGap gap);
}

/// <summary>空窗/断流归因种类。</summary>
public enum XcpAcquisitionGapKind
{
    /// <summary>包侧 MissingCause 并入（五值全通过，spec 定为并入）。</summary>
    MissingCauseAttributed,

    /// <summary>
    /// 接收层逐帧归因直通（MalformedFrame/UnknownPid/UnmappedDto/无 cause 的
    /// DecodeFailed/CallbackFailed/LocalFrameDrop——本机 DTO DropOldest 丢帧归因）。
    /// </summary>
    ReceiveAttribution,

    /// <summary>计划内换表空窗开窗（Receive 层新增类型，携带预期时长上界）。</summary>
    PlanGapOpened,

    /// <summary>计划空窗超时未恢复 → 断流升级（MissingCause.AcquisitionInterrupted 槽位的生产者，spec 写死）。</summary>
    AcquisitionInterrupted,
}

/// <summary>
/// 空窗/断流归因条目。
/// <para>
/// T14-review 前瞻观察落纸（sink 聚合的边界条件）与 S2-audit-fix1 修订：
/// XcpCanTransport DTO DropOldest 丢帧现已有本地归因（<see cref="XcpReceiveAttributionKind.LocalFrameDrop"/>，
/// 搭载其后首个被派发的帧），但最后一轮丢帧后若无后续帧，归因无从搭载、
/// 空窗升级断流仍只能靠 PlanGapWindow 的时长上界兜底 ——
/// sink 聚合<b>仍不得假设每个空窗必有逐帧归因事件</b>。
/// </para>
/// </summary>
/// <param name="Kind">归因种类。</param>
/// <param name="Detail">人类可读归因细节。</param>
/// <param name="Cause">包侧 MissingCause（MissingCauseAttributed / AcquisitionInterrupted 时非 null；不改包枚举）。</param>
/// <param name="ExpectedMaxDuration">计划空窗的预期时长上界（仅 PlanGapOpened 携带，来源 T11 RotationGapWindow）。</param>
/// <param name="ReceiveKind">触发本条的 XcpReceiveLoop 帧级归因种类（仅 FromAttribution 两条路径携带；M-3 帧级 Kind 不并入 Detail 糊掉）。</param>
public sealed record XcpAcquisitionGap(
    XcpAcquisitionGapKind Kind,
    string Detail,
    A2lEditor.Core.Layout.MissingCause? Cause = null,
    TimeSpan? ExpectedMaxDuration = null,
    XcpReceiveAttributionKind? ReceiveKind = null)
{
    /// <summary>T14 逐帧归因 → 空窗通道的并入转换（包侧五值全通过；帧级 Kind 保留在 ReceiveKind）。</summary>
    public static XcpAcquisitionGap FromAttribution(XcpReceiveAttribution attribution) =>
        attribution.MissingCause is { } cause
            ? new XcpAcquisitionGap(XcpAcquisitionGapKind.MissingCauseAttributed, attribution.Detail, cause,
                ReceiveKind: attribution.Kind)
            : new XcpAcquisitionGap(XcpAcquisitionGapKind.ReceiveAttribution, attribution.Detail,
                ReceiveKind: attribution.Kind);

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

    /// <summary>归因出站接线：逐帧归因并入空窗通道（包侧 MissingCause 五值全通过，帧级 Kind 保留）。</summary>
    public static Action<XcpReceiveAttribution> Attributed(IXcpAcquisitionSink sink) =>
        attribution => sink.OnGap(XcpAcquisitionGap.FromAttribution(attribution));
}