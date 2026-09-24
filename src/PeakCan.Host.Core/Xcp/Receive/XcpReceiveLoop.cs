using System.Collections.Generic;
using A2lEditor.Core.Layout;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Xcp.Receive;

/// <summary>
/// S2-T14 接收线程（spec §3 Receive 写死条款）：<b>按 CAN 帧首字节分流</b>——
/// 正响应 PID 0xFF / 错误 PID 0xFE / DAQ DTO PID 0x00–0xFB，三流共用
/// CAN_ID_SLAVE（无 SET_DAQ_ID，<b>禁止按 CAN ID 分流</b>）。
/// <para>
/// 流向（T4 <see cref="XcpMaster"/> 现有模式定方式）：master 构造期自订阅
/// <see cref="IXcpTransport.FrameReceived"/> 做 pending 配对（0xFF 正响应原样上抛、
/// 0xFE 转 XcpErrorResponseException、EV/SERV 消费忽略，见 XcpMaster.OnFrameReceived）；
/// 本层对 0xFF/0xFE <b>让路不消费</b>（避免双份派发），只承接 DTO 流：
/// planner 自产映射反查（<see cref="PlannedAcquisitionMap.TryResolve"/> 语义的
/// PID→ODT 构造期索引）→ 拷字节 → 包侧 <see cref="ValueContract.Decode"/> → 结果回调出站。
/// </para>
/// <para>
/// 不可变与并发：反查索引与合同快照均在构造期一次建好、之后只读；
/// <see cref="ValueContract.Decode"/> 无结果缓存、可并发调用（spec §3 Receive）。
/// 所有派发状态无锁——并发回调安全。
/// </para>
/// <para>
/// 读线程纪律（IFrameSink 先例）：CAN 读线程阻塞保护由 IXcpTransport 实现方的
/// 分发循环承担（XcpCanTransport 离读线程派发，spec §3 Infrastructure；XCP 侧只入队）。
/// 本层单帧路径为有界轻活（一次字典探查 + ≤7B 拷贝 + 一次标量解码，≤7 条目/ODT），
/// 重活（sink IO / 批量落盘）归 T15 sink，依 IFrameSink 契约自行入队。
/// </para>
/// <para>
/// 回调异常契约（T14-review M2）：订阅方回调抛异常<b>不终结帧分发</b>——
/// <see cref="XcpReceiveOptions.SampleDecoded"/> 抛出 → 转归因出站
/// （<see cref="XcpReceiveAttributionKind.CallbackFailed"/>，Detail 注明回调失败）；
/// <see cref="XcpReceiveOptions.Attributed"/> 自身抛出时只得放弃该条归因——
/// 本层之上再无归因出口，这是不可消除的残余，爆炸半径仅限该条归因丢失，
/// 帧分发不中断、后续帧照常。
/// </para>
/// </summary>
public sealed class XcpReceiveLoop : IDisposable
{
    private readonly IXcpTransport _transport;
    private readonly XcpReceiveOptions _options;

    /// <summary>PID → 规划 ODT 索引（构造期一次建好，之后只读）。</summary>
    private readonly Dictionary<uint, PlannedOdt> _odtByPid;

    /// <summary>对象名 → 包侧合同快照（仅方案涉及对象；构造期一次建好，之后只读）。</summary>
    private readonly Dictionary<string, ValueContract> _contracts;

    /// <summary>出站口（构造期校验非 null 后快照，调用点免空检查）。</summary>
    private readonly Action<XcpDaqSample> _sampleDecoded;
    private readonly Action<XcpReceiveAttribution> _attributed;

    /// <summary>主机时钟（ReceivedAt 取时刻来源；构造期快照，null 已在 Options 兜底）。</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 本机丢帧基线（构造期快照 transport.FramesDropped）：挂接前积累的历史丢帧
    /// 不回溯归因；此后每次派发帧核对 delta（OnFrameReceived 单线程串行调用，
    /// Interlocked 仅为防御）。
    /// </summary>
    private long _framesDroppedSeen;

    private bool _disposed;

    /// <summary>planner 自产方案（原样暴露；loop 不复制、不重建、不写）。</summary>
    public PlannedAcquisitionMap Map => _options.Map;

