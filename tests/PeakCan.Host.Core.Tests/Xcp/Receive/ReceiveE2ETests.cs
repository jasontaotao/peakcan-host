using A2lEditor.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using Microsoft.Extensions.Time.Testing;
using PeakCan.Host.Core.Tests.Xcp.TestKit;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Core.Xcp.Receive;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Tests.Xcp.Receive;

/// <summary>
/// S2-T16 Receive 层端到端（spec §5 验收 2 末句：模拟从机端到端 + 空窗归因断言全项）：
/// <list type="bullet">
/// <item>(a) 计划内换表空窗：轮转 stop 后 watcher 开窗（PlanGapOpened），期内 DTO 恢复
/// 关窗——无断流误报（FakeTimeProvider 推进越过预算也不升级）。</item>
/// <item>(b) 空窗超预期：预算内无 DTO → 升级断流，AcquisitionInterrupted 到达 sink
/// 通道（包枚举槽位由 Receive 层生产，spec 写死）。</item>
/// <item>(c) 965 测量端到端：真机 A2L + RAM_PROBE 夹具，DTO 流 → planner 方案反查 →
/// 包侧 Decode → sink 全链路字节正确性（注入字节 × 端序 × RAT_FUNC 换算独立验算）。</item>
/// <item>(d) 全程无 DOWNLOAD（XcpPid.Download=0xF0 Spy 断言续用，spec §0/D2）。</item>
/// </list>
/// 组合根三件套同装义务（T15 评审钉死）：gapNotifier=PlanGapWatcher、
/// SampleDecoded=sink 接线、Attributed=sink 归因接线——本文件经
/// XcpAcquisitionSessionOptions.Sink 一个入口验证三件套同时生效。
/// </summary>
public class ReceiveE2ETests
{
    // ---- (a) 计划内换表空窗：开 → DTO 恢复关，无断流误报 ----

    [Fact]
    public async Task PlanGap_Opens_On_Rotation_Closes_On_Recovered_Dto_And_Never_Interrupts()
    {
        var clock = new FakeTimeProvider();
        var sink = new InMemoryAcquisitionSink(16);
        using var session = NewSession(new XcpVirtualSlave(), out var spy, sink, clock);
        var map = session.Plan(Contracts(SmallDoc()), Placeholders(SmallDoc()));

        var rotation = await session.ConfigureRotationAsync(map);
        Assert.Equal(RotationTableState.Running, rotation.TableState);

        // 三件套证据一（接线存在）：Plan 之后组合根已装配 ReceiveLoop。
        Assert.NotNull(session.ReceiveLoop);
        Assert.Same(map, session.ReceiveLoop!.Map);

        // 空窗开：stop 应答落地后 watcher 开窗，恰一条 PlanGapOpened（1 ODT / 1 entry）。
        var opened = Assert.Single(DrainGaps(sink));
        Assert.Equal(XcpAcquisitionGapKind.PlanGapOpened, opened.Kind);
        Assert.Contains("odts 1", opened.Detail);
        Assert.NotNull(opened.ExpectedMaxDuration);
        Assert.True(opened.ExpectedMaxDuration!.Value > TimeSpan.Zero);

        // 期内恢复：DTO 到达 → 先关窗后入队（恢复样本不算空窗内数据）。
        TransportOf(spy).InjectDto(0x00, 0x2A);
        var sample = Assert.Single(DrainValues(sink));
        Assert.Equal("M0", sample.Entry.ObjectName);
        Assert.Equal(42d, sample.Value);

        // 无断流误报：推进越过空窗上界，不得升级断流。
        clock.Advance(opened.ExpectedMaxDuration!.Value + TimeSpan.FromSeconds(10));
        // 开窗事件已消费、期内恢复不产归因：通道应保持为空（无断流误报的准确形状）。
        Assert.Empty(DrainGaps(sink));
        Assert.Equal(0, sink.DroppedGaps);
    }

    // ---- (b) 空窗超预期：升级断流 ----

