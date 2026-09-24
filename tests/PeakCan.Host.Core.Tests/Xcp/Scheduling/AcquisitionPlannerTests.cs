using System.Text.Json;
using A2lEditor.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Tests.Xcp.Scheduling;

/// <summary>
/// S2-T9：AcquisitionPlanner（spec §3 Scheduling / §1 从机硬约束）。
/// 写死口径：单 DAQ 表、15 ODT、ODT 数据场 7B（DTO 8B − PID 1B）、
/// 4B×1 / 2B×3 / 1B×7 同类同箱、超 105 B/拍降级轮询、占位索引必须被
/// planner 自建打包方案整体替换（[H2]）。
/// </summary>
public class AcquisitionPlannerTests
{
    private const int MaxOdts = 15;
    private const int OdtDataFieldBytes = 7;

    // ------------------------------------------------------------------
    // (a) 7B 装箱：逐尺寸类（MAX_ODT_ENTRIES=100 仅声明天花板，禁当容量用）
    // ------------------------------------------------------------------

    [Fact]
    public void OneByte_Objects_Pack_Seven_Per_Odt()
    {
        // 8 个 1B 对象 → 2 个 ODT（7+1）。MAX_ODT_ENTRIES 钉成 100：
        // 一个 ODT 永不出现第 8 条，容量只认 7B 数据场。
        var map = Plan(m =>
        {
            m.Measurements = Uniform("B", A2lDataType.UBYTE, 8, 0x1000);
            m.DaqList = DaqList(firstPid: 0, maxOdtEntries: 100);
        });

        Assert.True(map.Odts.Count == 2);
        Assert.Equal(7, map.Odts[0].Entries.Count);
        Assert.Single(map.Odts[1].Entries);
        Assert.All(map.Odts.SelectMany(o => o.Entries), e => Assert.Equal(1, e.ByteLength));
        for (var i = 0; i < map.Odts[0].Entries.Count; i++)
            Assert.Equal((ushort)i, map.Odts[0].Entries[i].EntryIndex);
    }

    [Fact]
    public void TwoByte_Objects_Pack_Three_Per_Odt()
    {
        // 4 个 2B 对象 → 2 个 ODT（3+1）：第 4 条放不下（4×2=8 > 7B 数据场）。
        var map = Plan(m =>
        {
            m.Measurements = Uniform("W", A2lDataType.UWORD, 4, 0x1000);
            m.DaqList = DaqList(firstPid: 0, maxOdtEntries: 100);
        });

        Assert.True(map.Odts.Count == 2);
        Assert.Equal(3, map.Odts[0].Entries.Count);
        Assert.Single(map.Odts[1].Entries);
        Assert.All(map.Odts.SelectMany(o => o.Entries), e => Assert.Equal(2, e.ByteLength));
    }

    [Fact]
    public void FourByte_Objects_Pack_One_Per_Odt()
    {
        // 2 个 4B 对象 → 2 个 ODT 各 1 条：单条已占 4B，同箱禁再放（同类同箱
        // 且 2×4=8 > 7B）。
        var map = Plan(m =>
        {
            m.Measurements = Uniform("D", A2lDataType.ULONG, 2, 0x1000);
            m.DaqList = DaqList(firstPid: 0, maxOdtEntries: 100);
        });

        Assert.True(map.Odts.Count == 2);
        Assert.All(map.Odts, o => Assert.Single(o.Entries));
        Assert.All(map.Odts.SelectMany(o => o.Entries), e => Assert.Equal(4, e.ByteLength));
    }

    [Fact]
    public void Mixed_Classes_Never_Share_An_Odt()
    {
        // 1×4B + 3×2B + 7×1B → 3 个 ODT，每个 ODT 只装一个尺寸类（同类同箱）。
        var measurements = new List<A2lMeasurement> { Meas("D0", A2lDataType.ULONG, 0x1000) };
        measurements.AddRange(Uniform("W", A2lDataType.UWORD, 3, 0x1100));
        measurements.AddRange(Uniform("B", A2lDataType.UBYTE, 7, 0x1200));
        var map = Plan(m =>
        {
            m.Measurements = measurements;
            m.DaqList = DaqList(firstPid: 0, maxOdtEntries: 100);
        });

        Assert.True(map.Odts.Count == 3);
        var classCounts = new List<(int Class, int Count)>();
        foreach (var odt in map.Odts)
        {
            Assert.NotEmpty(odt.Entries);
            Assert.All(odt.Entries, e => Assert.Equal(odt.SizeClassBytes, e.ByteLength));
            classCounts.Add((odt.SizeClassBytes, odt.Entries.Count));
        }

        Assert.Contains((4, 1), classCounts);
        Assert.Contains((2, 3), classCounts);
        Assert.Contains((1, 7), classCounts);
    }

