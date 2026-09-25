using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Xcp.Capability;

/// <summary>
/// 单个能力查询失败归因（S2-T8 评审 Important-1 口径：异常类型 + 预期偏差标记）。
/// D7 下沉后 CLI probe 的事实清单与 App VM 的状态区共用同一归因形状。
/// </summary>
public sealed record XcpCapabilityQueryFailure(string Command, string ExceptionType, string Marker);

/// <summary>
/// 能力实测链结果（D7 下沉后的单源产物，CLI probe 与 App VM 同源消费）。
/// 各响应字段为解码值或类型默认值——单项查询失败不终止链，归因见
/// <see cref="QueryFailures"/>；实测支持命令集见 <see cref="Measured"/>.OptionalCommands。
/// </summary>
public sealed record XcpCapabilityProbeResult(
    XcpConnectResponse Connect,
    XcpGetCommModeInfoResponse CommMode,
    XcpGetDaqProcessorInfoResponse Processor,
    XcpGetDaqResolutionInfoResponse Resolution,
    XcpGetDaqListInfoResponse ListInfo,
    XcpGetDaqEventInfoResponse EventInfo,
    XcpMeasuredCapabilities Measured,
    IReadOnlyList<XcpCapabilityQueryFailure> QueryFailures,
    int MaxObservedResponseLength);

/// <summary>
/// XCP 能力实测链（S3-T7b / D7 裁决：CLI probe 与 App VM 的单源探测器）。
/// 链路 = CONNECT → GET_COMM_MODE_INFO / GET_DAQ_PROCESSOR_INFO /
/// GET_DAQ_RESOLUTION_INFO / GET_DAQ_LIST_INFO / GET_DAQ_EVENT_INFO →
/// OPTIONAL_CMD 逐命令良性探测 → <see cref="XcpMeasuredCapabilities"/>。
/// <para>
/// OPTIONAL_CMD 探测口径（照抄 S2-T8 评审后的 xcp-probe）：
/// ① <b>含 0 字节 DOWNLOAD 良性探测</b>（BYTE_COUNT=0，无数据可写，从机零效应——
/// D7 裁决：良性探测 ≠ 写流量，"DOWNLOAD 零入口"约束的是写数据路径）；
/// ② 非 <see cref="XcpError.CmdUnknown"/> 负响应仍算命令存在（参数被拒 ≠ 不支持，M1）；
/// ③ 单项信息查询失败按 <see cref="XcpCapabilityQueryFailure"/> 归因出站，不终止链（L2），
/// 失败项不冒充实测支持命令。
/// </para>
/// <para>
/// 断连不在本链内：CLI probe 探测后自行发 DISCONNECT；App 会话继续复用同一
/// master 进入采集，探针不得替调用侧断开会话。
/// </para>
/// </summary>
public static class XcpCapabilityProber
{
    /// <summary>单项能力查询失败归因标记（S2-T8 评审：真机三条已钉死布局偏差的占位标记）。</summary>
    private const string ExpectedDeviationMarker = "expected-deviation";

