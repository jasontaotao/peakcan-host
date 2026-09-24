using A2lEditor.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Model;
using A2lEditor.Core.Parsing;
using PeakCan.Host.Core.Xcp.Capability;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Tests.Xcp.Capability;

/// <summary>
/// CapabilityReconciler 能力对账引擎（S2-T7，spec §3 Capability / §5 验收 3）。
/// 对账输入 = A2L 声明值（XcpProtocolLayer/XcpDaq/XcpOnCan/XcpSegment，含各自
/// Missing）+ CollectCrossChecks 的 ValidationNote + XcpIfData.Unmodelled；
/// 对账对象 = 声明 vs XcpVirtualSlave / 真实 CONNECT+GET_DAQ_*_INFO 响应解码值。
/// 分级标准（本任务钉死，宁可不采不错采）：
///   拒绝启动 = 硬约束字段（MAX_DAQ/MAX_CTO/MAX_DTO/MAX_ODT/事件周期/
///   MAX_ODT_ENTRY_SIZE_DAQ 非空值）声明 vs 实测不一致，或硬字段无法核对
///   （声明 null / 实测缺 / 声明缺 S2 必需命令）；
///   告警 = ValidationNote 全部、Unmodelled 非空、Missing 留痕、
///   MAX_ODT_ENTRY_SIZE_DAQ 未声明、CAN ID 不一致（A-4 待核实）。
/// </summary>
public class CapabilityReconcilerTests
{
    private static readonly int[] CrossCheckLines = [827];

    /// <summary>真机 OPTIONAL_CMD 清单（App_merge_INCA.a2l 行 760-771，含别名 SET_DAQ_LIST_MODE）。</summary>
    private static readonly string[] RealOptionalCommands =
    [
        "GET_COMM_MODE_INFO", "SET_MTA", "UPLOAD", "SHORT_UPLOAD",
        "SET_DAQ_PTR", "WRITE_DAQ", "CLEAR_DAQ_LIST",
        "START_STOP_DAQ_LIST", "SET_DAQ_LIST_MODE", "START_STOP_SYNCH",
        "GET_DAQ_PROCESSOR_INFO", "GET_DAQ_RESOLUTION_INFO", "GET_DAQ_LIST_INFO",
        "GET_DAQ_EVENT_INFO", "DOWNLOAD",
    ];

    private static XcpIfData BuildBaselineDeclaration(
        string[]? optionalCommands = null,
        uint? maxOdtEntrySizeDaq = 4,
        uint periodMicroseconds = 10000,
        uint maxCto = 8,
        uint maxDto = 8,
        uint maxDaq = 1,
        uint maxOdt = 0x0F)
    {
        var protocolLayer = new XcpProtocolLayer(
            SourceVersion: string.Empty, XcpVersion: 0x0100,
            T1Ms: 2000, T2Ms: 10000, T3Ms: 0, T4Ms: 0, T5Ms: 0, T6Ms: 0, T7Ms: 0,
            MaxCto: maxCto, MaxDto: maxDto,
            ByteOrder: XcpByteOrder.MsbFirst, AddressGranularity: XcpAddressGranularity.Byte,
            OptionalCommands: optionalCommands ?? RealOptionalCommands,
            BlockModeSupportedBySlave: true, BlockModeSupportedByMaster: false,
            MaxBlockSize: null, MinStPin: null,
            ByteOrderLine: 0,
            Missing: Array.Empty<XcpMissingField>(), SourceText: string.Empty);

        var daqList = new XcpDaqList(
            Number: 0, Direction: "DAQ", MaxOdt: maxOdt, MaxOdtEntries: 0x64,
            FirstPid: 0x00, EventFixed: 0x00, SourceText: string.Empty);

        // TIME_CYCLE=0x0A、TIME_UNIT=0x06（本文件 A2ML UNIT_1MS=6）→ 10 × 1000 µs = 10000 µs。
        var eventChannel = new XcpEventChannel(
            Name: "Event0", ShortName: "Event0", Number: 0, Direction: "DAQ",
            MaxDaqList: 1, TimeCycle: 0x0A, TimeUnitCode: 0x06, Priority: 0,
            PeriodMicroseconds: periodMicroseconds,
            TimeUnitBasis: "A2ML EVENT.TIME_UNIT (UNIT_1MS=6)");

        var daq = new XcpDaq(
            Dynamic: false, MaxDaq: maxDaq, MaxEventChannel: 1, MinDaq: 0,
            OptimisationType: "OPTIMISATION_TYPE_DEFAULT",
            AddressExtension: "ADDRESS_EXTENSION_FREE",
            IdentificationFieldType: "IDENTIFICATION_FIELD_TYPE_ABSOLUTE",
            GranularityOdtEntrySizeDaq: "GRANULARITY_ODT_ENTRY_SIZE_DAQ_BYTE",
            MaxOdtEntrySizeDaq: maxOdtEntrySizeDaq,
            OverloadIndication: false,
            Lists: new[] { daqList },
            Events: new[] { eventChannel },
            Missing: Array.Empty<XcpMissingField>(), SourceText: string.Empty);

        var onCan = new XcpOnCan(
            CanVersion: 0x0100, MasterCanIdRaw: 0x98FFF666, SlaveCanIdRaw: 0x98FFF667,
            Baudrate: 500000, SamplePoint: 0x4B, SampleRate: "SINGLE",
            BtlCycles: null, Sjw: null, SyncEdge: "SINGLE", MaxDlcRequired: false,
            MasterCanIdLine: 0, SlaveCanIdLine: 0,
            Missing: Array.Empty<XcpMissingField>(), SourceText: string.Empty);

        var segment = new XcpSegment(
            Number: 0, NumberOfPages: 2, AddressExtension: 0, Compression: 0, Encryption: 0,
            Mappings: Array.Empty<XcpAddressMapping>(), NumberOfPagesLine: 0,
            Missing: Array.Empty<XcpMissingField>(), SourceText: string.Empty);

        return new XcpIfData(
            Scope: XcpIfDataScope.ModuleLevel,
            ProtocolLayer: protocolLayer,
            Daq: daq,
            Pag: null, Pgm: null,
            OnCan: new[] { onCan },
            Segments: new[] { segment },
            Unmodelled: Array.Empty<A2lUnknownBlock>(),
            Missing: Array.Empty<XcpMissingField>(),
            SourceText: string.Empty);
    }

