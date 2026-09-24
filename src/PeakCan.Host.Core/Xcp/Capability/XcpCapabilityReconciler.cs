using A2lEditor.Core;
using A2lEditor.Core.IfData;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Xcp.Capability;

/// <summary>
/// 实测能力侧输入（S2-T7）：来自 CONNECT + GET_DAQ_*_INFO 响应解码值
///（XcpResponseDecoder；模拟从机走 XcpGoldenSamples 同一路径）。
/// <para>
/// 事件周期只收换算后的微秒值：线上 (EVENT_CYCLE, TIME_UNIT) → µs 的换算表归
/// 探针（T8），对账只消费 µs 并与包侧 PeriodMicroseconds 直比——TIME_UNIT 的
/// A2ML 枚举与线上表是两套编号体系，禁止拿线上字节值与声明码直比（spec §1）。
/// </para>
/// <para>
/// CAN ID 为传输层实际使用的原样值（非命令响应字段，A-4 台架核实前作告警级对账）。
/// </para>
/// </summary>
public sealed record XcpMeasuredCapabilities(
    ushort MaxDaq,                     // GET_DAQ_PROCESSOR_INFO.MaxDaq
    ushort MaxEventChannel,            // GET_DAQ_PROCESSOR_INFO.MaxEventChannel
    byte MinDaq,                       // GET_DAQ_PROCESSOR_INFO.MinDaq
    byte MaxOdt,                       // GET_DAQ_LIST_INFO.MaxOdt（与 DAQ_LIST.MAX_ODT 同为"ODT 数 − 1"口径）
    byte MaxCto,                       // 线上 CTO 帧长观察派生（T8 探针职责）
    byte MaxDto,                       // 线上 DTO 帧长观察派生
    byte? MaxOdtEntrySizeDaq,          // WRITE_DAQ 条目尺寸观测；null = 未测得
    uint? EventPeriodMicroseconds,     // 线上 (EVENT_CYCLE, TIME_UNIT) 换算后的 µs；null = 未测得
    IReadOnlyList<string> OptionalCommands, // 实测支持的命令名（经 XcpA2mlCommandAlias 归一）
    uint? SlaveCanIdRaw = null,        // 传输层实际使用的 slave CAN ID（原样）
    uint? MasterCanIdRaw = null);      // 传输层实际使用的 master CAN ID（原样）

/// <summary>
/// 能力对账引擎（S2-T7，spec §3 Capability / §5 验收 3）。
/// 对账输入 = A2L 声明值（XcpIfData 聚合的 XcpProtocolLayer/XcpDaq/XcpOnCan/
/// XcpSegment，含各自 Missing）+ Asap2PackageApi.CollectCrossChecks 的
/// ValidationNote + XcpIfData.Unmodelled；对账对象 = 上述声明 vs 实测响应解码值。
/// <para>
/// 分级标准（钉死，宁可不采不错采）——
/// <b>拒绝启动</b>：硬约束字段（MAX_DAQ / MAX_CTO / MAX_DTO / MAX_ODT / 事件周期 /
/// MAX_ODT_ENTRY_SIZE_DAQ 非空值）声明 vs 实测不一致；硬字段无法核对（声明 null、
/// 实测缺、声明块整体缺席）；声明缺 S2 必需命令。
/// <b>告警</b>：ValidationNote 全量、Unmodelled 非空（不静默跳过未知块）、各块
/// Missing 留痕、MAX_ODT_ENTRY_SIZE_DAQ 未声明（禁用 Suitability 回填 4）、
/// MIN_DAQ / MAX_EVENT_CHANNEL 不一致、CAN ID 不一致（A-4 待核实）。
/// </para>
/// </summary>
public static class XcpCapabilityReconciler
{
    /// <summary>S2 必需命令（XCP 1.0 OPTIONAL_CMD 口径；CONNECT/DISCONNECT/GET_STATUS/SYNCH 为强制命令不在枚举内）。</summary>
    private static readonly (string Name, byte Code)[] RequiredCommands =
    [
        ("GET_COMM_MODE_INFO", XcpPid.GetCommModeInfo),
        ("SET_MTA", XcpPid.SetMta),
        ("UPLOAD", XcpPid.Upload),
        ("SHORT_UPLOAD", XcpPid.ShortUpload),
        ("SET_DAQ_PTR", XcpPid.SetDaqPtr),
        ("WRITE_DAQ", XcpPid.WriteDaq),
        ("CLEAR_DAQ_LIST", XcpPid.ClearDaqList),
        ("START_STOP_DAQ_LIST", XcpPid.StartStopDaqList),
        ("START_STOP_SYNCH", XcpPid.StartStopSynch),
        ("GET_DAQ_PROCESSOR_INFO", XcpPid.GetDaqProcessorInfo),
        ("GET_DAQ_RESOLUTION_INFO", XcpPid.GetDaqResolutionInfo),
        ("GET_DAQ_LIST_INFO", XcpPid.GetDaqListInfo),
        ("GET_DAQ_EVENT_INFO", XcpPid.GetDaqEventInfo),
    ];

