using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Xcp.Calibration;

/// <summary>单对象写回结果（D4：回读失败 ≠ 写失败，状态分列）。</summary>
public enum CalibrationWriteStatus
{
    /// <summary>写 + 回读逐字节一致。</summary>
    Written,

    /// <summary>写前拒绝（非标定对象 / 编码拒绝 / 非有限值）——零线上流量。</summary>
    Rejected,

    /// <summary>写序列失败（负响应耗尽 / 超时 / 应答违规）。</summary>
    WriteFailed,

    /// <summary>写后回读与 Encode 结果不一致（D4：逐字节比对不过）。</summary>
    ReadBackMismatch,
}

/// <summary>写回结果值（Detail 人读归因；状态区红字数据源）。</summary>
public sealed record CalibrationWriteOutcome(CalibrationWriteStatus Status, string Detail)
{
    public static CalibrationWriteOutcome Written() => new(CalibrationWriteStatus.Written, "写 + 回读一致");
    public static CalibrationWriteOutcome Rejected(string detail) => new(CalibrationWriteStatus.Rejected, detail);
    public static CalibrationWriteOutcome WriteFailed(string detail) => new(CalibrationWriteStatus.WriteFailed, detail);
    public static CalibrationWriteOutcome ReadBackMismatch(string detail) => new(CalibrationWriteStatus.ReadBackMismatch, detail);
}

/// <summary>写回内核参数（D1：BUSY 重试；TimeProvider 测试 seam）。</summary>
public sealed class XcpCalibrationWriterOptions
{
    /// <summary>ERR_CMD_BUSY 重试次数（非 BUSY 负响应永不重试）。</summary>
    public int BusyRetryCount { get; init; } = 1;