    /// <summary>
    /// 实测能力基线 = XcpGoldenSamples（模拟从机默认应答）经 XcpResponseDecoder 解码的值。
    /// 事件周期 10000 µs（100 Hz）为黄金样本 (EVENT_CYCLE=0x0A, TIME_UNIT=0x06) 经
    /// XcpWireTimeUnit（线上表 6=1ms）换算的结果——对账只消费 µs（spec：不得直比线上字节值）。
    /// </summary>
    private static XcpMeasuredCapabilities BuildBaselineMeasured(
        ushort maxDaq = 1, byte maxCto = 8, byte maxDto = 8, byte maxOdt = 0x0F,
        uint? eventPeriodMicroseconds = 10000,
        string[]? optionalCommands = null)
        => new(
            MaxDaq: maxDaq,
            MaxEventChannel: 1,
            MinDaq: 0,
            MaxOdt: maxOdt,
            MaxCto: maxCto,
            MaxDto: maxDto,
            MaxOdtEntrySizeDaq: 4,
            EventPeriodMicroseconds: eventPeriodMicroseconds,
            OptionalCommands: optionalCommands ?? RealOptionalCommands,
            SlaveCanIdRaw: 0x18FFF666,   // XcpVirtualSlave.DefaultSlaveCanId（host 侧实际使用值）
            MasterCanIdRaw: 0x18FFF667);

    private static XcpCapabilityReport Reconcile(
        XcpIfData? declared, XcpMeasuredCapabilities measured,
        IReadOnlyList<ValidationNote>? notes = null)
        => XcpCapabilityReconciler.Reconcile(declared, notes ?? Array.Empty<ValidationNote>(), measured);

    // ---- (a) 声明 vs 实测一致 → 通过报告 ----

    [Fact]
    public void Consistent_declaration_and_measurement_allows_start_without_rejects()
    {
        var report = Reconcile(BuildBaselineDeclaration(), BuildBaselineMeasured());

        Assert.False(report.RejectedStart);
        Assert.DoesNotContain(report.Findings, f => f.Severity == XcpCapabilitySeverity.Reject);
    }

    // ---- (b) 篡改声明 / 篡改响应 → 硬字段不一致必须拒绝（宁可不采不错采）----

