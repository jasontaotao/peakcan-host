using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using A2lEditor.Core;
using PeakCan.HIL.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Capability;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Cli;

/// <summary>
/// xcp-probe 子命令参数（决策 D3：PeakCan.Host.Cli 子命令）。
/// <para>
/// CAN ID 必显式给出（--xcp-master-id / --xcp-slave-id，0x 前缀十六进制或十进制）——
/// <b>无默认兜底</b>：宁可拒绝运行也不静默猜 ID。A2L 声明的 CAN ID 只进事实清单
/// 与实测值比对（A-4 台架核实前为告警级，见 XcpCapabilityReconciler）。
/// </para>
/// </summary>
public sealed record XcpProbeOptions(
    string A2LPath,
    uint MasterCanIdRaw,
    uint SlaveCanIdRaw,
    string? OutputPath = null,
    string? HardwareChannel = null,
    TimeSpan? Timeout = null,
    int MaxRetries = XcpMasterOptions.DefaultMaxRetries);

/// <summary>
/// xcp-probe 最小握手探针（S2-T8，spec §4）：CONNECT → 能力查询全链
///（GET_COMM_MODE_INFO / GET_DAQ_PROCESSOR_INFO / GET_DAQ_RESOLUTION_INFO /
/// GET_DAQ_LIST_INFO / GET_DAQ_EVENT_INFO + OPTIONAL_CMD 逐命令探测）→
/// <see cref="XcpCapabilityReconciler"/> 对账 → 输出事实清单 JSON
///（A-1 能力 / A-2 事件节拍 / A-3 间隔抖动占位 / A-4 CAN 号合规占位 / A-5 ODT 打包上限）。
/// <para>
/// 退出码：0 = 对账允许启动；1 = 对账拒绝（事实清单仍输出——宁可不采不错采）；
/// 2 = 用法/协议错误（由 Program 统一捕获）。
/// 真机握手是 T19 人工验收项；CI 只跑模拟从机（Core.Tests 的 XcpVirtualSlave）。
/// <para>S3-T7b（D7 裁决）：实测链已下沉 Core XcpCapabilityProber，本命令只装配
/// master、转发调用并收尾 DISCONNECT——CLI 与 App VM 单源消费，零行为变化。</para>
/// </para>
/// </summary>
public static class XcpProbeCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>解析 xcp-probe 参数（子命令名之后的剩余参数）。缺 CAN ID / --a2l 即抛异常。</summary>
    public static XcpProbeOptions ParseArgs(string[] args)
    {
        string? a2l = null, output = null, hw = null, masterId = null, slaveId = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--a2l": a2l = NextArg(args, ref i, "--a2l"); break;
                case "--output": output = NextArg(args, ref i, "--output"); break;
                case "--hw": hw = NextArg(args, ref i, "--hw"); break;
                case "--xcp-master-id": masterId = NextArg(args, ref i, "--xcp-master-id"); break;
                case "--xcp-slave-id": slaveId = NextArg(args, ref i, "--xcp-slave-id"); break;
                default: throw new ArgumentException($"Unknown xcp-probe argument '{args[i]}'.");
            }
        }

        // D3 硬约束：CAN ID 必显式给出，无默认兜底。
        if (a2l is null)
            throw new ArgumentException("xcp-probe requires --a2l <path>.");
        if (masterId is null)
            throw new ArgumentException("xcp-probe requires --xcp-master-id <id> — no default fallback (D3).");
        if (slaveId is null)
            throw new ArgumentException("xcp-probe requires --xcp-slave-id <id> — no default fallback (D3).");

        return new XcpProbeOptions(
            a2l,
            ParseCanIdRaw(masterId, "--xcp-master-id"),
            ParseCanIdRaw(slaveId, "--xcp-slave-id"),
            output,
            hw);
    }

    /// <summary>
    /// 探针主流程（transport 由调用侧注入：测试传模拟从机，Program 传 XcpCanTransport）。
    /// 返回退出码：0 允许启动 / 1 对账拒绝（事实清单已输出）。
    /// </summary>
    public static async Task<int> RunAsync(XcpProbeOptions options, IXcpTransport transport, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);

        // ---- A2L 声明侧（按声明值走；能力对账不匹配即告警/拒绝，宁可不采不错采）----
        var declared = ParseDeclaration(options.A2LPath, out var validationNotes, out var document);

        // ---- XCP 会话：CONNECT → 能力实测链（S3-T7b / D7：链路下沉 Core
        // XcpCapabilityProber，CLI 与 App VM 同源消费；本命令只装配 master 与收尾断连）----
        var masterOptions = new XcpMasterOptions(
            new CanId(options.MasterCanIdRaw, FrameFormatOf(options.MasterCanIdRaw)),
            options.Timeout,
            options.MaxRetries);
        using var master = new XcpMaster(transport, masterOptions);
        var probe = await XcpCapabilityProber.ProbeAsync(master, options.MasterCanIdRaw, options.SlaveCanIdRaw, ct);

        var connect = probe.Connect;
        var commMode = probe.CommMode;
        var processor = probe.Processor;
        var resolution = probe.Resolution;
        var listInfo = probe.ListInfo;
        var eventInfo = probe.EventInfo;
        var measured = probe.Measured;
        var measuredCommands = measured.OptionalCommands;
        var queryFailures = probe.QueryFailures
            .Select(f => new XcpProbeQueryFailure(f.Command, f.ExceptionType, f.Marker))
            .ToList();

        // 收尾断连：强制命令，不入对账（A2L OPTIONAL_CMD 不含 CONNECT/DISCONNECT）。
        XcpResponseDecoder.Disconnect(await master.SendAsync(XcpCommandEncoder.Disconnect(), ct));

        var reconciliation = XcpCapabilityReconciler.Reconcile(declared, validationNotes, measured);

        // ---- 事实清单（spec §4：直接兑现附录 A-1/2/3/4/5 的探针侧字段）----
        // A-11 位域量统计（spec §4）：包侧可得口径最小化——解析期合同一次建好，只读计数。
        var bitfieldStatistics = BitfieldStatisticsOf(Asap2PackageApi.Contracts(document));

        var report = BuildReport(options, declared, connect, commMode, processor, resolution, listInfo, eventInfo, measured, measuredCommands, reconciliation, bitfieldStatistics, queryFailures);
        var json = JsonSerializer.Serialize(report, JsonOptions);

        if (options.OutputPath is { } outputPath)
            await File.WriteAllTextAsync(outputPath, json, ct);
        Console.WriteLine(json);

        return reconciliation.RejectedStart ? 1 : 0;
    }

    /// <summary>
    /// A2L 声明的 XCP_ON_CAN.BAUDRATE → 经典 CAN 预设（真机路径用，按声明值走不猜；
    /// 不在预设表 = 异常）。CI/测试路径不经过本方法（直接注入模拟从机 transport）。
    /// S3-T2 (D4)：逻辑已下沉 Core XcpA2lLoader.ResolveBaudRate，本方法只转发——
    /// probe 与 App VM 同源消费，杜绝双份解析路径漂移。
    /// </summary>
    public static BaudRate ResolveDeclaredBaudRate(string a2lPath)
    {
        var declared = ParseDeclaration(a2lPath, out _, out _);
        return XcpA2lLoader.ResolveBaudRate(declared);
    }

    /// <summary>
    /// S3-T2 (D4)：A2L 声明侧加载已下沉 Core XcpA2lLoader.Load（parse + cross-checks +
    /// IF_DATA 提取 + ContractSet 一次建齐），本方法只转发并把显式失败结果翻回探针
    /// 原有的 InvalidDataException 边界——CLI 退出码语义（Program 顶层 catch → 2）不变。
    /// 唯一例外：无 XCP_ON_CAN 块在波特率解析处现在抛 NotSupportedException 而非
    /// InvalidDataException（Core 口径"按声明值走不猜"）。退出码仍为 2（同一顶层 catch）；
    /// </summary>
    private static XcpIfData ParseDeclaration(
        string a2lPath, out IReadOnlyList<ValidationNote> validationNotes, out A2lDocument document)
    {
        var loaded = XcpA2lLoader.Load(a2lPath) switch
        {
            XcpA2lLoadResult.Loaded ok => ok,
            XcpA2lLoadResult.Failed failed => throw new InvalidDataException(failed.Message),
            _ => throw new UnreachableException("XcpA2lLoadResult 只有 Loaded/Failed 两态"),
        };

        validationNotes = loaded.ValidationNotes;
        document = loaded.Document;
        return loaded.IfData;
    }

    /// <summary>
    /// A-11 位域量统计（包侧可得口径最小化，spec §4 已同步降级）：包 API 刻意不上
    /// BitWidth/BitOffset（§5.6 判据 3：真机对象级 BIT_MASK 0 处，无来源的字段上了
    /// 接口就是骗下游）→ 位域专属计数无来源，BitMaskObjects 恒 null。
    /// <para>
    /// TotalByteLength 只能粗分聚合对象：≠1/2/4/8（与包 ByteLayout 标准宽度表一致，
    /// 含 FLOAT64 的 8B）的对象全是 CURVE/MAP/VAL_BLK 等聚合体，不是位域信号——
    /// 子字节位域在标准模型里就是 1 字节对象，此口径识别不了。位域数量只能台架手工
    /// 统计（A-11 降级口径，spec §4 那一句已同步修正）。
    /// </para>
    /// </summary>
    private static XcpProbeBitfieldStatisticsFacts BitfieldStatisticsOf(ContractSet contracts)
    {
        var all = contracts.All;
        return new XcpProbeBitfieldStatisticsFacts(
            TotalObjects: all.Count,
            NonByteAlignedObjects: all.Count(c => c.TotalByteLength is not (1 or 2 or 4 or 8)),
            BitMaskObjects: null,
            Status: "degraded-package-has-no-bit-model");
    }


    private static XcpProbeReport BuildReport(
        XcpProbeOptions options,
        XcpIfData declared,
        XcpConnectResponse connect,
        XcpGetCommModeInfoResponse commMode,
        XcpGetDaqProcessorInfoResponse processor,
        XcpGetDaqResolutionInfoResponse resolution,
        XcpGetDaqListInfoResponse listInfo,
        XcpGetDaqEventInfoResponse eventInfo,
        XcpMeasuredCapabilities measured,
        IReadOnlyList<string> measuredCommands,
        XcpCapabilityReport reconciliation,
        XcpProbeBitfieldStatisticsFacts bitfieldStatistics,
        List<XcpProbeQueryFailure> queryFailures)
    {
        var declaredOnCan = declared.OnCan.Count > 0 ? declared.OnCan[0] : null;
        var declaredEvent = declared.Daq is { } daq && daq.Events.Count > 0 ? daq.Events[0] : null;

        // A-5 ODT 打包上限：DTO 数据场硬上限 7B（DTO 8B − PID 1B，spec §1）；
        // 条目不可拆 → 每 ODT 至多 floor(7B / 条目尺寸) 个完整条目。
        var dtoPayloadCapBytes = XcpCtoFrame.MaxByteLength - 1;
        int? maxEntriesPerOdt = resolution.MaxOdtEntrySizeDaq > 0
            ? dtoPayloadCapBytes / resolution.MaxOdtEntrySizeDaq
            : null;

        // 真机路径占位（S2-T8 评审 Important-1）：任一能力查询失败 = 设备布局偏差已现身，
        // 输出 deviceLayout 指向 S1§15；CI 模拟从机全绿 → null。
        var deviceLayout = queryFailures.Count > 0 ? "nonconformant-see-S1§15" : null;

        return new XcpProbeReport(
            SchemaVersion: "1",
            A2LPath: options.A2LPath,
            CanIds: new XcpProbeCanIdFacts(options.MasterCanIdRaw, options.SlaveCanIdRaw),
            Connect: new XcpProbeConnectFacts(
                connect.ProtocolVersion, connect.TransportVersion, connect.Resources, connect.CommModeBasic, commMode.QueueSize),
            Measured: measured,
            EventPeriod: new XcpProbeEventPeriodFacts(
                eventInfo.EventCycle,
                eventInfo.EventChannelTimeUnit,
                measured.EventPeriodMicroseconds,
                declaredEvent is null ? null : declaredEvent.PeriodMicroseconds),
            DtoIntervalJitter: new XcpProbeJitterFacts(
                Status: "placeholder",
                Reason: "S2 has no clock synchronization (spec §1); slave timestampTicks=" +
                        $"{resolution.TimestampTicks} — DTO interval jitter is bench-only assessment (A-3 / T19).",
                MeasuredTimestampTicks: resolution.TimestampTicks),
            CanIdCompliance: new XcpProbeCanIdComplianceFacts(
                declaredOnCan?.MasterCanIdRaw,
                declaredOnCan?.SlaveCanIdRaw,
                options.MasterCanIdRaw,
                options.SlaveCanIdRaw,
                Status: "pending-bench-verification"),
            OdtPacking: new XcpProbeOdtPackingFacts(
                dtoPayloadCapBytes, resolution.MaxOdtEntrySizeDaq, maxEntriesPerOdt, listInfo.MaxOdt),
            MeasuredCommands: measuredCommands,
            BitfieldStatistics: bitfieldStatistics,
            Reconciliation: reconciliation,
            DeviceLayout: deviceLayout,
            QueryFailures: queryFailures);
    }

    private static FrameFormat FrameFormatOf(uint canIdRaw) =>
        canIdRaw > 0x7FF ? FrameFormat.Extended : FrameFormat.Standard;

    private static uint ParseCanIdRaw(string raw, string option)
    {
        var isHex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var number = isHex ? raw[2..] : raw;
        if (!uint.TryParse(
                number,
                isHex ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var id))
        {
            throw new ArgumentException($"Invalid CAN ID for {option}: '{raw}'.");
        }

        return id;
    }

    private static string NextArg(string[] args, ref int i, string option)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {option}.");
        i++;
        return args[i];
    }

}

