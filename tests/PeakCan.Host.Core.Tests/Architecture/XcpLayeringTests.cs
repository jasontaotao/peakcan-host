using System.Reflection;
using System.Reflection.Emit;
using NetArchTest.Rules;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Tests.Architecture;

/// <summary>
/// S2-T7 临时守卫 + S2-T18 正式守卫（T18 落地并入）：Core.Xcp 分层红线、
/// 协议层纯度、XcpAddressMap 唯一入口、DOWNLOAD 调度禁用的静态面
/// （spec §5 验收 5、§3、决策 D2）。
///
/// 与既有守卫的分工：
/// - tests/PeakCan.Host.Infrastructure.Tests/Architecture/LayeringRulesTests.cs：
///   全 Core 程序集粒度的 NetArchTest 规则（Core → System.Windows / Peak.Can.Basic）；
/// - 本文件首条测试：Core 程序集直接引用面检查（GetReferencedAssemblies）；
/// - 本文件其余规则：Xcp 命名空间粒度——违规可归因到具体 Xcp 类型，且宾语面更宽
///   （Peak.Can.* 前缀族、Infrastructure、App 的类型级依赖）。
/// </summary>
public class XcpLayeringTests
{
    private const string XcpNamespace = "PeakCan.Host.Core.Xcp";
    private const string ProtocolNamespace = "PeakCan.Host.Core.Xcp.Protocol";
    private const string SchedulingNamespace = "PeakCan.Host.Core.Xcp.Scheduling";
    private const string ReceiveNamespace = "PeakCan.Host.Core.Xcp.Receive";

    private static readonly Dictionary<byte, OpCode> SingleByteOpcodes = BuildOpcodeTable(size: 1);
    private static readonly Dictionary<byte, OpCode> TwoByteOpcodes = BuildOpcodeTable(size: 2);

    [Fact]
    public void Core_assembly_does_not_reference_drivers_ui_infrastructure_or_app()
    {
        // P0-1 修复（2026-09-06）：用本地 Core 程序集类型定位守卫目标。
        // 旧写法 typeof(PeakCan.HIL.Core.HIL.TestCase).Assembly 定位到外部
        // PeakCan.HIL.Core.dll（TestCase 在 sibling 包），守卫形同虚设。
        var assembly = typeof(PeakCan.Host.Core.Xcp.Capability.XcpCapabilityReconciler).Assembly;
        var refs = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain("Peak.Can.Basic", refs);
        Assert.DoesNotContain("System.Windows", refs);
        Assert.DoesNotContain("PeakCan.Host.Infrastructure", refs);
        Assert.DoesNotContain("PeakCan.Host.App", refs);
    }