    [Fact]
    public void Tampered_declaration_max_daq_is_rejected()
    {
        var declared = BuildBaselineDeclaration();
        // 篡改声明侧 MAX_DAQ：1 → 5（实测仍为黄金样本 1）。
        var tampered = declared.Daq! with { MaxDaq = 5 };
        declared = declared with { Daq = tampered };

        var report = Reconcile(declared, BuildBaselineMeasured());

        Assert.True(report.RejectedStart);
        Assert.Contains(report.Findings, f =>
            f.Severity == XcpCapabilitySeverity.Reject && f.Code == "MAX_DAQ_MISMATCH");
    }

    [Fact]
    public void Tampered_response_max_cto_is_rejected()
    {
        var report = Reconcile(BuildBaselineDeclaration(), BuildBaselineMeasured(maxCto: 4));

        Assert.True(report.RejectedStart);
        Assert.Contains(report.Findings, f =>
            f.Severity == XcpCapabilitySeverity.Reject && f.Code == "MAX_CTO_MISMATCH");
    }

    [Fact]
    public void Tampered_response_max_dto_is_rejected()
    {
        var report = Reconcile(BuildBaselineDeclaration(), BuildBaselineMeasured(maxDto: 16));

        Assert.True(report.RejectedStart);
        Assert.Contains(report.Findings, f =>
            f.Severity == XcpCapabilitySeverity.Reject && f.Code == "MAX_DTO_MISMATCH");
    }

    [Fact]
    public void Tampered_response_event_period_is_rejected()
    {
        var report = Reconcile(BuildBaselineDeclaration(), BuildBaselineMeasured(eventPeriodMicroseconds: 20000));

        Assert.True(report.RejectedStart);
        Assert.Contains(report.Findings, f =>
            f.Severity == XcpCapabilitySeverity.Reject && f.Code == "EVENT_PERIOD_MISMATCH");
    }

    [Fact]
    public void Tampered_response_max_odt_is_rejected()
    {
        var report = Reconcile(BuildBaselineDeclaration(), BuildBaselineMeasured(maxOdt: 0x03));

        Assert.True(report.RejectedStart);
        Assert.Contains(report.Findings, f =>
            f.Severity == XcpCapabilitySeverity.Reject && f.Code == "MAX_ODT_MISMATCH");
    }

    // ---- (c) PeriodMicroseconds == 0 → 对账失败（TIME_UNIT 两套编号体系坑）----

    [Fact]
    public void Zero_declared_period_microseconds_fails_reconciliation()
    {
        var declared = BuildBaselineDeclaration(periodMicroseconds: 0);

        var report = Reconcile(declared, BuildBaselineMeasured());

        Assert.True(report.RejectedStart);
        Assert.Contains(report.Findings, f =>
            f.Severity == XcpCapabilitySeverity.Reject && f.Code == "EVENT_PERIOD_UNCONVERTED");
    }

    // ---- (c2) F6 parked：病态声明值 sanity——声明 0 本身非法，直接拒绝，不产出失真 MISMATCH ----
    // 值域定案（F6）：MAX_DAQ / MAX_ODT / MAX_CTO / MAX_DTO / MAX_ODT_ENTRY_SIZE_DAQ ≥ 1。
    // 声明 =0 与实测无关，报 DECLARED_VALUE_INVALID（Detail 带字段名与值）并跳过常规比对；
    // 事件周期 0 仍走既有 EVENT_PERIOD_UNCONVERTED 路径（上方测试钉死），不归本 code。

