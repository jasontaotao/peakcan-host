using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using A2lEditor.Core;
using PeakCan.HIL.Core;
using A2lEditor.Core.IfData;
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
        var declared = ParseDeclaration(options.A2LPath, out var validationNotes);

        // ---- XCP 会话：CONNECT → 能力查询全链 ----
        var masterOptions = new XcpMasterOptions(
            new CanId(options.MasterCanIdRaw, FrameFormatOf(options.MasterCanIdRaw)),
            options.Timeout,
            options.MaxRetries);
        using var session = new ProbeSession(transport, masterOptions);

        var connectBytes = await session.SendAsync(XcpCommandEncoder.Connect(), ct);
        var connect = XcpResponseDecoder.Connect(connectBytes);
        var commMode = XcpResponseDecoder.GetCommModeInfo(
            await session.SendAsync(XcpCommandEncoder.GetCommModeInfo(), ct));
        var processor = XcpResponseDecoder.GetDaqProcessorInfo(
            await session.SendAsync(XcpCommandEncoder.GetDaqProcessorInfo(), ct));
        var resolution = XcpResponseDecoder.GetDaqResolutionInfo(
            await session.SendAsync(XcpCommandEncoder.GetDaqResolutionInfo(), ct));
        var listInfo = XcpResponseDecoder.GetDaqListInfo(
            await session.SendAsync(XcpCommandEncoder.GetDaqListInfo(daqListNumber: 0), ct));
        var eventInfo = XcpResponseDecoder.GetDaqEventInfo(
            await session.SendAsync(XcpCommandEncoder.GetDaqEventInfo(eventChannel: 0), ct));

        // ---- OPTIONAL_CMD 逐命令探测（只发良性/0 效应帧，见 ProbeMutableCommands）----
        var measuredCommands = await ProbeOptionalCommandsAsync(session, ct);

        // 收尾断连：强制命令，不入对账（A2L OPTIONAL_CMD 不含 CONNECT/DISCONNECT）。
        XcpResponseDecoder.Disconnect(await session.SendAsync(XcpCommandEncoder.Disconnect(), ct));

        // ---- 实测能力：全部值来自响应解码 / 线上帧长观察，禁止散写期望值 ----
        // MAX_CTO/MAX_DTO 不在任何 XCP 响应字段内（CONNECT byte[3]=RESOURCE、byte[4]=COMM_MODE_BASIC
        // 是 XCP 1.0 定义，不是 CTO/DTO）——属传输层观察：CTO = 观察到的最大响应帧长，
        // DTO = CAN 经典帧 DLC（spec §1：DTO 8B）。
        var measured = new XcpMeasuredCapabilities(
            MaxDaq: processor.MaxDaq,
            MaxEventChannel: processor.MaxEventChannel,
            MinDaq: processor.MinDaq,
            MaxOdt: listInfo.MaxOdt,
            MaxCto: (byte)Math.Max(session.MaxObservedResponseLength, 1),
            MaxDto: (byte)XcpCtoFrame.MaxByteLength,
            MaxOdtEntrySizeDaq: resolution.MaxOdtEntrySizeDaq,
            // 事件周期只经 XcpWireTimeUnit（线上表 0=1ns 起）换算成 µs；换不出（亚微秒档/
            // 周期未知/表外编号）= null = 对账拒绝（宁缺不猜，spec §1 TIME_UNIT 坑）。
            EventPeriodMicroseconds: XcpWireTimeUnit.TryConvertMicroseconds(
                eventInfo.EventCycle, eventInfo.EventChannelTimeUnit, out var measuredPeriodUs)
                ? measuredPeriodUs
                : null,
            OptionalCommands: measuredCommands,
            SlaveCanIdRaw: options.SlaveCanIdRaw,
            MasterCanIdRaw: options.MasterCanIdRaw);

        var reconciliation = XcpCapabilityReconciler.Reconcile(declared, validationNotes, measured);

        // ---- 事实清单（spec §4：直接兑现附录 A-1/2/3/4/5 的探针侧字段）----
        var report = BuildReport(options, declared, connect, commMode, processor, resolution, listInfo, eventInfo, measured, measuredCommands, reconciliation);
        var json = JsonSerializer.Serialize(report, JsonOptions);

        if (options.OutputPath is { } outputPath)
            await File.WriteAllTextAsync(outputPath, json, ct);
        Console.WriteLine(json);

        return reconciliation.RejectedStart ? 1 : 0;
    }

    /// <summary>
    /// A2L 声明的 XCP_ON_CAN.BAUDRATE → 经典 CAN 预设（真机路径用，按声明值走不猜；
    /// 不在预设表 = 异常）。CI/测试路径不经过本方法（直接注入模拟从机 transport）。
    /// </summary>
    public static BaudRate ResolveDeclaredBaudRate(string a2lPath)
    {
        var declared = ParseDeclaration(a2lPath, out _);
        if (declared.OnCan.Count == 0)
            throw new InvalidDataException($"A2L has no XCP_ON_CAN block: {a2lPath}");

        return declared.OnCan[0].Baudrate switch
        {
            125000 => BaudRate.Can125kbps,
            250000 => BaudRate.Can250kbps,
            500000 => BaudRate.Can500kbps,
            1000000 => BaudRate.Can1Mbps,
            _ => throw new NotSupportedException(
                $"XCP_ON_CAN.BAUDRATE {declared.OnCan[0].Baudrate} has no classic CAN preset — refusing to guess."),
        };
    }

    private static XcpIfData ParseDeclaration(string a2lPath, out IReadOnlyList<ValidationNote> validationNotes)
    {
        var parsed = Asap2PackageApi.ParseFile(a2lPath);
        if (parsed.Value is not { } doc)
            throw new InvalidDataException($"A2L parse failed: {a2lPath}");

        validationNotes = Asap2PackageApi.CollectCrossChecks(doc);
        return doc.Modules.Select(m => m.IfDataXcp).FirstOrDefault(x => x is not null)
            ?? throw new InvalidDataException($"A2L has no XCP IF_DATA: {a2lPath}");
    }

    /// <summary>
    /// OPTIONAL_CMD 逐命令探测。XCP 没有"支持命令清单"响应——标准做法是逐命令发
    /// 良性参数帧，按 ERR_CMD_UNKNOWN 判定不支持；其他负响应（参数被拒等）仍算
    /// 命令存在。全部探测帧 0 效应：地址 0 读、DAQ 未运行态的配置/停表。
    /// DOWNLOAD 用 0 字节请求（BYTE_COUNT=0，无数据可写）——探针绝不产生真实写流量
    /// （spec 决策 D2 的禁用语义同样约束探针）。
    /// </summary>
    private static async Task<List<string>> ProbeOptionalCommandsAsync(ProbeSession session, CancellationToken ct)
    {
        // 信息类命令已在能力查询全链中发出并得到正响应——直接计入实测支持集。
        List<string> supported =
        [
            "GET_COMM_MODE_INFO",
            "GET_DAQ_PROCESSOR_INFO",
            "GET_DAQ_RESOLUTION_INFO",
            "GET_DAQ_LIST_INFO",
            "GET_DAQ_EVENT_INFO",
        ];

        foreach (var (name, frame) in MutableCommandProbes())
        {
            try
            {
                _ = await session.SendAsync(frame, ct);
            }
            catch (XcpErrorResponseException ex) when (ex.Response.Code == XcpError.CmdUnknown)
            {
                continue;
            }

            supported.Add(name);
        }

        return supported;
    }

    private static IEnumerable<(string Name, XcpCtoFrame Frame)> MutableCommandProbes()
    {
        yield return ("SET_MTA", XcpCommandEncoder.SetMta(0x00, 0x00000000));
        yield return ("UPLOAD", XcpCommandEncoder.Upload(1));
        yield return ("SHORT_UPLOAD", XcpCommandEncoder.ShortUpload(1, 0x00000000, 0x00));
        yield return ("DOWNLOAD", new XcpCtoFrame(new byte[] { XcpPid.Download, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }));
        yield return ("SET_DAQ_PTR", XcpCommandEncoder.SetDaqPtr(0x00, 0x00000000));
        yield return ("WRITE_DAQ", XcpCommandEncoder.WriteDaq(0, 1, 0x00, 0x00000000));
        yield return ("CLEAR_DAQ_LIST", XcpCommandEncoder.ClearDaqList(0x00, 0));
        yield return ("START_STOP_DAQ_LIST", XcpCommandEncoder.StartStopDaqList(0x00, 0));
        yield return ("START_STOP_SYNCH", XcpCommandEncoder.StartStopSynch());
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
        List<string> measuredCommands,
        XcpCapabilityReport reconciliation)
    {
        var declaredOnCan = declared.OnCan.Count > 0 ? declared.OnCan[0] : null;
        var declaredEvent = declared.Daq is { } daq && daq.Events.Count > 0 ? daq.Events[0] : null;

        // A-5 ODT 打包上限：DTO 数据场硬上限 7B（DTO 8B − PID 1B，spec §1）；
        // 条目不可拆 → 每 ODT 至多 floor(7B / 条目尺寸) 个完整条目。
        var dtoPayloadCapBytes = XcpCtoFrame.MaxByteLength - 1;
        int? maxEntriesPerOdt = resolution.MaxOdtEntrySizeDaq > 0
            ? dtoPayloadCapBytes / resolution.MaxOdtEntrySizeDaq
            : null;

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
            Reconciliation: reconciliation);
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

    /// <summary>XcpMaster 会话 + 线上响应帧长观察（MAX_CTO 的观察派生来源）。</summary>
    private sealed class ProbeSession : IDisposable
    {
        private readonly XcpMaster _master;

        public ProbeSession(IXcpTransport transport, XcpMasterOptions options)
        {
            _master = new XcpMaster(transport, options);
        }

        public int MaxObservedResponseLength { get; private set; }

        public async Task<byte[]> SendAsync(XcpCtoFrame command, CancellationToken ct)
        {
            var response = await _master.SendAsync(command, ct);
            if (response.Length > MaxObservedResponseLength)
                MaxObservedResponseLength = response.Length;
            return response;
        }

        public void Dispose() => _master.Dispose();
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
    IReadOnlyList<string> MeasuredCommands,
    XcpCapabilityReport Reconciliation);

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