    [Fact]
    public void Xcp_namespace_does_not_depend_on_drivers_wpf_or_infrastructure()
    {
        // S2-T18 (a)：Xcp 命名空间粒度红线（spec §5 验收 5）。
        // 分工说明：LayeringRulesTests 已在"全 Core"粒度封禁 Peak.Can.Basic /
        // System.Windows；本条按 T18 计划在 Xcp 命名空间粒度补齐——宾语更宽
        // （"Peak.Can" 前缀覆盖 Basic 之外的 Peak.Can.* 族）并新增
        // Infrastructure / App 的类型级封禁（引用面检查只看得见直接引用，
        // NetArchTest 能看见对已引用程序集内类型的真实使用）。
        var core = typeof(AcquisitionPlanner).Assembly;

        // 非空守卫（双层）：1) 本地枚举确认 Xcp 子命名空间树非空——Xcp 树被
        // 整体搬走/改名时这里先炸；2) NetArchTest 依赖检测锚点：AcquisitionPlanner
        // （位于 Xcp.Scheduling 子命名空间）依赖 XcpSegment 是 T10 已验证事实。
        // 注：NetArchTest 1.3.2 的 ResideInNamespace 为前缀匹配，嵌套子命名空间
        // 全部入选（本测试首次落地时以失败输出的选中列表实证过）。
        Assert.Contains(core.GetTypes(), t => t.Namespace == SchedulingNamespace);
        var selectionGuard = Types.InAssembly(core)
            .That().ResideInNamespace(XcpNamespace)
            .And().HaveName("AcquisitionPlanner")
            .Should().HaveDependencyOn("A2lEditor.Core.IfData.XcpSegment")
            .GetResult();
        Assert.True(selectionGuard.IsSuccessful,
            "Xcp namespace selection or dependency detection is broken (AcquisitionPlanner must read XcpSegment): " +
            string.Join(", ", selectionGuard.FailingTypeNames ?? Array.Empty<string>()));

        foreach (var banned in new[]
                 {
                     "Peak.Can",
                     "System.Windows",
                     "PeakCan.Host.Infrastructure",
                     "PeakCan.Host.App",
                 })
        {
            var result = Types.InAssembly(core)
                .That().ResideInNamespace(XcpNamespace)
                .ShouldNot().HaveDependencyOn(banned)
                .GetResult();
            Assert.True(result.IsSuccessful,
                $"{banned}: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
        }
    }

    [Fact]
    public void Xcp_Record_namespace_dependency_face_is_pinned()
    {
        // S4-T7（S4 plan）：记录面依赖 = Core.Xcp.Receive（样本/gap 契约）+
        // Core.Xcp.Scheduling（PlannedDaqEntry）+ A2lEditor.Core.Layout（快照面，
        // spec D2）+ BCL。禁 Uds/HIL/Infrastructure/App/驱动/WPF——记录面永远
        // 不该长出诊断或驱动依赖（S3 红线延续：包与内核零 API 变更）。
        var core = typeof(PeakCan.Host.Core.Xcp.Record.XcpMdfRecordSink).Assembly;

        // 非空守卫：记录 sink 确实依赖 Receive 面——依赖检测机器坏了这里先炸，
        // 下面的封禁不会对空集 vacuous pass（照 Scheduling 锚点先例）。
        var anchor = Types.InAssembly(core)
            .That().HaveName("XcpMdfRecordSink")
            .Should().HaveDependencyOn(ReceiveNamespace)
            .GetResult();
        Assert.True(anchor.IsSuccessful,
            "Record namespace selection or dependency detection is broken (XcpMdfRecordSink must consume XcpDaqSample): " +
            string.Join(", ", anchor.FailingTypeNames ?? Array.Empty<string>()));

        foreach (var banned in new[]
                 {
                     "PeakCan.Host.Core.Uds",
                     "PeakCan.Host.Core.HIL",
                     "PeakCan.Host.Infrastructure",
                     "PeakCan.Host.App",
                     "Peak.Can",
                     "System.Windows",
                 })
        {
            var result = Types.InAssembly(core)
                .That().ResideInNamespace("PeakCan.Host.Core.Xcp.Record")
                .ShouldNot().HaveDependencyOn(banned)
                .GetResult();
            Assert.True(result.IsSuccessful,
                $"{banned}: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
        }
    }

    [Fact]
    public void Xcp_Scheduling_does_not_touch_package_segment_address_types()
    {
        // S2-T10 (e)：地址换算唯一入口是包侧 XcpAddressMap.TryTranslate（spec [H1]）。
        // Scheduling 命名空间禁止引用 XcpAddressMapping（Logical/Physical/Length——
        // 地址算术载体，全量封禁）；谁引用了它，谁就能重建第二套换算。
        // ValueSegment.Address 是 [H1] 钦定的翻译输入，合法引用，不在此列。
        //
        // T12 review F1 修订（窄幅收窄，评审定案）：EXTENSION≠0 段 fail-loud 守卫
        // （AcquisitionPlanner.GuardAddressExtension）需要读包侧声明元数据
        // XcpSegment.AddressExtension 与段名 A2lMemorySegment.Name——这是只读声明
        // 检查、零地址算术，覆盖判定仍全权走包侧 SegmentsCovering。故
        // XcpSegment / A2lMemorySegment 从"全量封禁"收窄为"仅 AcquisitionPlanner
        // 一个类型可依赖"（其余 Scheduling 类型维持封禁，防扩散）。
        var core = typeof(AcquisitionPlanner).Assembly;

        // 非空守卫：过滤器必须真的选中 Scheduling 类型（防 vacuous pass）。
        Assert.Contains(core.GetTypes(), t => t.Namespace == "PeakCan.Host.Core.Xcp.Scheduling");

        // 宾语侧守卫：封禁串与包侧类型全名逐一比对——包侧重命名时这里先炸，
        // 而不是 HaveDependencyOn 对不存在的名字永远空通过。
        // XcpAddressMapping：地址算术载体，Scheduling 全量封禁（T10 原语义不变）。
        Assert.Equal("A2lEditor.Core.IfData.XcpAddressMapping", typeof(A2lEditor.Core.IfData.XcpAddressMapping).FullName);
        var mappingBan = Types.InAssembly(core)
            .That().ResideInNamespace("PeakCan.Host.Core.Xcp.Scheduling")
            .ShouldNot().HaveDependencyOn("A2lEditor.Core.IfData.XcpAddressMapping")
            .GetResult();
        Assert.True(mappingBan.IsSuccessful,
            "XcpAddressMapping: " + string.Join(", ", mappingBan.FailingTypeNames ?? Array.Empty<string>()));

        // XcpSegment / A2lMemorySegment：声明元数据，仅 AcquisitionPlanner 可依赖（T12 review F1）。
        // DoNotHaveName 排除守卫本体后维持全量封禁（防依赖面扩散）。
        foreach (var metadataType in new[]
                 {
                     "A2lEditor.Core.IfData.XcpSegment",
                     "A2lEditor.Core.Model.A2lMemorySegment",
                 })
        {
            var metadataBan = Types.InAssembly(core)
                .That().ResideInNamespace("PeakCan.Host.Core.Xcp.Scheduling")
                .And().DoNotHaveName("AcquisitionPlanner")
                .ShouldNot().HaveDependencyOn(metadataType)
                .GetResult();
            Assert.True(metadataBan.IsSuccessful,
                $"{metadataType}: {string.Join(", ", metadataBan.FailingTypeNames ?? Array.Empty<string>())}");
        }

        // 非空守卫：F1 守卫本体必须真实依赖声明元数据（守卫被移走/改名时这里先炸，
        // 防 carve-out 退化成 vacuous pass）。
        var plannerUsesMetadata = Types.InAssembly(core)
            .That().HaveName("AcquisitionPlanner")
            .Should().HaveDependencyOn("A2lEditor.Core.IfData.XcpSegment")
            .GetResult();
        Assert.True(plannerUsesMetadata.IsSuccessful,
            "AcquisitionPlanner no longer reads XcpSegment.AddressExtension — " +
            "the T12 review F1 EXTENSION guard is gone or renamed; revisit this carve-out.");
    }
    [Fact]
    public void Xcp_Receive_does_not_touch_package_segment_address_types()
    {
        // S2-T14-review M1：Receive 命名空间兑现 T14 到期承诺——反查只准走
        // planner 自产 PlannedAcquisitionMap（spec [H2]），地址算术载体
        // XcpAddressMapping 全量封禁（与 Scheduling 守卫同一宾语）。
        var core = typeof(PeakCan.Host.Core.Xcp.Receive.XcpReceiveLoop).Assembly;

        // 非空守卫：过滤器必须真的选中 Receive 类型（防 vacuous pass；
        // XcpReceiveLoop 为非空锚）。
        Assert.Contains(core.GetTypes(), t => t.Namespace == "PeakCan.Host.Core.Xcp.Receive");

        // 宾语侧守卫：包侧重命名时这里先炸，而不是 HaveDependencyOn 对
        // 不存在的名字永远空通过（与 Scheduling 守卫同型）。
        Assert.Equal("A2lEditor.Core.IfData.XcpAddressMapping", typeof(A2lEditor.Core.IfData.XcpAddressMapping).FullName);
        var mappingBan = Types.InAssembly(core)
            .That().ResideInNamespace("PeakCan.Host.Core.Xcp.Receive")
            .ShouldNot().HaveDependencyOn("A2lEditor.Core.IfData.XcpAddressMapping")
            .GetResult();
        Assert.True(mappingBan.IsSuccessful,
            "XcpAddressMapping: " + string.Join(", ", mappingBan.FailingTypeNames ?? Array.Empty<string>()));
    }

    [Fact]
    public void Xcp_protocol_does_not_depend_on_scheduling_or_receive()
    {
        // S2-T18 (b)：协议层纯度——Protocol 只做字节级编解码与命令时序，
        // 禁止反向依赖上层（Scheduling / Receive），防止协议层长出调度逻辑。
        var core = typeof(XcpPid).Assembly;

        // 宾语侧守卫：命名空间改名时这里先炸（防 HaveDependencyOn 对不存在的
        // 名字永远空通过）。
        Assert.Equal(ProtocolNamespace, typeof(XcpPid).Namespace);
        Assert.Equal(SchedulingNamespace, typeof(AcquisitionPlanner).Namespace);
        Assert.Equal(ReceiveNamespace, typeof(PeakCan.Host.Core.Xcp.Receive.XcpReceiveLoop).Namespace);

        // 非空守卫：NetArchTest 依赖检测锚点——XcpMaster 以 IXcpTransport 字段
        // 绑定传输层是当前事实（Should() 要求选中集合全部满足，故收窄到
        // XcpMaster 本体）；Protocol 被清空/搬走时这里先炸。
        var selectionGuard = Types.InAssembly(core)
            .That().ResideInNamespace(ProtocolNamespace)
            .And().HaveName("XcpMaster")
            .Should().HaveDependencyOn("PeakCan.Host.Core.Xcp.Abstractions.IXcpTransport")
            .GetResult();
        Assert.True(selectionGuard.IsSuccessful,
            "Protocol namespace filter selected nothing (or XcpMaster no longer binds IXcpTransport): " +
            string.Join(", ", selectionGuard.FailingTypeNames ?? Array.Empty<string>()));

        foreach (var banned in new[] { SchedulingNamespace, ReceiveNamespace })
        {
            var result = Types.InAssembly(core)
                .That().ResideInNamespace(ProtocolNamespace)
                .ShouldNot().HaveDependencyOn(banned)
                .GetResult();
            Assert.True(result.IsSuccessful,
                $"{banned}: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
        }
    }

    [Fact]
    public void Xcp_address_translation_unique_entry_is_pinned()
    {
        // S2-T18 (c)：XcpAddressMap 唯一入口的查缺补漏（spec [H1]/[H2]）。
        // 既有守卫（出处，本文件上方两条原样保留）：
        //   S2-T10 (e) —— Scheduling 禁 XcpAddressMapping（地址算术载体全量封禁）；
        //   T12 review F1 —— XcpSegment/A2lMemorySegment 仅 AcquisitionPlanner 可依赖；
        //   S2-T14-review M1 —— Receive 禁 XcpAddressMapping。
        // 本条补两个缺口：
        //   1) 正向钉死唯一入口真的被使用：AcquisitionPlanner 必须依赖包侧
        //      XcpAddressMap（TryTranslate/SegmentsCovering）——T10/T14 的负向封禁
        //      抓不到"什么都不做"的退化（如换算被整体删除、物理地址静默变 null）。
        //   2) Receive 侧补齐 XcpAddressMap 封禁：Receive 的地址语义只来自 planner
        //      自产 PlannedAcquisitionMap（[H2]），绕过它直连包侧换算 = 重建映射链。
        //      注意 "XcpAddressMap" 前缀按 NetArchTest 前缀匹配同时覆盖
        //      XcpAddressMapping（T14 守卫的宾语），两条叠加是纵深而非重复。
        var core = typeof(AcquisitionPlanner).Assembly;

        // 宾语侧守卫：包侧重命名时这里先炸。
        Assert.Equal("A2lEditor.Core.IfData.XcpAddressMap", typeof(A2lEditor.Core.IfData.XcpAddressMap).FullName);

        // 缺口 1：唯一入口必须被 planner 真实使用。
        var plannerUsesEntry = Types.InAssembly(core)
            .That().ResideInNamespace(SchedulingNamespace)
            .And().HaveName("AcquisitionPlanner")
            .Should().HaveDependencyOn("A2lEditor.Core.IfData.XcpAddressMap")
            .GetResult();
        Assert.True(plannerUsesEntry.IsSuccessful,
            "AcquisitionPlanner no longer uses the package XcpAddressMap (TryTranslate/SegmentsCovering) — " +
            "the [H1] unique entry is bypassed or the package renamed; revisit translation wiring.");

        // 缺口 2（正向）：Receive 的反查必须走 planner 自产映射，而不是自己换算。
        // （Should() 要求选中集合全部满足，故收窄到消费映射的 XcpReceiveLoop 本体。）
        var receiveUsesPlannerMap = Types.InAssembly(core)
            .That().ResideInNamespace(ReceiveNamespace)
            .And().HaveName("XcpReceiveLoop")
            .Should().HaveDependencyOn("PeakCan.Host.Core.Xcp.Scheduling.PlannedAcquisitionMap")
            .GetResult();
        Assert.True(receiveUsesPlannerMap.IsSuccessful,
            "Receive no longer reverse-looks-up via planner PlannedAcquisitionMap ([H2] broken): " +
            string.Join(", ", receiveUsesPlannerMap.FailingTypeNames ?? Array.Empty<string>()));

        // 缺口 2（负向）：Receive 禁直连包侧地址换算面。
        var receiveBan = Types.InAssembly(core)
            .That().ResideInNamespace(ReceiveNamespace)
            .ShouldNot().HaveDependencyOn("A2lEditor.Core.IfData.XcpAddressMap")
            .GetResult();
        Assert.True(receiveBan.IsSuccessful,
            $"XcpAddressMap*: {string.Join(", ", receiveBan.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Xcp_scheduling_and_receive_never_reference_download_command()
    {
        // S2-T18 (d)：DOWNLOAD 调度禁用的静态面（spec §0/§3、决策 D2）。
        //
        // 判别力说明：XcpPid.Download 是 const——编译期常量内联为 ldc.i4 0xF0，
        // 不产生对 XcpPid 类型的依赖，NetArchTest 的
        // HaveDependencyOn("...XcpPid") 根本抓不到"调度层引用 DOWNLOAD 命令码"
        // 这一主形态；而 XcpCommandEncoder 类型本身是调度层合法依赖
        // （StartStopDaqList/SetDaqPtr/WriteDaq 都要从它编帧），也无法在类型级封禁。
        // 故本条做成员级 IL 扫描，两条泄漏形态都抓：
        //   1) ldc.i4 0xF0 —— 覆盖 XcpPid.Download 的内联引用与散写 0xF0 字面量；
        //   2) call → XcpCommandEncoder.Download —— 覆盖走编码器的调用路径。
        // 已知代价（刻意收紧）：Scheduling/Receive 的 IL 中任何 0xF0（240）立即数
        // 都会命中——当前代码无此立即数（含 cctor）；未来若确需 240（如缓冲区
        // 长度），必须挪进 Protocol 或改写算式。"调度层 IL 不出现 DOWNLOAD 命令码"
        // 是比"没有 DOWNLOAD 调用"更强的静态面。
        var core = typeof(XcpPid).Assembly;
        var encoderType = typeof(XcpCommandEncoder);

        // 宾语侧守卫：判别基准是 DOWNLOAD 命令码本身（0xF0），不是猜的魔法数。
        Assert.Equal(ProtocolNamespace, typeof(XcpPid).Namespace);
        Assert.Equal(ProtocolNamespace, encoderType.Namespace);
        Assert.Equal(0xF0, XcpPid.Download);

        // 非空守卫（扫得动）：IL 解码 + 成员解析必须能在 Scheduling 找到一次对
        // XcpCommandEncoder.StartStopDaqList 的真实调用（RotationScheduler 是当前
        // 事实）——扫描器坏了（opcode 表错位、token 解析失效）这里先炸，
        // 下面的封禁不会对空集 vacuous pass。
        var scanProbe = ScanSchedulingReceive(core, encoderType, "StartStopDaqList", literalValue: null);
        Assert.True(scanProbe.Count > 0,
            "IL scanner found no StartStopDaqList call in Scheduling — the member-level scan machinery " +
            "is broken; the DOWNLOAD ban below would vacuously pass.");

        var downloadHits = ScanSchedulingReceive(core, encoderType, "Download", XcpPid.Download);
        Assert.True(downloadHits.Count == 0,
            "DOWNLOAD leaked into scheduling/receive (encoder call or 0xF0 command-code literal): " +
            string.Join(", ", downloadHits));
    }

    /// <summary>
    /// Member-level IL scan over the Scheduling/Receive namespaces of the given
    /// assembly: flags methods that either call the named member on
    /// <paramref name="declaringType"/> or load <paramref name="literalValue"/> as
    /// an ldc.i4 operand (compiler-inlined const usage).
    /// </summary>
    private static List<string> ScanSchedulingReceive(
        Assembly assembly, Type declaringType, string memberName, int? literalValue)
    {
        var hits = new List<string>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.Namespace is not (SchedulingNamespace or ReceiveNamespace))
                continue;

            var methods = type
                .GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.Instance | BindingFlags.Static)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Public |
                                             BindingFlags.NonPublic | BindingFlags.Instance |
                                             BindingFlags.Static));

            foreach (var method in methods)
            {
                var body = method.GetMethodBody();
                if (body is null)
                    continue;

                if (ScanMethodBody(method, body.GetILAsByteArray()!, declaringType, memberName, literalValue))
                    hits.Add($"{type.FullName}::{method.Name}");
            }
        }

        return hits;
    }