    // ------------------------------------------------------------------
    // (b) 真机 965 测量 / 105 B 每拍：确定性（seed-free，同输入两次规划一致）
    // ------------------------------------------------------------------

    [Fact]
    public void RealSample_Plans_Deterministically_Within_105B_PerTick()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "App_merge_INCA.a2l");
        var parsed = Asap2PackageApi.ParseFile(path);
        Assert.NotNull(parsed.Value);
        var contracts = Asap2PackageApi.Contracts(parsed.Value!);
        var placeholders = AcquisitionPlan.Build(contracts);

        // seed-free 确定性：同输入两次规划字节级一致（JSON 序列化逐字节比对）。
        var map1 = AcquisitionPlanner.Plan(contracts, placeholders);
        var map2 = AcquisitionPlanner.Plan(contracts, placeholders);
        Assert.Equal(JsonSerializer.Serialize(map1), JsonSerializer.Serialize(map2));

        // 每拍容量：ODT 数 ≤ 15，任一 ODT ≤ 7B，总打包字节 ≤ 15×7=105。
        Assert.NotEmpty(map1.Odts);
        Assert.True(map1.Odts.Count <= MaxOdts);
        Assert.All(map1.Odts, o => Assert.True(o.Entries.Sum(e => e.ByteLength) <= OdtDataFieldBytes));
        Assert.True(map1.Odts.SelectMany(o => o.Entries).Sum(e => e.ByteLength) <= MaxOdts * OdtDataFieldBytes);

        // 真机 965 测量远超单表容量 → 降级集合非空；轮询条目必须带逻辑地址。
        Assert.NotEmpty(map1.PollingEntries);
        Assert.All(map1.PollingEntries, e => Assert.True(e.LogicalAddress != 0));

        // 反查索引：每个打包条目都能用 planner 自产 (PID, ODT, Entry) 命中同一对象。
        foreach (var entry in map1.Odts.SelectMany(o => o.Entries))
        {
            Assert.True(map1.TryResolve(entry.Pid, entry.OdtIndex, entry.EntryIndex, out var resolved));
            Assert.Equal(entry.ObjectName, resolved.ObjectName);
        }
    }

    // ------------------------------------------------------------------
    // (c) 超 105 B/拍 → 自动降级轮询，降级集合非空
    // ------------------------------------------------------------------

    [Fact]
    public void Overflow_Beyond_105B_PerTick_Downgrades_To_Polling()
    {
        // 120×1B = 120 B > 105 B/拍：15 ODT × 7B 装满 105 B，余 15 条降级。
        var map = Plan(m =>
        {
            m.Measurements = Uniform("OV", A2lDataType.UBYTE, 120, 0x1000);
            m.DaqList = DaqList(firstPid: 0, maxOdtEntries: 100);
        });

        Assert.True(map.Odts.Count == MaxOdts);
        Assert.Equal(MaxOdts * OdtDataFieldBytes, map.Odts.SelectMany(o => o.Entries).Sum(e => e.ByteLength));
        Assert.True(map.PollingEntries.Count == 15);
        Assert.All(map.PollingEntries, e => Assert.Equal(PlannedPollingCause.DaqCapacityExceeded, e.Cause));
    }

    // ------------------------------------------------------------------
    // (d) [H2] 占位索引必须被 planner 自建打包方案替换
    // ------------------------------------------------------------------

    [Fact]
    public void Placeholder_Indexes_Are_Replaced_By_Planner_Packing()
    {
        // FIRST_PID=0x50（0x50+14=0x5E，落在 DTO PID 区内）：占位三元组 = (0x50+文档序 k, ODT=0, Entry=k)。
        // 文档序首位放 1B 对象 → 它进不了 ODT0（4B 类先占箱），占位/输出必然错位。
        var measurements = new List<A2lMeasurement> { Meas("B_A", A2lDataType.UBYTE, 0x1000) };
        measurements.AddRange(Uniform("D", A2lDataType.ULONG, 2, 0x1100));
        measurements.AddRange(Uniform("W", A2lDataType.UWORD, 3, 0x1200));
        measurements.AddRange(Uniform("B", A2lDataType.UBYTE, 7, 0x1300));
        const ushort firstPid = 0x50;

        var spec = new ModuleSpec
        {
            Measurements = measurements,
            DaqList = DaqList(firstPid, maxOdtEntries: 100),
        };
        var contracts = new ContractSet(Doc(spec));
        var placeholders = AcquisitionPlan.Build(contracts);

        // 对照组：plan 的占位口径确为 (FIRST_PID+k, ODT=0, Entry=k)。
        var docOrder = placeholders.All.Select(f => f.ObjectName).Distinct().ToList();
        Assert.True(placeholders.TryResolve(firstPid, 0, 0, out var firstFragments));
        Assert.Equal(docOrder[0], firstFragments[0].ObjectName);

        var map = AcquisitionPlanner.Plan(contracts, placeholders);
        var output = map.Odts.SelectMany(o => o.Entries).ToList();
        Assert.NotEmpty(output);

        // planner 自建 ODT 布局：PID = FIRST_PID + ODT 序号，箱内 Entry 从 0 起。
        Assert.All(output, e => Assert.Equal((uint)(firstPid + e.OdtIndex), e.Pid));
        var odt0 = map.Odts[0];
        Assert.Single(odt0.Entries);
        Assert.Equal((ushort)0, odt0.Entries[0].EntryIndex);

        // [H2] 占位编号不得出现在输出：对文档序 k 的对象，凡被打包挪动过的
        //（输出三元组 ≠ 其占位三元组），用占位三元组反查输出映射绝不允许命中
        // 它自己——占位索引没有一条被当作真实配置沿用。
        // [H2] 判别收紧：任何打包对象的输出三元组都不得等于其自身占位三元组，
        // 重合必须进显式白名单。白名单恒空——耦合说明：装箱填充顺序钉死为
        // SizeClasses 首类 4B 先占箱（4B→2B→1B），docOrder 0 的 1B 对象永远
        // 进不了 ODT0 Entry0，故本夹具不存在巧合重合；若改填充顺序，必须
        // 重审白名单（重合 = 占位编号被沿用 = [H2] 违例）。
        var ownPlaceholderOverlapWhitelist = new HashSet<int>();
        for (var k = 0; k < docOrder.Count; k++)
        {
            var packed = output.Single(e => e.ObjectName == docOrder[k]);
            var isOwnPlaceholder = packed.Pid == firstPid + (uint)k &&
                                   packed.OdtIndex == 0 && packed.EntryIndex == (ushort)k;
            Assert.True(!isOwnPlaceholder || ownPlaceholderOverlapWhitelist.Contains(k));
        }

        // 每个打包对象在其输出坐标可反查（planner 自产映射自洽）。
        foreach (var entry in output)
        {
            Assert.True(map.TryResolve(entry.Pid, entry.OdtIndex, entry.EntryIndex, out var resolved));
            Assert.Equal(entry.ObjectName, resolved.ObjectName);
        }
    }

    // ------------------------------------------------------------------
    // (e) 位域/非字节对齐量与 >4B 量直接归轮询（DAQ 装不下）+ 地址翻译唯一入口
    // ------------------------------------------------------------------

    [Fact]
    public void Oversized_And_NonByteAligned_Objects_Route_To_Polling()
    {
        // FLOAT64 8B > 4B 直归轮询；VAL_BLK 3×UBYTE = 3B 无 1/2/4 尺寸类
        //（位域/非字节对齐量同口径——真机 BIT_MASK 全 0、包契约未建位域字段
        //（spec §5.6），包侧能表达的"装不下"形态就是非整类字节量）。
        // 内存段映射 logical 0x1000→physical 0x9000（非恒等，证明翻译走了
        // 包侧 XcpAddressMap.TryTranslate 唯一入口，planner 未自建映射）。
        var map = Plan(new ModuleSpec
        {
            Measurements =
            {
                Meas("F64", A2lDataType.FLOAT64_IEEE, 0x1100),
                Meas("D_OK", A2lDataType.ULONG, 0x1000),
            },
            Characteristics = new[] { ValBlk3("V3", 0x1200) },
            Layouts = new[] { UbyteLayout() },
            DaqList = DaqList(firstPid: 0, maxOdtEntries: 100),
            Segments = new[] { CalSegment() },
        });

        // DAQ 侧只剩 4B 合法类。
        Assert.Single(map.Odts);
        var daqEntry = Assert.Single(map.Odts[0].Entries);
        Assert.Equal("D_OK", daqEntry.ObjectName);

        // 轮询侧：8B 与 3B 两条，带降级归因 + 经段映射的物理地址
        //（0x1100−0x1000+0x9000=0x9100；0x1200−0x1000+0x9000=0x9200）。
        Assert.True(map.PollingEntries.Count == 2);
        var f64 = map.PollingEntries.Single(e => e.ObjectName == "F64");
        Assert.Equal(PlannedPollingCause.ObjectTooLarge, f64.Cause);
        Assert.Equal(0x1100UL, f64.LogicalAddress);
        Assert.Equal(0x9100UL, f64.PhysicalAddress);
        var v3 = map.PollingEntries.Single(e => e.ObjectName == "V3");
        Assert.Equal(PlannedPollingCause.NotByteAlignedClass, v3.Cause);
        Assert.Equal(0x9200UL, v3.PhysicalAddress);
    }

    // ------------------------------------------------------------------
    // (F1) PID 区与单表约束 fail-loud
    // ------------------------------------------------------------------

    [Fact]
    public void FirstPid_Encroaching_Response_Pid_Range_Fails_Loud()
    {
        // FIRST_PID=0xF8：15 个 ODT 的 PID 区 (0xF8..0x106) 越过 DTO PID 上界
        // 0xFB，触到 0xFF/0xFE 响应区——接收线程按 DTO 首字节分流，规划必须拒绝。
        var map = () => Plan(m =>
        {
            m.Measurements = Uniform("B", A2lDataType.UBYTE, 8, 0x1000);
            m.DaqList = DaqList(firstPid: 0xF8, maxOdtEntries: 100);
        });

        Assert.Throws<InvalidOperationException>(map);
    }

    [Fact]
    public void NonZero_DaqList_Number_Fails_Loud()
    {
        // 单 DAQ 表约束（spec §1：MAX_DAQ=1，轮转只控 list 0）：DAQ_LIST_NUMBER≠0
        // 显式拒绝，禁止静默配成 list 0。
        var map = () => Plan(m =>
        {
            m.Measurements = Uniform("B", A2lDataType.UBYTE, 8, 0x1000);
            m.DaqList = DaqList(firstPid: 0, maxOdtEntries: 100, number: 1);
        });

        Assert.Throws<InvalidOperationException>(map);
    }

    // ------------------------------------------------------------------
    // (F2) MAX_ODT_ENTRY_SIZE_DAQ 声明值消费（对账前置，宁可不采）
    // ------------------------------------------------------------------

    [Fact]
    public void Declared_MaxOdtEntrySizeDaq_Two_Tightens_Size_Classes()
    {
        // 声明 2 → 尺寸类收紧为 {1,2}：4B 对象整类降级轮询（ObjectTooLarge），
        // 2B/1B 类照常装箱。
        var map = Plan(m =>
        {
            m.Measurements = new List<A2lMeasurement>(
                Uniform("D", A2lDataType.ULONG, 2, 0x1000)
                    .Concat(Uniform("W", A2lDataType.UWORD, 2, 0x1100))
                    .Concat(Uniform("B", A2lDataType.UBYTE, 2, 0x1200)));
            m.DaqList = DaqList(firstPid: 0, maxOdtEntries: 100);
            m.MaxOdtEntrySizeDaq = 2;
        });

        Assert.All(map.Odts, o => Assert.True(o.SizeClassBytes <= 2));
        Assert.All(map.Odts.SelectMany(o => o.Entries), e => Assert.True(e.ByteLength <= 2));
        var downgraded = map.PollingEntries.Where(e => e.ObjectName.StartsWith('D')).ToList();
        Assert.True(downgraded.Count == 2);
        Assert.All(downgraded, e => Assert.Equal(PlannedPollingCause.ObjectTooLarge, e.Cause));
    }

    [Fact]
    public void Missing_MaxOdtEntrySizeDaq_Fails_Loud()
    {
        // 声明 null = 装箱判据缺依据（对账前置）：拒绝规划，绝不按缺省 4 静默放行。
        var map = () => Plan(m =>
        {
            m.Measurements = Uniform("B", A2lDataType.UBYTE, 8, 0x1000);
            m.DaqList = DaqList(firstPid: 0, maxOdtEntries: 100);
            m.MaxOdtEntrySizeDaq = null;
        });

        Assert.Throws<InvalidOperationException>(map);
    }

    // ------------------------------------------------------------------
    // (F3) 判别性钉死：降级判据是 ODT 预算，不是 105 B 字节数
    // ------------------------------------------------------------------

    [Fact]
    public void SixteenFourByteObjects_Degraded_Despite_Bytes_Under_105B()
    {
        // 16×4B = 64B ≤ 105B，但需 16 个 ODT > 15 → 超预算的第 16 条降级
        // DaqCapacityExceeded。判据钉死为 ODT 预算；装满的 15 条照常打包
        //（部分装箱语义与 (b)/(c) 一致——整类降级会把它们一起清空）。
        var map = Plan(m =>
        {
            m.Measurements = Uniform("D", A2lDataType.ULONG, 16, 0x1000);
            m.DaqList = DaqList(firstPid: 0, maxOdtEntries: 100);
        });

        Assert.True(map.Odts.Count == MaxOdts);
        Assert.True(map.Odts.SelectMany(o => o.Entries).Sum(e => e.ByteLength) <= MaxOdts * OdtDataFieldBytes);
        Assert.True(map.PollingEntries.Count == 1);
        Assert.All(map.PollingEntries, e => Assert.Equal(PlannedPollingCause.DaqCapacityExceeded, e.Cause));
    }

    // ------------------------------------------------------------------
    // 测试脚手架（构造包侧 A2L 模型，与 a2l-editor 测试同构）
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // (T12 review F1) EXTENSION≠0 段 fail-loud：轮询与 DAQ 条目同守卫
    // ------------------------------------------------------------------

    [Fact]
    public void Segment_With_Nonzero_Address_Extension_Fails_Loud_At_Planning()
    {
        // DAQ 路径：4B 合格对象落在 EXTENSION≠0 段——构造期拒绝，消息含对象名与段名。
        var daqPath = Assert.Throws<InvalidOperationException>(() => Plan(new ModuleSpec
        {
            Measurements = { Meas("OK4B", A2lDataType.ULONG, 0x1000) },
            Layouts = new[] { UbyteLayout() },
            DaqList = DaqList(firstPid: 0, maxOdtEntries: 100),
            Segments = new[] { CalSegment(addressExtension: 3) },
        }));
        Assert.Contains("OK4B", daqPath.Message);
        Assert.Contains("CAL", daqPath.Message);
        Assert.Contains("3", daqPath.Message);

        // 轮询路径：>4B 对象降级轮询同样被守卫（宁可不采不错采）。
        var pollingPath = Assert.Throws<InvalidOperationException>(() => Plan(new ModuleSpec
        {
            Measurements = { Meas("BIG8", A2lDataType.FLOAT64_IEEE, 0x1000) },
            Layouts = new[] { UbyteLayout() },
            DaqList = DaqList(firstPid: 0, maxOdtEntries: 100),
            Segments = new[] { CalSegment(addressExtension: 3) },
        }));
        Assert.Contains("BIG8", pollingPath.Message);
        Assert.Contains("CAL", pollingPath.Message);
    }

    private sealed class ModuleSpec
    {
        public List<A2lMeasurement> Measurements = [];
        public A2lCharacteristic[] Characteristics = [];
        public A2lRecordLayout[] Layouts = [];
        public XcpDaqList? DaqList;
        public A2lMemorySegment[] Segments = [];

        // 包侧声明值（XcpDaq.MaxOdtEntrySizeDaq）；默认 4 = spec §1 实测基线。
        public uint? MaxOdtEntrySizeDaq = 4;
    }

    private static PlannedAcquisitionMap Plan(ModuleSpec spec)
    {
        // 两次独立构建（合同与占位索引都从同一份文档模型重建），确保 planner
        // 消费的是纯输入而非任何隐藏状态。
        var doc = Doc(spec);
        var contracts = new ContractSet(doc);
        var placeholders = AcquisitionPlan.Build(contracts);
        return AcquisitionPlanner.Plan(contracts, placeholders);
    }

    private static PlannedAcquisitionMap Plan(Action<ModuleSpec> configure)
    {
        var spec = new ModuleSpec();
        configure(spec);
        return Plan(spec);
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
        XcpIfData? ifData = null;
        if (spec.DaqList is not null)
        {
            var daq = new XcpDaq(
                Dynamic: false, MaxDaq: 1, MaxEventChannel: 1, MinDaq: null,
                OptimisationType: "STATIC", AddressExtension: "", IdentificationFieldType: "",
                GranularityOdtEntrySizeDaq: "", MaxOdtEntrySizeDaq: spec.MaxOdtEntrySizeDaq, OverloadIndication: false,
                new[] { spec.DaqList }, Array.Empty<XcpEventChannel>(),
                Array.Empty<XcpMissingField>(), "");
            ifData = new XcpIfData(XcpIfDataScope.ModuleLevel, null, daq, null, null,
                Array.Empty<XcpOnCan>(), Array.Empty<XcpSegment>(),
                Array.Empty<A2lUnknownBlock>(), Array.Empty<XcpMissingField>(), "");
        }

        return new A2lModule("M", "m",
            spec.Measurements, spec.Characteristics, Array.Empty<A2lAxisPts>(),
            new[] { CmIdentical }, spec.Layouts, Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1), MemorySegments: spec.Segments, IfDataXcp: ifData);
    }

    private static XcpDaqList DaqList(ushort firstPid, uint maxOdtEntries, uint number = 0) =>
        new(number, "DAQ", MaxOdts, maxOdtEntries, firstPid, 0, "");

    private static A2lCompuMethod CmIdentical =>
        new("CM_ID", "id", "IDENTICAL", "%d", "-", new IdenticalConversion(), new LineRange(1, 1));

    private static A2lMeasurement Meas(string name, A2lDataType dt, ulong addr) =>
        new(name, "d", dt, "CM_ID", "0", "0", "0", "65535", addr, new LineRange(1, 1));

    private static List<A2lMeasurement> Uniform(string prefix, A2lDataType dt, int count, ulong baseAddr) =>
        Enumerable.Range(0, count).Select(i => Meas($"{prefix}{i}", dt, baseAddr + (ulong)(i * 8))).ToList();

    private static A2lRecordLayout UbyteLayout() =>
        new("RL_U8",
            new[] { new RecordLayoutEntry("FNC_VALUES", 0, "UBYTE", "COLUMN_DIR", "DIRECT", null, null) },
            new LineRange(1, 1));

    private static A2lCharacteristic ValBlk3(string name, ulong addr) =>
        new(name, "d", "VAL_BLK", "RL_U8", addr, "0", "100", null, "CM_ID", new LineRange(1, 1),
            MatrixDim: new uint[] { 3, 1, 1 });

    private static A2lMemorySegment CalSegment(uint addressExtension = 0) =>
        new("CAL", "cal", "DATA", "FLASH", "INTERN", 0x1000, 0x1000,
            Array.Empty<string>(), new LineRange(1, 1),
            IfDataXcp: new XcpIfData(XcpIfDataScope.MemorySegmentLevel, null, null, null, null,
                Array.Empty<XcpOnCan>(),
                new[]
                {
                    new XcpSegment(0, 1, addressExtension, 0, 0,
                        new[]
                        {
                            new XcpAddressMapping(0x1000, 0x9000, 0x1000, Array.Empty<XcpMissingField>(), ""),
                        },
                        1, Array.Empty<XcpMissingField>(), ""),
                },
                Array.Empty<A2lUnknownBlock>(), Array.Empty<XcpMissingField>(), ""));
}
