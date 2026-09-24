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
/// 订阅方回调（<see cref="XcpReceiveOptions.SampleDecoded"/> /
/// <see cref="XcpReceiveOptions.Attributed"/>）同样不得阻塞、不得抛出。
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

        _odtByPid = new Dictionary<uint, PlannedOdt>();
        foreach (var odt in options.Map.Odts)
        {
            if (!_odtByPid.TryAdd(odt.Pid, odt))
                throw new ArgumentException(
                    $"Planned acquisition map contains duplicate ODT PID 0x{odt.Pid:X2} (planner contract violation).",
                    nameof(options));
        }

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

        _transport.FrameReceived += OnFrameReceived;
    }

    private void OnFrameReceived(CanFrame frame)
    {
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

            _options.SampleDecoded?.Invoke(new XcpDaqSample(entry, value));
        }
    }

    private void Attribute(XcpReceiveAttributionKind kind, byte firstByte, string detail,
        A2lEditor.Core.Layout.MissingCause? cause = null)
        => _options.Attributed?.Invoke(new XcpReceiveAttribution(kind, firstByte, detail, cause));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _transport.FrameReceived -= OnFrameReceived;
    }
}