    private static bool ScanMethodBody(
        MethodBase method, byte[] il, Type declaringType, string memberName, int? literalValue)
    {
        var pos = 0;
        while (pos < il.Length)
        {
            var op = ReadOpCode(il, ref pos);
            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.InlineSwitch:
                    pos += 4 + 4 * BitConverter.ToInt32(il, pos);
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    pos += 1;
                    break;
                case OperandType.InlineVar:
                    pos += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    pos += 8;
                    break;
                default:
                    // 4-byte operand family: InlineBrTarget/Field/I/Method/Sig/String/Tok/Type/ShortInlineR.
                    if (literalValue.HasValue && op == OpCodes.Ldc_I4 &&
                        BitConverter.ToInt32(il, pos) == literalValue.Value)
                        return true;

                    if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
                    {
                        var token = BitConverter.ToInt32(il, pos);
                        try
                        {
                            var target = method.Module.ResolveMethod(token);
                            if (target?.DeclaringType == declaringType && target.Name == memberName)
                                return true;
                        }
                        catch (Exception) // 解析失败不算命中（MissingMethod/BadImageFormat 同类）；探针锚保证解析器坏了不会静默
                        {
                            // Unresolvable tokens are not treated as hits; the
                            // StartStopDaqList probe in the rule guards the
                            // machinery itself.
                        }
                    }

                    pos += 4;
                    break;
            }
        }

        return false;
    }

    private static OpCode ReadOpCode(byte[] il, ref int pos)
    {
        var first = il[pos++];
        return first != 0xFE
            ? SingleByteOpcodes[first]
            : TwoByteOpcodes[il[pos++]];
    }

    private static Dictionary<byte, OpCode> BuildOpcodeTable(int size) => typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .Where(op => op.Size == size)
        // ToDictionary 对重复 opcode 值抛异常——故意 fail loud，防未来 .NET 表变化被静默吞。
        .ToDictionary(op => size == 1 ? unchecked((byte)op.Value) : (byte)(op.Value & 0xFF));
}