    public XcpReceiveLoop(IXcpTransport transport, XcpReceiveOptions options)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.Map is null)
            throw new ArgumentNullException(nameof(options), "XcpReceiveOptions.Map must not be null.");
        if (options.Contracts is null)
            throw new ArgumentNullException(nameof(options), "XcpReceiveOptions.Contracts must not be null.");
        // T14-review M3：两个出站口都强制非 null——null 即静默黑洞
        //（样本/归因解码完直接扔掉，违背"未知帧不静默丢"同源的防静默纪律）。
        // T15 sink 接管前，测试/组合根必须显式给消费者（哪怕是 no-op）。
        if (options.SampleDecoded is null)
            throw new ArgumentNullException(nameof(options),
                "XcpReceiveOptions.SampleDecoded must not be null (a null sink would silently discard decoded samples).");
        if (options.Attributed is null)
            throw new ArgumentNullException(nameof(options),
                "XcpReceiveOptions.Attributed must not be null (a null sink would silently discard unknown-frame attributions).");

        _odtByPid = new Dictionary<uint, PlannedOdt>();
        foreach (var odt in options.Map.Odts)
        {
            if (!_odtByPid.TryAdd(odt.Pid, odt))
                throw new ArgumentException(
                    $"Planned acquisition map contains duplicate ODT PID 0x{odt.Pid:X2} (planner contract violation).",
                    nameof(options));
        }

        _sampleDecoded = options.SampleDecoded;
        _attributed = options.Attributed;

        _contracts = new Dictionary<string, ValueContract>(StringComparer.Ordinal);
        foreach (var entry in options.Map.Odts.SelectMany(o => o.Entries))
        {
            if (_contracts.ContainsKey(entry.ObjectName))
                continue;
            if (!options.Contracts.TryGet(entry.ObjectName, out var contract))
                throw new ArgumentException(
                    $"No contract for planned DAQ object '{entry.ObjectName}'.",
                    nameof(options));
            _contracts.Add(entry.ObjectName, contract);
        }

        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        _framesDroppedSeen = _transport.FramesDropped;

        _transport.FrameReceived += OnFrameReceived;
    }

    private void OnFrameReceived(CanFrame frame)
    {
        AttributeLocalFrameDrops();

        if (frame.Data.Length < 1)
        {
            Attribute(XcpReceiveAttributionKind.MalformedFrame, 0x00,
                "frame has no payload; cannot classify by PID first byte.");
            return;
        }

        var pid = frame.Data.Span[0];

        // spec §3 Receive 写死：按首字节分流。0xFF/0xFE 归 XcpMaster（其自订阅
        // 已配对消费）；本层让路不消费——同 ID 上 DTO 流靠首字节到达本分支。
        if (pid is XcpPid.PositiveResponse or XcpPid.Error)
            return;

        if (pid > XcpPid.DaqDtoLast)
        {
            // EV(0xFD)/SERV(0xFC) 等：归因出站，不静默丢。
            Attribute(XcpReceiveAttributionKind.UnknownPid, pid,
                $"PID 0x{pid:X2} is neither a response PID (0xFF/0xFE) nor a DAQ DTO (0x00-0xFB).");
            return;
        }

        if (!_odtByPid.TryGetValue(pid, out var odt))
        {
            Attribute(XcpReceiveAttributionKind.UnmappedDto, pid,
                $"DAQ PID 0x{pid:X2} has no planned ODT in the acquisition map.");
            return;
        }

        DispatchDto(pid, odt, frame.Data.Span);
    }

    private void DispatchDto(byte pid, PlannedOdt odt, ReadOnlySpan<byte> data)
    {
        // host 接收时刻（spec §1 承接）：分发循环处理该帧时刻（UTC，TimeProvider 注入）。
        // transport 入队到派发的排队延迟不计——DTO DropOldest 语义下这是可得且一致的
        // 最早口径（同帧条目共用同一时刻）；与 XcpMaster/PlanGapWatcher 时钟同源。
        var receivedAt = _timeProvider.GetUtcNow();

        Span<byte> buffer = stackalloc byte[XcpCtoFrame.MaxByteLength];
        foreach (var entry in odt.Entries)
        {
            var start = 1 + entry.OffsetInOdt;
            if (data.Length < start + entry.ByteLength)
            {
                Attribute(XcpReceiveAttributionKind.MalformedFrame, pid,
                    $"DTO 0x{pid:X2} payload {data.Length}B is shorter than planned entry " +
                    $"'{entry.ObjectName}' (offset {entry.OffsetInOdt}, {entry.ByteLength}B).");
                return;
            }

            // 拷字节：帧缓冲归 transport 所有，Decode 消费私有副本（spec §3 Receive 链条）。
            var raw = buffer[..entry.ByteLength];
            data.Slice(start, entry.ByteLength).CopyTo(raw);

            double value;
            try
            {
                value = _contracts[entry.ObjectName].Decode(raw);
            }
            catch (DecodeException ex)
            {
                Attribute(XcpReceiveAttributionKind.DecodeFailed, pid,
                    $"decode failed for '{entry.ObjectName}': {ex.Message}", ex.Cause);
                continue;
            }

            // T14-review M2：订阅方回调抛异常不终结帧分发——转归因出站。
            try
            {
                _sampleDecoded(new XcpDaqSample(entry, value, receivedAt));
            }
            catch (Exception ex)
            {
                Attribute(XcpReceiveAttributionKind.CallbackFailed, pid,
                    $"sample callback failed for '{entry.ObjectName}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 本机丢帧归因：transport <see cref="IXcpTransport.FramesDropped"/> 相对基线的
    /// delta &gt; 0 时随当前帧出站（丢帧不出帧，只能搭载其后首个被派发的帧）。
    /// </summary>
    private void AttributeLocalFrameDrops()
    {
        var dropped = _transport.FramesDropped;
        var delta = dropped - Interlocked.Read(ref _framesDroppedSeen);
        if (delta <= 0)
            return;
        Interlocked.Exchange(ref _framesDroppedSeen, dropped);

        Attribute(XcpReceiveAttributionKind.LocalFrameDrop, 0x00,
            $"transport dropped {delta} DTO frame(s) locally (bounded queue DropOldest) " +
            "before dispatch.");
    }

    private void Attribute(XcpReceiveAttributionKind kind, byte firstByte, string detail,
        A2lEditor.Core.Layout.MissingCause? cause = null)
    {
        // T14-review M2 残余钉死：Attributed 自身抛异常时本层之上再无归因出口，
        // 只得放弃该条归因（爆炸半径：仅该条丢失；帧分发不中断、后续帧照常）。
        try
        {
            _attributed(new XcpReceiveAttribution(kind, firstByte, detail, cause));
        }
        catch
        {
            // Attribution sink failure is unrecoverable at this layer — see doc above.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _transport.FrameReceived -= OnFrameReceived;
    }
}
