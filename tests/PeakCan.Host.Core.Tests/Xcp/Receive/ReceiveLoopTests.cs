using System.Collections.Concurrent;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Tests.Xcp.TestKit;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Core.Xcp.Receive;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Tests.Xcp.Receive;

/// <summary>
/// S2-T14：XcpReceiveLoop（spec §3 Receive 写死条款 + §5 验收 2）。
/// <para>
/// 分流判别口径：正响应 PID 0xFF / 错误 PID 0xFE / DAQ DTO PID 0x00–0xFB ——
/// 三流共用<b>同一 CAN_ID_SLAVE</b>（本文件所有回放帧统一用
/// XcpVirtualSlave.DefaultSlaveCanId）。任何按 CAN ID 分流的实现，
/// 同一 ID 上三种 PID 的回放样本必然在此判失败。
/// </para>
/// </summary>
public class ReceiveLoopTests
{
    private static readonly CanId SlaveCanId = XcpVirtualSlave.DefaultSlaveCanId;
    private static readonly CanId MasterCanId = XcpVirtualSlave.DefaultMasterCanId;

    // ------------------------------------------------------------------
    // (a) PID 首字节分流：同一 CAN_ID_SLAVE 上的三种 PID 逐一钉流向
    // ------------------------------------------------------------------