    /// <summary>BUSY 重试退避间隔。</summary>
    public TimeSpan BusyRetryDelay { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>时间源 seam（测试注入）。</summary>
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>
/// S5-T2 写回内核（spec D1/D4 + T0 裁决；S6-T6 扩多段）：唯一合法 DOWNLOAD 写入口。
/// <para>
/// 写序列 = SET_MTA → DOWNLOAD×⌈n/4⌉（从机 MTA 按 nbytes 自增，T0 源码裁决）→
/// 重臂 SET_MTA → UPLOAD×⌈n/7⌉ 回读 → 与 Encode 结果逐字节比对（D4 每写必回读）。
/// BUSY 负响应重试（D1），非 BUSY 负响应/超时直接失败。single-flight：写序列内
/// 不可插其他命令（XcpMaster 单飞行 + 本层串行）。
/// </para>
/// <para>
/// S6-T6 多段扩展（spec D6）：对象逻辑区间经 <see cref="CalibrationRunPlanner"/>
/// 按 MEMORY_SEGMENT ADDRESS_MAPPING 切 run，逐 run 写 + 分段回读比对；
/// 单 run 失败中断后续 run（宁可不写不错写）。多元素对象以"同值广播全元素"写入
/// （S5 评审 P1-1 预告的元素广播语义——Encode 只写首元素的静默清零问题就此解除）。
/// </para>
/// <para>
/// 拒绝面（零线上流量）：非标定对象 / DataType 缺失 / 物理值非有限 /
/// Encode 拒绝（ConversionUnsupported）/ 字节长度非元素整数倍 / run 规划失败
///（映射空洞、重叠、addrExt≠0）。
/// </para>
/// </summary>
public sealed class XcpCalibrationWriter
{
    /// <summary>单帧 DOWNLOAD 数据上限（CTO 8B − 4B header；与从机 maxCto 校验一致）。</summary>
    private const int MaxDownloadBytes = XcpCtoFrame.MaxByteLength - 4;

    /// <summary>单帧 UPLOAD 数据上限（CTO 8B − PID 1B）。</summary>
    private const int MaxUploadBytes = XcpCtoFrame.MaxByteLength - 1;

    private readonly XcpMaster _master;
    private readonly XcpCalibrationWriterOptions _options;
    private readonly TimeProvider _timeProvider;

    public XcpCalibrationWriter(XcpMaster master, XcpCalibrationWriterOptions? options = null)
    {
        _master = master ?? throw new ArgumentNullException(nameof(master));
        _options = options ?? new XcpCalibrationWriterOptions();
        _timeProvider = _options.TimeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 写单标定对象（S5 原分片路径，调用方已过段映射）：物理值经包侧
    /// <c>ValueContract.Encode</code> 广播编码 → 单地址写序列 → 回读校验。
    /// </summary>
    public async Task<CalibrationWriteOutcome> WriteAsync(
        ValueContract contract, uint physicalAddress, double physicalValue,
        byte addressExtension = 0, CancellationToken ct = default)
    {
        var image = PrepareImage(contract, physicalValue, out var elementCount);
        if (image.Outcome is not null)
            return image.Outcome;

        // ---- 写序列（原子性，S5 评审 P1-2）：整体持有 master 的 MTA 序列门——
        // 序列内不可被卡片写值/批量下发/轮询读等其他 MTA 命令插入。----
        using var sequence = await _master.EnterMemorySequenceAsync(ct).ConfigureAwait(false);
        var outcome = await WriteSliceGuardedAsync(contract.ObjectName, physicalAddress, addressExtension, image.Data!, ct)
            .ConfigureAwait(false);
        return Decorate(outcome, elementCount);
    }

    /// <summary>
    /// 写单标定对象（S6-T6 多段入口）：从 A2L 文档规划写 run（单映射退化为
    /// S5 原分片路径），逐 run 写 + 分段回读比对；规划失败零线上流量拒绝。
    /// </summary>
    public async Task<CalibrationWriteOutcome> WriteAsync(
        ValueContract contract, A2lDocument document, double physicalValue,
        byte addressExtension = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var image = PrepareImage(contract, physicalValue, out var elementCount);
        if (image.Outcome is not null)
            return image.Outcome;

        var runs = CalibrationRunPlanner.PlanWriteRuns(document, contract.Segments[0].Address, contract.TotalByteLength);
        if (runs is null)
            return CalibrationWriteOutcome.Rejected(
                $"对象 '{contract.ObjectName}' 段映射规划失败（映射覆盖不到 / 重叠 / 地址扩展非 0），拒绝写");

        using var sequence = await _master.EnterMemorySequenceAsync(ct).ConfigureAwait(false);
        if (runs.Count == 1)
        {
            var outcome = await WriteSliceGuardedAsync(
                    contract.ObjectName, runs[0].PhysicalAddress, addressExtension, image.Data!, ct)
                .ConfigureAwait(false);
            return Decorate(outcome, elementCount);
        }

        // 多 run：单段失败中断后续（D6 中断语义），Detail 指明失败 run。
        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            var slice = new byte[run.ByteLength];
            Array.Copy(image.Data!, run.SourceOffset, slice, 0, run.ByteLength);
            CalibrationWriteOutcome runOutcome;
            try
            {
                runOutcome = await WriteSliceAsync(run.PhysicalAddress, addressExtension, slice, ct)
                    .ConfigureAwait(false);
            }
            catch (XcpErrorResponseException ex)
            {
                return CalibrationWriteOutcome.WriteFailed(
                    $"对象 '{contract.ObjectName}' 第 {i + 1}/{runs.Count} 段从机负响应：{ex.Response.Code}（后续段中断）");
            }
            catch (XcpTimeoutException ex)
            {
                return CalibrationWriteOutcome.WriteFailed(
                    $"对象 '{contract.ObjectName}' 第 {i + 1}/{runs.Count} 段命令超时：{ex.Message}（后续段中断）");
            }
            catch (InvalidOperationException ex)
            {
                return CalibrationWriteOutcome.WriteFailed(
                    $"对象 '{contract.ObjectName}' 第 {i + 1}/{runs.Count} 段写序列异常：{ex.Message}（后续段中断）");
            }
            catch (ArgumentException ex)
            {
                return CalibrationWriteOutcome.WriteFailed(
                    $"对象 '{contract.ObjectName}' 第 {i + 1}/{runs.Count} 段应答解码异常：{ex.Message}（后续段中断）");
            }

            if (runOutcome.Status is not CalibrationWriteStatus.Written)
            {
                return runOutcome.Status switch
                {
                    CalibrationWriteStatus.ReadBackMismatch =>
                        CalibrationWriteOutcome.ReadBackMismatch(
                            $"对象 '{contract.ObjectName}' 第 {i + 1}/{runs.Count} 段回读不一致：{runOutcome.Detail}（后续段中断）"),
                    _ => CalibrationWriteOutcome.WriteFailed(
                        $"对象 '{contract.ObjectName}' 第 {i + 1}/{runs.Count} 段失败：{runOutcome.Detail}（后续段中断）"),
                };
            }
        }

        return Decorate(CalibrationWriteOutcome.Written(), elementCount,
            $"（{runs.Count} 段逐段写 + 回读一致）");
    }

    // ---------------- 拒绝面 + 广播编码（零线上流量） ----------------

    private (CalibrationWriteOutcome? Outcome, byte[]? Data, int ElementCount) PrepareImage(
        ValueContract contract, double physicalValue)
    {
        ArgumentNullException.ThrowIfNull(contract);

        // ---- 拒绝面（零线上流量）----
        if (contract.Category != A2lObjectCategory.Characteristic)
            return (CalibrationWriteOutcome.Rejected($"对象 '{contract.ObjectName}' 非标定对象（{contract.Category}），无写入口"), null, 0);
        if (contract.DataType is null)
            return (CalibrationWriteOutcome.Rejected($"对象 '{contract.ObjectName}' 元素数据类型未知（RECORD_LAYOUT/FNC_VALUES 缺失）"), null, 0);
        if (!double.IsFinite(physicalValue))
            return (CalibrationWriteOutcome.Rejected($"对象 '{contract.ObjectName}' 物理值非有限（{physicalValue}）"), null, 0);
        if (contract.TotalByteLength <= 0)
            return (CalibrationWriteOutcome.Rejected($"对象 '{contract.ObjectName}' 字节长度为 0"), null, 0);

        var elementBytes = ByteLayout.SizeOf(contract.DataType.Value);
        if (contract.TotalByteLength % elementBytes != 0)
            return (CalibrationWriteOutcome.Rejected(
                $"对象 '{contract.ObjectName}' 字节长度 {contract.TotalByteLength}B 不是元素 {elementBytes}B 的整数倍（布局异常，宁可不写）"), null, 0);

        var raw = new byte[contract.TotalByteLength];
        try
        {
            contract.Encode(physicalValue, raw);
        }
        catch (DecodeException ex)
        {
            return (CalibrationWriteOutcome.Rejected($"对象 '{contract.ObjectName}' 编码拒绝：{ex.Message}"), null, 0);
        }

        var elementCount = contract.TotalByteLength / elementBytes;
        // S6-T6 元素广播：Encode 只填首元素，其余元素以同值重复（不再静默清零）。
        for (var offset = elementBytes; offset < raw.Length; offset += elementBytes)
            Array.Copy(raw, 0, raw, offset, elementBytes);
        return (null, raw, elementCount);
    }

    private (CalibrationWriteOutcome? Outcome, byte[]? Data, int ElementCount) PrepareImage(
        ValueContract contract, double physicalValue, out int elementCount)
    {
        var result = PrepareImage(contract, physicalValue);
        elementCount = result.ElementCount;
        return result;
    }

    /// <summary>广播/分段信息的 Detail 装饰（单元素不加字）。</summary>
    private CalibrationWriteOutcome Decorate(CalibrationWriteOutcome outcome, int elementCount, string? suffix = null)
    {
        if (outcome.Status is not CalibrationWriteStatus.Written || elementCount <= 1)
            return outcome;
        return outcome with
        {
            Detail = outcome.Detail + $"；广播 {elementCount} 元素" + suffix,
        };
    }

    /// <summary>
    /// 单地址切片写序列（S5 原分片路径本体）：SET_MTA → DOWNLOAD 分片 →
    /// 重臂 SET_MTA → UPLOAD 回读逐字节比对。调用方持有 MTA 序列门。
    /// </summary>
    private async Task<CalibrationWriteOutcome> WriteSliceAsync(
        uint physicalAddress, byte addressExtension, byte[] data, CancellationToken ct)
    {
        await SendAndDecodeAsync(XcpCommandEncoder.SetMta(addressExtension, physicalAddress),
            r => { XcpResponseDecoder.SetMta(r); return true; }, ct).ConfigureAwait(false);

        for (var offset = 0; offset < data.Length; offset += MaxDownloadBytes)
        {
            var n = Math.Min(MaxDownloadBytes, data.Length - offset);
            await SendDownloadWithBusyRetryAsync(data.AsSpan(offset, n).ToArray(), ct).ConfigureAwait(false);
        }

        // D4 每写必回读：重臂 SET_MTA（DOWNLOAD 已把 MTA 推到队尾）再 UPLOAD 比对。
        await SendAndDecodeAsync(XcpCommandEncoder.SetMta(addressExtension, physicalAddress),
            r => { XcpResponseDecoder.SetMta(r); return true; }, ct).ConfigureAwait(false);
        var readBack = new byte[data.Length];
        for (var offset = 0; offset < data.Length; offset += MaxUploadBytes)
        {
            var n = Math.Min(MaxUploadBytes, data.Length - offset);
            var response = await SendAndDecodeAsync(XcpCommandEncoder.Upload((byte)n),
                r => XcpResponseDecoder.Upload(r), ct).ConfigureAwait(false);
            response.CopyTo(readBack, offset);
        }

        if (!readBack.AsSpan().SequenceEqual(data))
            return CalibrationWriteOutcome.ReadBackMismatch(
                $"回读不一致（写 {Convert.ToHexString(data)}，读 {Convert.ToHexString(readBack)}）");

        return CalibrationWriteOutcome.Written();
    }

    /// <summary>单 run 写 + 异常→outcome 转换（S5 评审 P3-6 口径，序列门由调用方持有）。</summary>
    private async Task<CalibrationWriteOutcome> WriteSliceGuardedAsync(
        string objectName, uint physicalAddress, byte addressExtension, byte[] data, CancellationToken ct)
    {
        try
        {
            return await WriteSliceAsync(physicalAddress, addressExtension, data, ct).ConfigureAwait(false);
        }
        catch (XcpErrorResponseException ex)
        {
            return CalibrationWriteOutcome.WriteFailed($"对象 '{objectName}' 从机负响应：{ex.Response.Code}");
        }
        catch (XcpTimeoutException ex)
        {
            return CalibrationWriteOutcome.WriteFailed($"对象 '{objectName}' 命令超时：{ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return CalibrationWriteOutcome.WriteFailed($"对象 '{objectName}' 写序列异常：{ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return CalibrationWriteOutcome.WriteFailed($"对象 '{objectName}' 应答解码异常：{ex.Message}");
        }
    }

    private async Task SendDownloadWithBusyRetryAsync(byte[] chunk, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await SendAndDecodeAsync(XcpCommandEncoder.Download(chunk),
                    r => { XcpResponseDecoder.Download(r); return true; }, ct).ConfigureAwait(false);
                return;
            }
            catch (XcpErrorResponseException ex) when (ex.Response.Code == XcpError.CmdBusy && attempt < _options.BusyRetryCount)
            {
                // D1：BUSY 重试 + 退避；其余负响应原样上抛（不重试）。
                var delay = _options.BusyRetryDelay;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<T> SendAndDecodeAsync<T>(XcpCtoFrame command, Func<byte[], T> decode, CancellationToken ct)
    {
        var response = await _master.SendAsync(command, ct).ConfigureAwait(false);
        return decode(response);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