    [Fact]
    public async Task PlanGap_Expires_Without_Dto_Upgrades_To_AcquisitionInterrupted()
    {
        var clock = new FakeTimeProvider();
        var sink = new InMemoryAcquisitionSink(16);
        using var session = NewSession(new XcpVirtualSlave(), out var spy, sink, clock);
        var map = session.Plan(Contracts(SmallDoc()), Placeholders(SmallDoc()));

        var rotation = await session.ConfigureRotationAsync(map);
        Assert.Equal(RotationTableState.Running, rotation.TableState);

        var opened = Assert.Single(DrainGaps(sink));
        Assert.Equal(XcpAcquisitionGapKind.PlanGapOpened, opened.Kind);

        // 预算内无 DTO：超时定时器触发 → AcquisitionInterrupted 到达 sink 通道。
        clock.Advance(opened.ExpectedMaxDuration!.Value);
        // 开窗事件已消费：本轮只应到达断流归因一条。
        var interrupted = Assert.Single(DrainGaps(sink));
        Assert.Equal(XcpAcquisitionGapKind.AcquisitionInterrupted, interrupted.Kind);
        // 包侧枚举槽位由 Receive 层生产（spec 写死条款），不新增包枚举值。
        Assert.Equal(MissingCause.AcquisitionInterrupted, interrupted.Cause);
        Assert.Equal("A2lEditor.Core", typeof(MissingCause).Assembly.GetName().Name);

        // 断流只宣判一次（单次触发，无周期性重复）。
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Empty(DrainGaps(sink)); // 断流只宣判一次：再推进无新归因、无周期性重复
    }

    // ---- (c) 965 测量端到端：DTO 流 → 反查 → Decode → sink 字节正确性 ----