    /// <summary>
    /// 执行完整能力实测链。CONNECT 失败直接抛出（无实测可比，调用侧按会话失败处理）；
    /// 其后所有单项失败均归因出站。master 的 CAN ID / 超时 / 重试由构造参数决定。
    /// </summary>
    public static async Task<XcpCapabilityProbeResult> ProbeAsync(
        XcpMaster master,
        uint masterCanIdRaw,
        uint slaveCanIdRaw,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(master);

        var maxObservedResponseLength = 0;
        var queryFailures = new List<XcpCapabilityQueryFailure>();

        async Task<byte[]> SendAsync(XcpCtoFrame command)
        {
            var response = await master.SendAsync(command, ct).ConfigureAwait(false);
            if (response.Length > maxObservedResponseLength)
                maxObservedResponseLength = response.Length;
            return response;
        }

        // CONNECT 失败即断链：不进对账（无实测可比）——CLI 退出码 2 / App 启动失败语义由调用侧兑现。
        var connect = XcpResponseDecoder.Connect(await SendAsync(XcpCommandEncoder.Connect()).ConfigureAwait(false));

        // ---- 能力查询全链：每个查询独立异常边界（S2-T8 评审 Important-1）——
        // 单个查询的解码异常/负响应/超时只归因入清单，不终止探针；真机三条已钉死
        // 布局偏差（GET_DAQ_EVENT_INFO 7B / PROCESSOR_INFO 大端 / LIST_INFO 错位，
        // 源码：从机固件 Xcp_Std.c）由此存活，交 T19 台架核死。
        var (commMode, commModeOk) = await TryQueryAsync(
            SendAsync, XcpCommandEncoder.GetCommModeInfo(), "GET_COMM_MODE_INFO",
            static bytes => XcpResponseDecoder.GetCommModeInfo(bytes), queryFailures).ConfigureAwait(false);
        var (processor, processorOk) = await TryQueryAsync(
            SendAsync, XcpCommandEncoder.GetDaqProcessorInfo(), "GET_DAQ_PROCESSOR_INFO",
            static bytes => XcpResponseDecoder.GetDaqProcessorInfo(bytes), queryFailures).ConfigureAwait(false);
        var (resolution, resolutionOk) = await TryQueryAsync(
            SendAsync, XcpCommandEncoder.GetDaqResolutionInfo(), "GET_DAQ_RESOLUTION_INFO",
            static bytes => XcpResponseDecoder.GetDaqResolutionInfo(bytes), queryFailures).ConfigureAwait(false);
        var (listInfo, listInfoOk) = await TryQueryAsync(
            SendAsync, XcpCommandEncoder.GetDaqListInfo(daqListNumber: 0), "GET_DAQ_LIST_INFO",
            static bytes => XcpResponseDecoder.GetDaqListInfo(bytes), queryFailures).ConfigureAwait(false);
        var (eventInfo, eventInfoOk) = await TryQueryAsync(
            SendAsync, XcpCommandEncoder.GetDaqEventInfo(eventChannel: 0), "GET_DAQ_EVENT_INFO",
            static bytes => XcpResponseDecoder.GetDaqEventInfo(bytes), queryFailures).ConfigureAwait(false);

        // ---- OPTIONAL_CMD 逐命令探测（只发良性/0 效应帧）----
        // 信息类命令：仅本次查询实际得到正响应的才计入实测支持集（失败项不得冒充）。
        List<string> measuredCommands = [];
        if (commModeOk) measuredCommands.Add("GET_COMM_MODE_INFO");
        if (processorOk) measuredCommands.Add("GET_DAQ_PROCESSOR_INFO");
        if (resolutionOk) measuredCommands.Add("GET_DAQ_RESOLUTION_INFO");
        if (listInfoOk) measuredCommands.Add("GET_DAQ_LIST_INFO");
        if (eventInfoOk) measuredCommands.Add("GET_DAQ_EVENT_INFO");
        await ProbeOptionalCommandsAsync(SendAsync, measuredCommands).ConfigureAwait(false);

        // ---- 实测能力：全部值来自响应解码 / 线上帧长观察，禁止散写期望值 ----
        // 事实清单取值：MaxCto = 观察到的最大响应帧长；MaxDto = spec 常量 8B，非实测
        //（T19 抓包回填）。事件周期只经 XcpWireTimeUnit 换算；换不出 = null = 对账拒绝
        //（宁缺不猜，spec §1 TIME_UNIT 坑）。
        var measured = new XcpMeasuredCapabilities(
            MaxDaq: processor.MaxDaq,
            MaxEventChannel: processor.MaxEventChannel,
            MinDaq: processor.MinDaq,
            MaxOdt: listInfo.MaxOdt,
            MaxCto: (byte)Math.Max(maxObservedResponseLength, 1),
            MaxDto: (byte)XcpCtoFrame.MaxByteLength,
            MaxOdtEntrySizeDaq: resolution.MaxOdtEntrySizeDaq,
            EventPeriodMicroseconds: XcpWireTimeUnit.TryConvertMicroseconds(
                eventInfo.EventCycle, eventInfo.EventChannelTimeUnit, out var measuredPeriodUs)
                ? measuredPeriodUs
                : null,
            OptionalCommands: measuredCommands,
            SlaveCanIdRaw: slaveCanIdRaw,
            MasterCanIdRaw: masterCanIdRaw);

        return new XcpCapabilityProbeResult(
            Connect: connect,
            CommMode: commMode,
            Processor: processor,
            Resolution: resolution,
            ListInfo: listInfo,
            EventInfo: eventInfo,
            Measured: measured,
            QueryFailures: queryFailures,
            MaxObservedResponseLength: maxObservedResponseLength);
    }