/// <summary>探针事实清单（机读 JSON，供 T19 台架回填 diff；camelCase 落盘）。</summary>
public sealed record XcpProbeReport(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("a2lPath")] string A2LPath,
    XcpProbeCanIdFacts CanIds,
    XcpProbeConnectFacts Connect,
    XcpMeasuredCapabilities Measured,
    XcpProbeEventPeriodFacts EventPeriod,
    XcpProbeJitterFacts DtoIntervalJitter,
    XcpProbeCanIdComplianceFacts CanIdCompliance,
    XcpProbeOdtPackingFacts OdtPacking,
    XcpProbeBitfieldStatisticsFacts BitfieldStatistics,
    IReadOnlyList<string> MeasuredCommands,
    XcpCapabilityReport Reconciliation,
    /// <summary>设备布局偏差占位：任一能力查询失败即置位（真机偏差清单归 T19 台架核死）。</summary>
    string? DeviceLayout,
    IReadOnlyList<XcpProbeQueryFailure> QueryFailures);

/// <summary>单个能力查询失败归因（S2-T8 评审 Important-1：异常类型 + 预期偏差标记）。</summary>
public sealed record XcpProbeQueryFailure(string Command, string ExceptionType, string Marker);

/// <summary>探针实际使用的 CAN ID（命令显式给出值，无默认兜底）。</summary>
public sealed record XcpProbeCanIdFacts(uint MasterUsed, uint SlaveUsed)
{
    /// <summary>Bench-friendly hex form (A-4 audit); canonical value stays numeric.</summary>
    public string MasterUsedHex => Hex(MasterUsed);