    [Fact]
    public async Task PositiveResponse_On_Slave_CanId_Pairs_Master_Pending_And_Leaves_No_Loop_Attribution()
    {
        var (map, contracts) = Plan(Fixture());
        var transport = new ManualXcpTransport
        {
            // 响应帧在 WriteAsync 内同步派发（XcpVirtualSlave 先例：订阅者不阻塞）。
            WriteResponse = () => XcpGoldenSamples.ConnectPositiveResponse.ToArray(),
        };
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId));
        var samples = new ConcurrentBag<XcpDaqSample>();
        var attributions = new ConcurrentBag<XcpReceiveAttribution>();
        using var loop = NewLoop(transport, map, contracts, samples, attributions);

        var response = await master.SendAsync(XcpCommandEncoder.Connect());

        // 0xFF 帧走了 master 的 pending 配对（正响应原样上抛，含 PID）。
        Assert.Equal(XcpGoldenSamples.ConnectPositiveResponse.ToArray(), response);
        // 0xFF 不属于 loop 的 DTO/归因流：既无样本也无归因（分流语义钉死）。
        Assert.Empty(samples);
        Assert.Empty(attributions);
        GC.KeepAlive(loop);
    }

    [Fact]
    public async Task ErrorResponse_On_Slave_CanId_Surfaces_As_XcpErrorResponseException()
    {
        var (map, contracts) = Plan(Fixture());
        var transport = new ManualXcpTransport
        {
            WriteResponse = () => new[] { XcpPid.Error, (byte)XcpError.CmdUnknown },
        };
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId));
        var samples = new ConcurrentBag<XcpDaqSample>();
        var attributions = new ConcurrentBag<XcpReceiveAttribution>();
        using var loop = NewLoop(transport, map, contracts, samples, attributions);

        var ex = await Assert.ThrowsAsync<XcpErrorResponseException>(
            () => master.SendAsync(XcpCommandEncoder.Connect()));

        Assert.Equal(XcpError.CmdUnknown, ex.Response.Code);
        Assert.Empty(samples);
        Assert.Empty(attributions);
        GC.KeepAlive(loop);
    }

    [Fact]
    public void DaqDto_On_Same_Slave_CanId_Routes_To_Decode_Path()
    {
        // 判别点：与 (a1)/(a2) 完全相同的从机 CAN ID，首字节 0x00 → DTO 解码路径。
        // 按 CAN ID 分流的实现（一条 ID 只认一种流）无法同时让三组样本通过。
        var (map, contracts) = Plan(Fixture());
        var transport = new ManualXcpTransport();
        var samples = new ConcurrentBag<XcpDaqSample>();
        var attributions = new ConcurrentBag<XcpReceiveAttribution>();
        using var loop = NewLoop(transport, map, contracts, samples, attributions);

        transport.Raise(Dto(0x00, 0x44, 0x33, 0x22, 0x11, 0, 0, 0));

        var sample = Assert.Single(samples);
        Assert.Equal("D0", sample.Entry.ObjectName);
        Assert.Empty(attributions);
        GC.KeepAlive(loop);
    }

    // ------------------------------------------------------------------
    // (b) DTO → planner 自产映射反查 → 拷字节 → Decode 全链 + 归因出口
    // ------------------------------------------------------------------

    [Fact]
    public void Dto_Reverse_Lookup_Copy_Decode_Produces_Expected_Samples()
    {
        var (map, contracts) = Plan(Fixture());
        var transport = new ManualXcpTransport();
        var samples = new ConcurrentBag<XcpDaqSample>();
        var attributions = new ConcurrentBag<XcpReceiveAttribution>();
        using var loop = NewLoop(transport, map, contracts, samples, attributions);

        // ODT0/PID0：D0 (ULONG, 4B @offset0)。ODT1/PID1：W0/W1/W2 (UWORD @0/2/4)。
        // ODT2/PID2：B0..B6 (UBYTE @0..6)。每帧 8B：PID + 7B 数据场。
        transport.Raise(Dto(0x00, 0x44, 0x33, 0x22, 0x11, 0, 0, 0));
        transport.Raise(Dto(0x01, 0x78, 0x56, 0x21, 0x43, 0xEF, 0xCD, 0));
        transport.Raise(Dto(0x02, 10, 11, 12, 13, 14, 15, 16));

        Assert.Empty(attributions);
        Assert.Equal(11, samples.Count); // 1 + 3 + 7
        var byName = samples.ToDictionary(s => s.Entry.ObjectName, s => s.Value);
        Assert.Equal(0x11223344, byName["D0"]);
        Assert.Equal(0x5678, byName["W0"]);
        Assert.Equal(0x4321, byName["W1"]);
        Assert.Equal(0xCDEF, byName["W2"]);
        for (var i = 0; i < 7; i++)
            Assert.Equal(10.0 + i, byName[$"B{i}"]);
        GC.KeepAlive(loop);
    }

    [Fact]
    public void Unknown_Pid_Unmapped_Dto_And_Malformed_Dto_Are_Attributed_Not_Dropped()
    {
        var (map, contracts) = Plan(Fixture());
        var transport = new ManualXcpTransport();
        var samples = new ConcurrentBag<XcpDaqSample>();
        var attributions = new ConcurrentBag<XcpReceiveAttribution>();
        using var loop = NewLoop(transport, map, contracts, samples, attributions);

        // EV 0xFD（事件包）：非响应非 DTO → UnknownPid。
        transport.Raise(Dto(0xFD, 0, 0, 0, 0, 0, 0, 0));
        // DTO PID 区（0x00–0xFB）但 planner 方案无此 ODT → UnmappedDto。
        transport.Raise(Dto(0xFB, 0, 0, 0, 0, 0, 0, 0));
        // DTO 载荷短于规划布局（D0 需 PID+4B）→ MalformedFrame。
        transport.Raise(new CanFrame(SlaveCanId, new byte[] { 0x00, 0x44, 0x33 }, FrameFlags.None, ChannelId.None, default));

        Assert.Empty(samples);
        Assert.Equal(3, attributions.Count);
        Assert.Contains(attributions, a => a.Kind == XcpReceiveAttributionKind.UnknownPid && a.FirstByte == 0xFD);
        Assert.Contains(attributions, a => a.Kind == XcpReceiveAttributionKind.UnmappedDto && a.FirstByte == 0xFB);
        Assert.Contains(attributions, a => a.Kind == XcpReceiveAttributionKind.MalformedFrame);
        GC.KeepAlive(loop);
    }

    [Fact]
    public void Decode_Failure_Is_Attributed_With_Package_MissingCause()
    {
        // TXT 挂 UnsupportedConversion：包侧 Decode 抛 DecodeException(ConversionUnsupported)，
        // loop 归因出站（spec §3 Receive：复用包 MissingCause），绝不静默丢。
        var spec = Fixture();
        spec.CompuMethods =
        [
            CmIdentical,
            new A2lCompuMethod("CM_UNSUP", "u", "TAB_INH", "%d", "-",
                new UnsupportedConversion("TAB_INH", "0x00 TEXT"), new LineRange(1, 1)),
        ];
        spec.Measurements.Add(Meas("TXT", A2lDataType.UBYTE, 0x1300, "CM_UNSUP"));
        var (map, contracts) = Plan(spec);
        Assert.Contains(map.Odts.SelectMany(o => o.Entries), e => e.ObjectName == "TXT");

        var transport = new ManualXcpTransport();
        var samples = new ConcurrentBag<XcpDaqSample>();
        var attributions = new ConcurrentBag<XcpReceiveAttribution>();
        using var loop = NewLoop(transport, map, contracts, samples, attributions);

        var txtPid = map.Odts.SelectMany(o => o.Entries).Single(e => e.ObjectName == "TXT").Pid;
        transport.Raise(Dto((byte)txtPid, 0x2A, 0, 0, 0, 0, 0, 0));

        Assert.Empty(samples);
        var attribution = Assert.Single(attributions);
        Assert.Equal(XcpReceiveAttributionKind.DecodeFailed, attribution.Kind);
        Assert.Equal(A2lEditor.Core.Layout.MissingCause.ConversionUnsupported, attribution.MissingCause);
        Assert.Contains("TXT", attribution.Detail);
        GC.KeepAlive(loop);
    }

    // ------------------------------------------------------------------
    // (c) 合同与索引解析期一次建好：构造后映射不可变 + 并发读稳定
    // ------------------------------------------------------------------

    [Fact]
    public void Map_Is_ReadOnly_And_Stable_Under_Concurrent_Reads_After_Construction()
    {
        var (map, contracts) = Plan(Fixture());
        var transport = new ManualXcpTransport();
        var samples = new ConcurrentBag<XcpDaqSample>();
        var attributions = new ConcurrentBag<XcpReceiveAttribution>();
        using var loop = NewLoop(transport, map, contracts, samples, attributions);

        // loop 不重建、不复制 planner 自产映射——反查必须落在同一实例上。
        Assert.Same(map, loop.Map);

        // 暴露面只读：Odts / Entries 均为 IReadOnlyList，无任何变更入口
        // （PlannedAcquisitionMap 的反查字典私有，构造期一次建好后只读）。
        Assert.IsAssignableFrom<IReadOnlyList<PlannedOdt>>(map.Odts);
        Assert.All(map.Odts, o => Assert.IsAssignableFrom<IReadOnlyList<PlannedDaqEntry>>(o.Entries));

        // 并发读稳定：反查结果恒等（引用不变），解码入口不受读次数影响。
        Parallel.For(0, 500, _ =>
        {
            foreach (var odt in map.Odts)
            foreach (var entry in odt.Entries)
                Assert.True(
                    map.TryResolve(entry.Pid, entry.OdtIndex, entry.EntryIndex, out var resolved)
                    && ReferenceEquals(resolved, entry));
        });
        Assert.Equal(11, map.Odts.Sum(o => o.Entries.Count));
        GC.KeepAlive(loop);
    }

    // ------------------------------------------------------------------
    // (d) Decode 并发调用安全：并行回放无竞态（包侧 Decode 无结果缓存）
    // ------------------------------------------------------------------

    [Fact]
    public void Concurrent_Dto_Replay_Decodes_All_Values_Without_Races()
    {
        // 仅 2B 类：W0/W1/W2 → ODT0/PID0，每帧 3 个样本。
        var (map, contracts) = Plan(Fixture(wOnly: true));
        var transport = new ManualXcpTransport();
        var samples = new ConcurrentBag<XcpDaqSample>();
        var attributions = new ConcurrentBag<XcpReceiveAttribution>();
        using var loop = NewLoop(transport, map, contracts, samples, attributions);

        const int threads = 8;
        const int perThread = 200;
        Parallel.For(0, threads, t =>
        {
            byte lo = (byte)(t + 1), hi = (byte)(t + 10);
            var frame = Dto(0x00, lo, hi, lo, hi, lo, hi, 0);
            for (var i = 0; i < perThread; i++)
                transport.Raise(frame);
        });

        Assert.Empty(attributions);
        Assert.Equal(threads * perThread * 3, samples.Count);

        // 无竞态判别：每个样本值都精确等于某一线程的字节模式（无撕裂值），
        // 且每个模式在每个对象名下恰好出现 perThread 次。
        var expectedValues = Enumerable.Range(0, threads)
            .Select(t => (double)((t + 1) | ((t + 10) << 8)))
            .ToHashSet();
        Assert.All(samples, s => Assert.Contains(s.Value, expectedValues));
        foreach (var name in new[] { "W0", "W1", "W2" })
        foreach (var expected in expectedValues)
            Assert.Equal(perThread, samples.Count(s => s.Entry.ObjectName == name && s.Value == expected));
        GC.KeepAlive(loop);
    }

    // ------------------------------------------------------------------
    // 测试基础设施
    // ------------------------------------------------------------------

    /// <summary>手动派发 transport：帧在测试线程上同步派发（等效满足"回调不阻塞"契约）。</summary>
    private sealed class ManualXcpTransport : IXcpTransport
    {
        public event Action<CanFrame>? FrameReceived;

        /// <summary>WriteAsync 内同步派发的响应帧脚本（XcpVirtualSlave 同型）。</summary>
        public Func<byte[]>? WriteResponse { get; init; }

        public long FramesDropped => 0;

        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        {
            var bytes = WriteResponse?.Invoke();
            if (bytes is { Length: > 0 })
                Raise(bytes);
            return ValueTask.FromResult(Result<Unit>.Ok(default));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Raise(CanFrame frame) => FrameReceived?.Invoke(frame);

        public void Raise(byte[] bytes) =>
            FrameReceived?.Invoke(new CanFrame(SlaveCanId, bytes, FrameFlags.None, ChannelId.None, default));
    }

    private static XcpReceiveLoop NewLoop(
        ManualXcpTransport transport,
        PlannedAcquisitionMap map,
        ContractSet contracts,
        ConcurrentBag<XcpDaqSample> samples,
        ConcurrentBag<XcpReceiveAttribution> attributions)
        => new(transport, new XcpReceiveOptions
        {
            Map = map,
            Contracts = contracts,
            SampleDecoded = samples.Add,
            Attributed = attributions.Add,
        });

    private static CanFrame Dto(byte pid, params byte[] payload)
    {
        var data = new byte[1 + payload.Length];
        data[0] = pid;
        payload.CopyTo(data, 1);
        return new CanFrame(SlaveCanId, data, FrameFlags.None, ChannelId.None, default);
    }

    /// <summary>
    /// 标准夹具：D0 (ULONG 4B) + W0..W2 (UWORD 2B) + B0..B6 (UBYTE 1B)。
    /// wOnly = 仅 2B 类（并发测试夹具）。
    /// </summary>
    private static ModuleSpec Fixture(bool wOnly = false)
    {
        var spec = new ModuleSpec();
        if (!wOnly)
            spec.Measurements.Add(Meas("D0", A2lDataType.ULONG, 0x1000));
        spec.Measurements.AddRange(Uniform("W", A2lDataType.UWORD, 3, 0x1100));
        if (!wOnly)
            spec.Measurements.AddRange(Uniform("B", A2lDataType.UBYTE, 7, 0x1200));
        return spec;
    }

    // ---- A2L 文档构建（AcquisitionPlannerTests 同口径复制，保证 planner 输入一致）----

    private sealed class ModuleSpec
    {
        public List<A2lMeasurement> Measurements = [];
        public A2lCompuMethod[] CompuMethods = [CmIdentical];
        public XcpDaqList DaqList = DaqList(firstPid: 0, maxOdtEntries: 100);
        public uint? MaxOdtEntrySizeDaq = 4;
    }

    private static (PlannedAcquisitionMap Map, ContractSet Contracts) Plan(ModuleSpec spec)
    {
        var doc = Doc(spec);
        var contracts = new ContractSet(doc);
        var placeholders = AcquisitionPlan.Build(contracts);
        return (AcquisitionPlanner.Plan(contracts, placeholders), contracts);
    }

    private static A2lDocument Doc(ModuleSpec spec)
    {
        var module = MakeModule(spec);
        return new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            new[] { module }, "", 1);
    }

    private static A2lModule MakeModule(ModuleSpec spec)
    {
        var daq = new XcpDaq(
            Dynamic: false, MaxDaq: 1, MaxEventChannel: 1, MinDaq: null,
            OptimisationType: "STATIC", AddressExtension: "", IdentificationFieldType: "",
            GranularityOdtEntrySizeDaq: "", MaxOdtEntrySizeDaq: spec.MaxOdtEntrySizeDaq, OverloadIndication: false,
            new[] { spec.DaqList }, Array.Empty<XcpEventChannel>(),
            Array.Empty<XcpMissingField>(), "");
        var ifData = new XcpIfData(XcpIfDataScope.ModuleLevel, null, daq, null, null,
            Array.Empty<XcpOnCan>(), Array.Empty<XcpSegment>(),
            Array.Empty<A2lUnknownBlock>(), Array.Empty<XcpMissingField>(), "");

        return new A2lModule("M", "m",
            spec.Measurements, Array.Empty<A2lCharacteristic>(), Array.Empty<A2lAxisPts>(),
            spec.CompuMethods, Array.Empty<A2lRecordLayout>(), Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1), IfDataXcp: ifData);
    }

    private static XcpDaqList DaqList(ushort firstPid, uint maxOdtEntries, uint number = 0) =>
        new(number, "DAQ", 15, maxOdtEntries, firstPid, 0, "");

    private static A2lCompuMethod CmIdentical =>
        new("CM_ID", "id", "IDENTICAL", "%d", "-", new IdenticalConversion(), new LineRange(1, 1));

    private static A2lMeasurement Meas(string name, A2lDataType dt, ulong addr, string conversion = "CM_ID") =>
        new(name, "d", dt, conversion, "0", "0", "0", "65535", addr, new LineRange(1, 1));

    private static List<A2lMeasurement> Uniform(string prefix, A2lDataType dt, int count, ulong baseAddr) =>
        Enumerable.Range(0, count).Select(i => Meas($"{prefix}{i}", dt, baseAddr + (ulong)(i * 8))).ToList();
}