    /// <summary>
    /// 单个能力查询 + 解码的独立异常边界（S2-T8 评审 Important-1）：解码异常/负响应/
    /// 超时不终止探针——按 (命令, 异常类型, 预期偏差标记) 归因入清单，返回类型默认值。
    /// OperationCanceledException（外部取消）不吞。
    /// </summary>
    private static async Task<(T Value, bool Ok)> TryQueryAsync<T>(
        Func<XcpCtoFrame, Task<byte[]>> send,
        XcpCtoFrame command,
        string commandName,
        Func<byte[], T> decode,
        List<XcpCapabilityQueryFailure> failures)
    {
        try
        {
            return (decode(await send(command).ConfigureAwait(false)), true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures.Add(new XcpCapabilityQueryFailure(commandName, ex.GetType().Name, ExpectedDeviationMarker));
            return (default!, false);
        }
    }

    /// <summary>
    /// OPTIONAL_CMD 逐命令探测。XCP 没有"支持命令清单"响应——标准做法是逐命令发
    /// 良性参数帧，按 ERR_CMD_UNKNOWN 判定不支持；其他负响应（参数被拒等）仍算
    /// 命令存在。全部探测帧 0 效应：地址 0 读、DAQ 未运行态的配置/停表。
    /// <para>
    /// DOWNLOAD 用 0 字节请求（BYTE_COUNT=0，无数据可写）——探针绝不产生真实写流量
    ///（D7 裁决：良性探测 ≠ 写流量；"DOWNLOAD 零入口"约束的是写数据路径）。
    /// </para>
    /// </summary>
    private static async Task ProbeOptionalCommandsAsync(
        Func<XcpCtoFrame, Task<byte[]>> send, List<string> supported)
    {
        foreach (var (name, frame) in MutableCommandProbes())
        {
            try
            {
                _ = await send(frame).ConfigureAwait(false);
            }
            catch (XcpErrorResponseException ex) when (ex.Response.Code == XcpError.CmdUnknown)
            {
                continue; // 从机不支持该命令：不进实测清单，交对账按声明比对。
            }
            catch (XcpErrorResponseException)
            {
                // 其他负响应（参数被拒等）仍算命令存在——漏出探针会违反本链语义
                //（S2-T8 评审 Important-1 修正口径，M1）。
            }

            supported.Add(name);
        }
    }

    /// <summary>良性探测帧全量清单（与 S2-T8 评审后的 xcp-probe 逐帧一致，含 0 字节 DOWNLOAD）。</summary>
    private static IEnumerable<(string Name, XcpCtoFrame Frame)> MutableCommandProbes()
    {
        yield return ("SET_MTA", XcpCommandEncoder.SetMta(0x00, 0x00000000));
        yield return ("UPLOAD", XcpCommandEncoder.Upload(1));
        yield return ("SHORT_UPLOAD", XcpCommandEncoder.ShortUpload(1, 0x00000000, 0x00));
        // 0 字节 DOWNLOAD：BYTE_COUNT=0 + 零填充到 CTO 全长——从机零效应（D7）。
        yield return ("DOWNLOAD", new XcpCtoFrame(new byte[] { XcpPid.Download, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }));
        yield return ("SET_DAQ_PTR", XcpCommandEncoder.SetDaqPtr(0x00, 0x00000000));
        yield return ("WRITE_DAQ", XcpCommandEncoder.WriteDaq(0, 1, 0x00, 0x00000000));
        yield return ("CLEAR_DAQ_LIST", XcpCommandEncoder.ClearDaqList(0x00, 0));
        yield return ("START_STOP_DAQ_LIST", XcpCommandEncoder.StartStopDaqList(0x00, 0));
        yield return ("START_STOP_SYNCH", XcpCommandEncoder.StartStopSynch());
    }
}