    public static XcpCapabilityReport Reconcile(
        XcpIfData? declared,
        IReadOnlyList<ValidationNote> validationNotes,
        XcpMeasuredCapabilities measured)
    {
        ArgumentNullException.ThrowIfNull(validationNotes);
        ArgumentNullException.ThrowIfNull(measured);

        var findings = new List<XcpCapabilityFinding>();

        if (declared is null)
        {
            findings.Add(Reject("DECLARATION_MISSING", "A2L has no XCP IF_DATA — nothing to reconcile, refusing to start."));
            return Finalize(findings);
        }

        // ---- A2L 自身矛盾/留痕：全部告警浮出，不阻断 ----
        foreach (var note in validationNotes)
        {
            var lines = note.Lines.Count > 0 ? $" (lines {string.Join(", ", note.Lines)})" : string.Empty;
            findings.Add(Warning("A2L_CROSSCHECK", $"{note.Kind}: {note.Message}{lines}"));
        }

        foreach (var unknown in declared.Unmodelled)
        {
            findings.Add(Warning("UNMODELLED_BLOCK",
                $"Unknown block '{unknown.BlockType}' (lines {unknown.SourceLines.Start}-{unknown.SourceLines.End}) " +
                "preserved verbatim — not modelled, do not silently ignore."));
        }

        ReportMissing(findings, "XCP", declared.Missing);
        if (declared.ProtocolLayer is { } pl)
            ReportMissing(findings, "PROTOCOL_LAYER", pl.Missing);
        if (declared.Daq is { } daq)
            ReportMissing(findings, "DAQ", daq.Missing);
        foreach (var onCan in declared.OnCan)
            ReportMissing(findings, "XCP_ON_CAN", onCan.Missing);
        foreach (var segment in declared.Segments)
            ReportMissing(findings, "SEGMENT", segment.Missing);

        // ---- 协议层硬字段：MAX_CTO / MAX_DTO ----
        if (declared.ProtocolLayer is not { } protocolLayer)
        {
            findings.Add(Reject("PROTOCOL_LAYER_MISSING", "PROTOCOL_LAYER block absent — CTO/DTO hard constraints unverifiable."));
        }
        else
        {
            ReconcileHardField(findings, "MAX_CTO", protocolLayer.MaxCto, measured.MaxCto);
            ReconcileHardField(findings, "MAX_DTO", protocolLayer.MaxDto, measured.MaxDto);
        }

        // ---- DAQ 硬字段：MAX_DAQ / MAX_ODT / 事件周期 / MAX_ODT_ENTRY_SIZE_DAQ ----
        if (declared.Daq is not { } daqBlock)
        {
            findings.Add(Reject("DAQ_MISSING", "DAQ block absent — DAQ configuration unverifiable."));
        }
        else
        {
            ReconcileHardField(findings, "MAX_DAQ", daqBlock.MaxDaq, measured.MaxDaq);

            if (daqBlock.MaxEventChannel is { } declaredMaxEventChannel && declaredMaxEventChannel != measured.MaxEventChannel)
                findings.Add(Warning("MAX_EVENT_CHANNEL_MISMATCH",
                    $"MAX_EVENT_CHANNEL declared {declaredMaxEventChannel} but measured {measured.MaxEventChannel}."));

            if (daqBlock.MinDaq is { } declaredMinDaq && declaredMinDaq != measured.MinDaq)
                findings.Add(Warning("MIN_DAQ_MISMATCH",
                    $"MIN_DAQ declared {declaredMinDaq} but measured {measured.MinDaq}."));

            // MAX_ODT：声明侧只认 DAQ_LIST.MAX_ODT（与 GET_DAQ_LIST_INFO 同为"ODT 数 − 1"口径）。
            if (daqBlock.Lists.Count == 0)
            {
                findings.Add(Reject("DAQ_LIST_MISSING", "No DAQ_LIST declared — DAQ planning impossible."));
            }
            else
            {
                if (daqBlock.Lists.Count > 1)
                    findings.Add(Warning("DAQ_LIST_MULTIPLE",
                        $"{daqBlock.Lists.Count} DAQ_LISTs declared — S2 only drives list 0."));
                ReconcileHardField(findings, "MAX_ODT", daqBlock.Lists[0].MaxOdt, measured.MaxOdt);
            }

            // 事件周期：只消费包侧 PeriodMicroseconds（0 = 未换算 = 对账失败）。
            if (daqBlock.Events.Count == 0)
            {
                findings.Add(Reject("EVENT_CHANNEL_MISSING", "No EVENT declared — event period unverifiable."));
            }
            else
            {
                if (daqBlock.Events.Count > 1)
                    findings.Add(Warning("EVENT_CHANNEL_MULTIPLE",
                        $"{daqBlock.Events.Count} EVENTs declared — S2 only reconciles event 0."));

                var declaredPeriod = daqBlock.Events[0].PeriodMicroseconds;
                if (declaredPeriod == 0)
                {
                    findings.Add(Reject("EVENT_PERIOD_UNCONVERTED",
                        "EVENT.PeriodMicroseconds == 0 (TIME_UNIT unknown/out-of-range — the package never converts) " +
                        "— refusing to compare against raw wire codes (A2ML vs online TIME_UNIT are different scales)."));
                }
                else if (measured.EventPeriodMicroseconds is not { } measuredPeriod)
                {
                    findings.Add(Reject("EVENT_PERIOD_NOT_MEASURED", "Event period not measured (GET_DAQ_EVENT_INFO missing)."));
                }
                else if (declaredPeriod != measuredPeriod)
                {
                    findings.Add(Reject("EVENT_PERIOD_MISMATCH",
                        $"Event period declared {declaredPeriod} µs but measured {measuredPeriod} µs."));
                }
            }

            // MAX_ODT_ENTRY_SIZE_DAQ：用 XcpDaq 原值（null = 未声明）——禁用 Suitability 的回填 4。
            if (daqBlock.MaxOdtEntrySizeDaq is not { } declaredEntrySize)
            {
                findings.Add(Warning("MAX_ODT_ENTRY_SIZE_UNDECLARED",
                    "MAX_ODT_ENTRY_SIZE_DAQ not declared (raw null) — recorded as undeclared; " +
                    "Suitability's default backfill of 4 is NOT applied."));
            }
            else if (measured.MaxOdtEntrySizeDaq is not { } measuredEntrySize)
            {
                findings.Add(Reject("MAX_ODT_ENTRY_SIZE_NOT_MEASURED",
                    $"MAX_ODT_ENTRY_SIZE_DAQ declared {declaredEntrySize} but not measured — hard field unverifiable."));
            }
            else if (declaredEntrySize != measuredEntrySize)
            {
                findings.Add(Reject("MAX_ODT_ENTRY_SIZE_MISMATCH",
                    $"MAX_ODT_ENTRY_SIZE_DAQ declared {declaredEntrySize} but measured {measuredEntrySize}."));
            }
        }

        // ---- OPTIONAL_CMD：按命令码归一后集合比对（别名 SET_DAQ_LIST_MODE ≡ START_STOP_DAQ_LIST）----
        ReconcileCommands(findings, declared.ProtocolLayer?.OptionalCommands ?? [], measured.OptionalCommands);

        // ---- CAN ID：A-4 台架核实前，不一致只告警（掩码规则归 host 侧）----
        var onCanDeclared = declared.OnCan.Count > 0 ? declared.OnCan[0] : null;
        if (onCanDeclared is null)
        {
            if (measured.SlaveCanIdRaw is not null || measured.MasterCanIdRaw is not null)
                findings.Add(Warning("CAN_ID_UNDECLARED", "Measured CAN IDs present but XCP_ON_CAN not declared."));
        }
        else
        {
            var mismatches = new List<string>();
            if (measured.MasterCanIdRaw is { } master && master != onCanDeclared.MasterCanIdRaw)
                mismatches.Add($"master declared 0x{onCanDeclared.MasterCanIdRaw:X} vs measured 0x{master:X}");
            if (measured.SlaveCanIdRaw is { } slave && slave != onCanDeclared.SlaveCanIdRaw)
                mismatches.Add($"slave declared 0x{onCanDeclared.SlaveCanIdRaw:X} vs measured 0x{slave:X}");
            if (mismatches.Count > 0)
                findings.Add(Warning("CAN_ID_MISMATCH",
                    "CAN ID mismatch pending bench verification (A-4, host-side mask rule undecided): "
                    + string.Join("; ", mismatches) + "."));
        }

        return Finalize(findings);
    }