    /// <summary>Bench-friendly hex form (A-4 audit).</summary>
    public string SlaveUsedHex => Hex(SlaveUsed);

    internal static string Hex(uint canId) => FormattableString.Invariant($"0x{canId:X}");
}

/// <summary>CONNECT / GET_COMM_MODE_INFO 基础事实（A-1 能力清单的握手层）。</summary>
public sealed record XcpProbeConnectFacts(
    byte ProtocolVersion,
    byte TransportVersion,
    byte Resources,
    byte CommModeBasic,
    ushort QueueSize);

/// <summary>A-2 事件节拍：线上原始字节（审计用）+ 两侧换算后的 µs（对账可比值）。</summary>
public sealed record XcpProbeEventPeriodFacts(
    byte MeasuredEventCycle,
    byte MeasuredWireTimeUnit,
    uint? MeasuredPeriodMicroseconds,
    uint? DeclaredPeriodMicroseconds);

/// <summary>A-3 DTO 间隔抖动：S2 无时钟同步，只能占位（时基精度评估归 T19 台架）。</summary>
public sealed record XcpProbeJitterFacts(string Status, string Reason, byte MeasuredTimestampTicks);

/// <summary>A-4 CAN 号合规：声明 vs 实际使用（掩码规则归 host，台架核实前占位）。</summary>
public sealed record XcpProbeCanIdComplianceFacts(
    uint? DeclaredMasterCanIdRaw,
    uint? DeclaredSlaveCanIdRaw,
    uint UsedMasterCanIdRaw,
    uint UsedSlaveCanIdRaw,
    string Status)
{
    /// <summary>Bench-friendly hex forms (A-4 audit); canonical values stay numeric.</summary>
    public string? DeclaredMasterCanIdHex => DeclaredMasterCanIdRaw is { } v ? XcpProbeCanIdFacts.Hex(v) : null;
    public string? DeclaredSlaveCanIdHex => DeclaredSlaveCanIdRaw is { } v ? XcpProbeCanIdFacts.Hex(v) : null;
    public string UsedMasterCanIdHex => XcpProbeCanIdFacts.Hex(UsedMasterCanIdRaw);
    public string UsedSlaveCanIdHex => XcpProbeCanIdFacts.Hex(UsedSlaveCanIdRaw);
}