    [Fact]
    public async Task RealSample_Dto_Stream_ReverseLookup_Decode_Sink_Byte_Correct()
    {
        var clock = new FakeTimeProvider();
        var sink = new InMemoryAcquisitionSink(64);
        var doc = FullFlowDoc();
        var contracts = Contracts(doc);
        using var session = NewSession(new XcpVirtualSlave(), out var spy, sink, clock);
        var map = session.Plan(contracts, Placeholders(doc));

        var rotation = await session.ConfigureRotationAsync(map);
        Assert.Equal(RotationTableState.Running, rotation.TableState);

        // 夹具钉子（T13 先例）：15 ODT × 4B（ULONG / FLOAT32_IEEE，小端，RAT_FUNC）。
        var entries = map.Odts.SelectMany(o => o.Entries).ToArray();
        Assert.Equal(15, entries.Length);
        Assert.All(entries, e =>
        {
            var c = contracts[e.ObjectName];
            Assert.Equal(4, e.ByteLength);
            Assert.False(c.BigEndian);
            Assert.IsType<RatFuncConversion>(c.Conversion);
            Assert.True(c.DataType is A2lDataType.ULONG or A2lDataType.FLOAT32_IEEE,
                $"{e.ObjectName}: fixture pin expects ULONG/FLOAT32_IEEE, got {c.DataType}");
        });

        // 回放脚本注入：逐 ODT 注入确定的字节模式（端序敏感——字节序反了值必错）。
        var slave = TransportOf(spy);
        foreach (var odt in map.Odts)
        {
            var entry = Assert.Single(odt.Entries);
            slave.InjectDto((byte)odt.Pid, PayloadFor(contracts[entry.ObjectName], odt.Pid));
        }

        // 全链路字节正确性：sink 侧逐条独立验算（原始字节 → 端序读数 → RAT_FUNC）。
        var samples = DrainValues(sink).ToArray();
        Assert.Equal(15, samples.Length);
        Assert.All(samples, s =>
        {
            // 反查正确性：sample.Entry 必须是 planner 自产方案里该 PID 的条目。
            var odt = Assert.Single(map.Odts, o => o.Pid == s.Entry.Pid);
            Assert.Contains(s.Entry, odt.Entries);
            Assert.Equal(ExpectedPhysical(contracts[s.Entry.ObjectName], s.Entry.Pid), s.Value, 9);
        });

        // 换表空窗在首条 DTO 到达时恢复关闭：推进越过预算不得升级断流。
        var opened = Assert.Single(DrainGaps(sink), g => g.Kind == XcpAcquisitionGapKind.PlanGapOpened);
        clock.Advance(opened.ExpectedMaxDuration!.Value + TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(DrainGaps(sink), g => g.Kind == XcpAcquisitionGapKind.AcquisitionInterrupted);
        Assert.Equal(0, sink.Values.Count); // 样本已全量消费验算，通道应清空
    }

    // ---- (d) 全程无 DOWNLOAD（spec §0/D2，Spy 断言续用）----

    [Fact]
    public async Task Receive_FullFlow_Never_Sends_Download_Command()
    {
        var clock = new FakeTimeProvider();
        var sink = new InMemoryAcquisitionSink(64);
        var doc = FullFlowDoc();
        var contracts = Contracts(doc);
        using var session = NewSession(new XcpVirtualSlave(), out var spy, sink, clock);
        var map = session.Plan(contracts, Placeholders(doc));

        await session.ConfigureRotationAsync(map);
        var slave = TransportOf(spy);
        foreach (var odt in map.Odts)
            slave.InjectDto((byte)odt.Pid, new byte[odt.Entries.Sum(e => e.ByteLength)]);

        // 轮询兜底两拍（PollBeatAsync 不走时钟延迟，FakeTimeProvider 下安全）。
        var beat1 = await session.PollBeatAsync();
        var beat2 = await session.PollBeatAsync();
        Assert.Equal(PollingCycleOutcome.Executed, beat1.Outcome);
        Assert.Equal(PollingCycleOutcome.Executed, beat2.Outcome);

        Assert.NotEmpty(spy.Sent);
        var download = spy.Sent.Count(f => f.Data.Span[0] == XcpPid.Download);
        Assert.True(download == 0,
            $"DOWNLOAD (0x{XcpPid.Download:X2}) must never be sent by the acquisition session (spec §0/D2); " +
            $"spy recorded {download} such frame(s).");
    }

    // ------------------------------------------------------------------
    // 夹具与断言辅助
    // ------------------------------------------------------------------

    private static XcpAcquisitionSession NewSession(
        IXcpTransport transport, out XcpTransportSpy spy, IXcpAcquisitionSink sink, FakeTimeProvider clock)
    {
        spy = new XcpTransportSpy(transport);
        return new XcpAcquisitionSession(spy, MasterOptions(),
            new XcpAcquisitionSessionOptions
            {
                Sink = sink,
                Polling = new PollingSchedulerOptions { Period = TimeSpan.FromMilliseconds(1) },
            }, clock);
    }

    private static XcpMasterOptions MasterOptions() => new(XcpVirtualSlave.DefaultMasterCanId);

    /// <summary>XcpTransportSpy 是纯包装，回退取被包裹的从机引用（注入 DTO 用）。</summary>
    private static XcpVirtualSlave TransportOf(XcpTransportSpy spy)
    {
        var field = typeof(XcpTransportSpy).GetField("_inner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return Assert.IsAssignableFrom<XcpVirtualSlave>(field!.GetValue(spy));
    }

    /// <summary>4B 注入模式：ULONG 递增整数、FLOAT32 递增尾数——每条目字节互不相同。</summary>
    private static byte[] PayloadFor(ValueContract contract, uint pid)
    {
        var i = (int)(pid & 0xFF);
        return contract.DataType == A2lDataType.FLOAT32_IEEE
            ? BitConverter.GetBytes(1.5f + i)
            : BitConverter.GetBytes(0x01020304u + (uint)i);
    }

    /// <summary>独立验算口：小端读原始值 + RAT_FUNC phys=(Ax²+Bx+C)/(Dx²+Ex+F)，不走 ValueContract.Decode。</summary>
    private static double ExpectedPhysical(ValueContract contract, uint pid)
    {
        var i = (int)(pid & 0xFF);
        var raw = contract.DataType == A2lDataType.FLOAT32_IEEE
            ? (double)BitConverter.ToSingle(BitConverter.GetBytes(1.5f + i), 0)
            : (double)(0x01020304u + (uint)i);
        var r = Assert.IsType<RatFuncConversion>(contract.Conversion);
        return (r.A * raw * raw + r.B * raw + r.C) / (r.D * raw * raw + r.E * raw + r.F);
    }

    private static IEnumerable<XcpAcquisitionGap> DrainGaps(InMemoryAcquisitionSink sink)
    {
        while (sink.Gaps.TryRead(out var gap))
            yield return gap;
    }

    private static IEnumerable<XcpDaqSample> DrainValues(InMemoryAcquisitionSink sink)
    {
        while (sink.Values.TryRead(out var sample))
            yield return sample;
    }

    // ---- 缩小夹具（T13 同款口径：单模块 + 单 DAQ 表 + 单段映射，UBYTE 同比换算）----

    private static A2lDocument SmallDoc()
    {
        var module = new A2lModule("M", "m",
            new[] { new A2lMeasurement("M0", "d", A2lDataType.UBYTE, "CM_ID", "0", "0", "0", "65535", 0x1000, new LineRange(1, 1)) },
            Array.Empty<A2lCharacteristic>(), Array.Empty<A2lAxisPts>(),
            new[] { new A2lCompuMethod("CM_ID", "id", "IDENTICAL", "%d", "-", new IdenticalConversion(), new LineRange(1, 1)) },
            Array.Empty<A2lRecordLayout>(), Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1), MemorySegments: Segments(),
            IfDataXcp: IfData(new XcpDaqList(0, "DAQ", 15, 100, 0, 0, "")));
        return new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            new[] { module }, "", 1);
    }