    [Theory]
    [InlineData("MAX_DAQ", 0u)]
    [InlineData("MAX_ODT", 0u)]
    [InlineData("MAX_CTO", 0u)]
    [InlineData("MAX_DTO", 0u)]
    [InlineData("MAX_ODT_ENTRY_SIZE_DAQ", 0u)]
    public void Pathological_declared_zero_is_rejected_as_declared_value_invalid_not_mismatch(string fieldName, uint value)
    {
        var declared = fieldName switch
        {
            "MAX_DAQ" => BuildBaselineDeclaration(maxDaq: value),
            "MAX_ODT" => BuildBaselineDeclaration(maxOdt: value),
            "MAX_CTO" => BuildBaselineDeclaration(maxCto: value),
            "MAX_DTO" => BuildBaselineDeclaration(maxDto: value),
            "MAX_ODT_ENTRY_SIZE_DAQ" => BuildBaselineDeclaration(maxOdtEntrySizeDaq: value),
            _ => throw new ArgumentOutOfRangeException(nameof(fieldName)),
        };

        var report = Reconcile(declared, BuildBaselineMeasured());

        // 病态声明：Reject 级 DECLARED_VALUE_INVALID，Detail 带字段名与值。
        var invalid = report.Findings.Where(f => f.Code == "DECLARED_VALUE_INVALID").ToList();
        Assert.Single(invalid);
        Assert.Equal(XcpCapabilitySeverity.Reject, invalid[0].Severity);
        Assert.Contains(fieldName, invalid[0].Message);
        Assert.Contains(value.ToString(System.Globalization.CultureInfo.InvariantCulture), invalid[0].Message);

        // 不产出误导性 MISMATCH：该字段跳过常规比对（declared=0 vs measured=8 不是"不一致"）。
        Assert.DoesNotContain(report.Findings, f => f.Code == $"{fieldName}_MISMATCH");

        // 病态声明 = 拒绝启动（声明本身非法，采集不可信）。
        Assert.True(report.RejectedStart);
    }

    // ---- (d) MAX_ODT_ENTRY_SIZE_DAQ 用 XcpDaq 原值（null=未声明），禁用 Suitability 回填 4 ----

    [Fact]
    public void Undeclared_max_odt_entry_size_records_warning_and_never_backfills_4()
    {
        // 声明 null + 实测缺（双缺）：不得按 Suitability 回填 4 判"一致"——必须留"未声明"告警。
        var declared = BuildBaselineDeclaration(maxOdtEntrySizeDaq: null);
        var measured = BuildBaselineMeasured() with { MaxOdtEntrySizeDaq = null };

        var report = Reconcile(declared, measured);

        Assert.False(report.RejectedStart);
        Assert.Contains(report.Findings, f =>
            f.Severity == XcpCapabilitySeverity.Warning && f.Code == "MAX_ODT_ENTRY_SIZE_UNDECLARED");
    }

    [Fact]
    public void Declared_entry_size_uses_raw_value_and_rejects_mismatch()
    {
        // 声明原值 8 vs 实测 4：原值比对不一致 → 拒绝（若回填 4 或静默对齐则漏判）。
        var declared = BuildBaselineDeclaration(maxOdtEntrySizeDaq: 8);

        var report = Reconcile(declared, BuildBaselineMeasured());

        Assert.True(report.RejectedStart);
        Assert.Contains(report.Findings, f =>
            f.Severity == XcpCapabilitySeverity.Reject && f.Code == "MAX_ODT_ENTRY_SIZE_MISMATCH");
    }

    [Fact]
    public void Declared_entry_size_without_measurement_is_rejected()
    {
        // 声明有（4）实测缺：无法核对硬字段 → 拒绝（宁可不采）。
        var measured = BuildBaselineMeasured() with { MaxOdtEntrySizeDaq = null };

        var report = Reconcile(BuildBaselineDeclaration(), measured);

        Assert.True(report.RejectedStart);
        Assert.Contains(report.Findings, f =>
            f.Severity == XcpCapabilitySeverity.Reject && f.Code == "MAX_ODT_ENTRY_SIZE_NOT_MEASURED");
    }

    // ---- (e) Unmodelled 非空 → 告警不静默 ----

    [Fact]
    public void Unmodelled_blocks_surface_as_warnings()
    {
        var declared = BuildBaselineDeclaration();
        var unmodelled = new A2lUnknownBlock("DAQ_EVENT", "/begin DAQ_EVENT ...", new LineRange(1, 2));
        declared = declared with { Unmodelled = new[] { unmodelled } };

        var report = Reconcile(declared, BuildBaselineMeasured());

        Assert.False(report.RejectedStart);
        Assert.Contains(report.Findings, f =>
            f.Severity == XcpCapabilitySeverity.Warning && f.Code == "UNMODELLED_BLOCK"
            && f.Message.Contains("DAQ_EVENT", StringComparison.Ordinal));
    }

    // ---- (f) A2ML 别名归一：SET_DAQ_LIST_MODE ≡ START_STOP_DAQ_LIST 按命令码归一 ----