/// <summary>A-5 ODT 打包上限：DTO 数据场 7B 硬上限下的条目/ODT 事实（规划器输入边界）。</summary>
public sealed record XcpProbeOdtPackingFacts(
    int DtoPayloadCapBytes,
    byte MeasuredMaxOdtEntrySizeDaq,
    int? MaxEntriesPerOdt,
    byte MeasuredMaxOdt);

/// <summary>
/// A-11 位域量统计（包侧可得口径最小化；位域专属计数不可得 → 降级，spec §4）。
/// </summary>
/// <param name="TotalObjects">全文档合同对象数（MEASUREMENT + CHARACTERISTIC + AXIS_PTS）。</param>
/// <param name="NonByteAlignedObjects">TotalByteLength ∉ {1,2,4,8} 的对象数——聚合对象
///（CURVE/MAP/VAL_BLK）口径，不能识别子字节位域（位域在标准模型里就是 1 字节）。</param>
/// <param name="BitMaskObjects">包 API 无 BIT_MASK 建模（§5.6 判据 3）——恒 null，台架手工统计。</param>
/// <param name="Status">降级标记：degraded-package-has-no-bit-model。</param>
public sealed record XcpProbeBitfieldStatisticsFacts(
    int TotalObjects,
    int NonByteAlignedObjects,
    int? BitMaskObjects,
    string Status);