    private static A2lMemorySegment[] Segments() => new[]
    {
        new A2lMemorySegment("CAL", "cal", "DATA", "FLASH", "INTERN", 0x1000, 0x1000,
            Array.Empty<string>(), new LineRange(1, 1),
            IfDataXcp: new XcpIfData(XcpIfDataScope.MemorySegmentLevel, null, null, null, null,
                Array.Empty<XcpOnCan>(),
                new[]
                {
                    new XcpSegment(0, 1, 0, 0, 0,
                        new[] { new XcpAddressMapping(0x1000, 0x9000, 0x1000, Array.Empty<XcpMissingField>(), "") },
                        1, Array.Empty<XcpMissingField>(), ""),
                },
                Array.Empty<A2lUnknownBlock>(), Array.Empty<XcpMissingField>(), "")),
    };

    private static XcpIfData IfData(XcpDaqList daqList) => new(
        XcpIfDataScope.ModuleLevel, null,
        new XcpDaq(Dynamic: false, MaxDaq: 1, MaxEventChannel: 1, MinDaq: null,
            OptimisationType: "STATIC", AddressExtension: "", IdentificationFieldType: "",
            GranularityOdtEntrySizeDaq: "", MaxOdtEntrySizeDaq: 4, OverloadIndication: false,
            new[] { daqList }, Array.Empty<XcpEventChannel>(),
            Array.Empty<XcpMissingField>(), ""),
        null, null,
        Array.Empty<XcpOnCan>(), Array.Empty<XcpSegment>(),
        Array.Empty<A2lUnknownBlock>(), Array.Empty<XcpMissingField>(), "");

    /// <summary>真机 A2L + 测试侧补 RAM 映射段（T13 夹具同款，965 测量输入 100% 真机文件）。</summary>
    private static A2lDocument FullFlowDoc()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "App_merge_INCA.a2l");
        var parsed = Asap2PackageApi.ParseFile(path);
        Assert.NotNull(parsed.Value);
        var doc = parsed.Value!;
        var module = doc.Modules[0];
        var ramProbe = new A2lMemorySegment(
            "RAM_PROBE", "T16 test fixture: mapping the sample A2L omits for its measurement RAM",
            "DATA", "RAM_INTERN", "INTERN", 0x1FFE0000, 0x30000, Array.Empty<string>(), new LineRange(1, 1),
            IfDataXcp: new XcpIfData(
                XcpIfDataScope.MemorySegmentLevel, null, null, null, null,
                Array.Empty<XcpOnCan>(),
                new[]
                {
                    new XcpSegment(0, 1, 0, 0, 0,
                        new[] { new XcpAddressMapping(0x1FFE0000, 0x1FFE0000, 0x30000, Array.Empty<XcpMissingField>(), "") },
                        1, Array.Empty<XcpMissingField>(), ""),
                },
                Array.Empty<A2lUnknownBlock>(), Array.Empty<XcpMissingField>(), ""));
        return doc with
        {
            Modules = new[] { module with { MemorySegments = (module.MemorySegments ?? Array.Empty<A2lMemorySegment>()).Append(ramProbe).ToArray() } },
        };
    }

    private static ContractSet Contracts(A2lDocument doc) => Asap2PackageApi.Contracts(doc);

    private static AcquisitionPlan Placeholders(A2lDocument doc) => AcquisitionPlan.Build(Contracts(doc));
}