    [Fact]
    public void Alias_set_daq_list_mode_normalizes_to_start_stop_daq_list_command_code()
    {
        // 两名不同串，必须归一到同一线上命令码 0xDE——字符串直比的实现路径在这里直接暴露。
        Assert.True(XcpA2mlCommandAlias.TryGetCommandCode("SET_DAQ_LIST_MODE", out var aliasCode));
        Assert.True(XcpA2mlCommandAlias.TryGetCommandCode("START_STOP_DAQ_LIST", out var canonicalCode));
        Assert.Equal(XcpPid.StartStopDaqList, canonicalCode);
        Assert.Equal(canonicalCode, aliasCode);
        Assert.NotEqual("SET_DAQ_LIST_MODE", "START_STOP_DAQ_LIST"); // 语义别名 ≠ 字符串相等
    }

    [Fact]
    public void Declared_alias_only_set_daq_list_mode_matches_measured_start_stop_daq_list()
    {
        // 声明侧去掉 START_STOP_DAQ_LIST、只留别名 SET_DAQ_LIST_MODE；实测侧是线上名。
        // 按命令码归一后集合相等 → 不得产生任何命令相关 findings（字符串直比实现必判差异）。
        var declaredCommands = RealOptionalCommands
            .Where(n => n != "START_STOP_DAQ_LIST")
            .ToArray();
        var measuredCommands = RealOptionalCommands
            .Where(n => n != "SET_DAQ_LIST_MODE")
            .ToArray();

        var report = Reconcile(
            BuildBaselineDeclaration(optionalCommands: declaredCommands),
            BuildBaselineMeasured(optionalCommands: measuredCommands));

        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("COMMAND_", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Findings, f => f.Code == "REQUIRED_COMMAND_NOT_DECLARED");
    }