    private static void ReconcileCommands(
        List<XcpCapabilityFinding> findings,
        IReadOnlyList<string> declaredNames,
        IReadOnlyList<string> measuredNames)
    {
        var (declaredCodes, declaredUnknown) = XcpA2mlCommandAlias.Normalize(declaredNames);
        var (measuredCodes, measuredUnknown) = XcpA2mlCommandAlias.Normalize(measuredNames);

        // 表外名字：告警不静默（无法归一 = 无法对账）。
        foreach (var name in declaredUnknown)
            findings.Add(Warning("COMMAND_NAME_UNKNOWN", $"Declared OPTIONAL_CMD '{name}' is not in the alias table — excluded from reconciliation."));
        foreach (var name in measuredUnknown)
            findings.Add(Warning("COMMAND_NAME_UNKNOWN", $"Measured command '{name}' is not in the alias table — excluded from reconciliation."));

        // S2 必需命令：声明缺 = 采集跑不了 → 拒绝。
        foreach (var (name, code) in RequiredCommands)
        {
            if (!declaredCodes.Contains(code))
                findings.Add(Reject("REQUIRED_COMMAND_NOT_DECLARED",
                    $"Required command {name} (0x{code:X2}) missing from A2L OPTIONAL_CMD — acquisition cannot run."));
        }

        // 声明有实测无：从机不支持声明的能力 → 拒绝（不匹配即拒绝）。
        foreach (var code in declaredCodes.Where(c => !measuredCodes.Contains(c)).OrderBy(c => c))
            findings.Add(Reject("COMMAND_DECLARED_NOT_MEASURED",
                $"Command 0x{code:X2} declared in A2L OPTIONAL_CMD but not supported by the slave."));

        // 实测有声明无：信息性（从机能力超出声明）。
        foreach (var code in measuredCodes.Where(c => !declaredCodes.Contains(c)).OrderBy(c => c))
            findings.Add(Warning("COMMAND_MEASURED_NOT_DECLARED",
                $"Command 0x{code:X2} supported by the slave but not declared in A2L OPTIONAL_CMD."));
    }