    [Fact]
    public void Declared_missing_required_command_is_rejected()
    {
        var declaredCommands = RealOptionalCommands.Where(n => n != "WRITE_DAQ").ToArray();

        var report = Reconcile(
            BuildBaselineDeclaration(optionalCommands: declaredCommands),
            BuildBaselineMeasured());

        Assert.True(report.RejectedStart);
        Assert.Contains(report.Findings, f =>
            f.Severity == XcpCapabilitySeverity.Reject && f.Code == "REQUIRED_COMMAND_NOT_DECLARED"
            && f.Message.Contains("WRITE_DAQ", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_command_name_is_warned_not_silently_ignored()
    {
        var declaredCommands = RealOptionalCommands.Append("TOTALLY_UNKNOWN_CMD").ToArray();

        var report = Reconcile(
            BuildBaselineDeclaration(optionalCommands: declaredCommands),
            BuildBaselineMeasured());

        Assert.Contains(report.Findings, f =>
            f.Severity == XcpCapabilitySeverity.Warning && f.Code == "COMMAND_NAME_UNKNOWN"
            && f.Message.Contains("TOTALLY_UNKNOWN_CMD", StringComparison.Ordinal));
    }

    // ---- Missing 留痕：各块 Missing 非空 → 告警 ----

    [Fact]
    public void Block_missing_fields_surface_as_warnings()
    {
        var missing = new XcpMissingField("MAX_CTO", 757, "token unreadable");
        var declared = BuildBaselineDeclaration();
        declared = declared with
        {
            ProtocolLayer = declared.ProtocolLayer! with { Missing = new[] { missing } },
        };

        var report = Reconcile(declared, BuildBaselineMeasured());

        Assert.False(report.RejectedStart);
        Assert.Contains(report.Findings, f =>
            f.Severity == XcpCapabilitySeverity.Warning && f.Code == "MISSING_FIELD"
            && f.Message.Contains("MAX_CTO", StringComparison.Ordinal));
    }

    // ---- CAN ID：A-4 待核实（掩码规则归 host）→ 不一致告警不拒绝 ----

    [Fact]
    public void Can_id_mismatch_is_a_warning_pending_bench_verification()
    {
        var report = Reconcile(BuildBaselineDeclaration(), BuildBaselineMeasured());

        // 声明 0x98FFF666/67（A2L 原样）vs 实测 0x18FFF666/67（host 实际使用值）→ 告警。
        Assert.False(report.RejectedStart);
        Assert.Contains(report.Findings, f =>
            f.Severity == XcpCapabilitySeverity.Warning && f.Code == "CAN_ID_MISMATCH");
    }

    // ---- ValidationNote 全量透传为告警 ----

    [Fact]
    public void Cross_check_validation_notes_surface_as_warnings()
    {
        var note = new ValidationNote(
            CrossCheckKind.CanIdBeyond29Bits, "CAN id beyond 29 bits", CrossCheckLines, ErrorSeverity.Warning);

        var report = Reconcile(BuildBaselineDeclaration(), BuildBaselineMeasured(), new[] { note });

        Assert.False(report.RejectedStart);
        Assert.Contains(report.Findings, f =>
            f.Severity == XcpCapabilitySeverity.Warning && f.Code == "A2L_CROSSCHECK"
            && f.Message.Contains("29", StringComparison.Ordinal));
    }

    // ---- (g) 真机 A2L 冒烟对账 ----

    [Fact]
    public void Real_a2l_smoke_reconciles_against_golden_sample_slave()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "App_merge_INCA.a2l");
        var parsed = Asap2PackageApi.ParseFile(path);
        Assert.NotNull(parsed.Value);
        var doc = parsed.Value!;

        var ifData = doc.Modules.Select(m => m.IfDataXcp).First(x => x is not null);
        var notes = Asap2PackageApi.CollectCrossChecks(doc);

        // 实测值 = 模拟从机黄金样本（XcpGoldenSamples）经 XcpResponseDecoder 解码——
        // 保证对账消费的是"响应解码值"而非测试散写的期望值（T7 评审 MEDIUM 债修正）。
        var processor = XcpResponseDecoder.GetDaqProcessorInfo(XcpGoldenSamples.GetDaqProcessorInfoPositiveResponse.Span);
        var listInfo = XcpResponseDecoder.GetDaqListInfo(XcpGoldenSamples.GetDaqListInfoPositiveResponse.Span);
        var resolution = XcpResponseDecoder.GetDaqResolutionInfo(XcpGoldenSamples.GetDaqResolutionInfoPositiveResponse.Span);
        var eventInfo = XcpResponseDecoder.GetDaqEventInfo(XcpGoldenSamples.GetDaqEventInfoPositiveResponse.Span);

        var measured = new XcpMeasuredCapabilities(
            MaxDaq: processor.MaxDaq,
            MaxEventChannel: processor.MaxEventChannel,
            MinDaq: processor.MinDaq,
            MaxOdt: listInfo.MaxOdt,
            // CONNECT 按 ASAM 标准布局解码（Xcp_Std.c 布局即标准，round-3 证伪"真机非标准"）。
            // 实测口径：MaxCto = 观察到的最大响应帧长（黄金样本 8B）；
            // MaxDto = spec 常量 8B，非实测（T19 抓包回填）。
            MaxCto: (byte)XcpGoldenSamples.ConnectPositiveResponse.Length,
            MaxDto: (byte)XcpCtoFrame.MaxByteLength,
            MaxOdtEntrySizeDaq: resolution.MaxOdtEntrySizeDaq,
            // 线上 (EVENT_CYCLE=0x0A, TIME_UNIT=0x06)：XCP 线上 TIME_UNIT 表 0=1ns 起、6=1ms
            // → 10×1000µs = 10000µs（100Hz，spec §1）。A2ML 编号与线上表的对应关系——
            // spec §1 "差 3 档"与 S1 §4.4 同表证据两说矛盾，统一为 A-2 台架核死（T19 回填）；
            // 此处一致是模拟从机按 spec §1 设定的结果；换算只经 XcpWireTimeUnit，禁止拿线上字节值直比。
            EventPeriodMicroseconds: XcpWireTimeUnit.TryConvertMicroseconds(
                eventInfo.EventCycle, eventInfo.EventChannelTimeUnit, out var measuredPeriodUs)
                ? measuredPeriodUs
                : null,
            OptionalCommands: RealOptionalCommands,
            SlaveCanIdRaw: 0x18FFF666,
            MasterCanIdRaw: 0x18FFF667);

        var report = XcpCapabilityReconciler.Reconcile(ifData, notes, measured);

        // 真机声明 vs 黄金样本从机：一致 → 允许启动；但 A2L 内部矛盾提醒必须全部浮出。
        Assert.False(report.RejectedStart);
        Assert.DoesNotContain(report.Findings, f => f.Severity == XcpCapabilitySeverity.Reject);
        Assert.Contains(report.Findings, f => f.Code == "A2L_CROSSCHECK" && f.Severity == XcpCapabilitySeverity.Warning);
        Assert.Contains(report.Findings, f => f.Code == "CAN_ID_MISMATCH" && f.Severity == XcpCapabilitySeverity.Warning);
    }
}