    /// <summary>硬字段通用核对：声明 null → 无法核对拒绝；实测缺已由调用方按字段细分；不一致 → 拒绝。</summary>
    private static void ReconcileHardField(List<XcpCapabilityFinding> findings, string name, uint? declared, uint measured)
    {
        if (declared is not { } declaredValue)
        {
            findings.Add(Reject($"{name}_UNDECLARED", $"{name} not declared — hard constraint unverifiable, refusing to start."));
            return;
        }

        if (declaredValue != measured)
            findings.Add(Reject($"{name}_MISMATCH", $"{name} declared {declaredValue} but measured {measured}."));
    }

    private static void ReportMissing(List<XcpCapabilityFinding> findings, string blockName, IReadOnlyList<XcpMissingField> missing)
    {
        foreach (var field in missing)
            findings.Add(Warning("MISSING_FIELD", $"{blockName}.{field.Field} (line {field.Line}): {field.Reason}"));
    }

    private static XcpCapabilityFinding Warning(string code, string message) =>
        new(XcpCapabilitySeverity.Warning, code, message);

    private static XcpCapabilityFinding Reject(string code, string message) =>
        new(XcpCapabilitySeverity.Reject, code, message);

    private static XcpCapabilityReport Finalize(List<XcpCapabilityFinding> findings) =>
        new(RejectedStart: findings.Any(f => f.Severity == XcpCapabilitySeverity.Reject), findings);
}
