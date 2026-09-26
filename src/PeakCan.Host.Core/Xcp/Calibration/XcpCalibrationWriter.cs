using A2lEditor.Core.Layout;
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
/// S5-T2 写回内核（spec D1/D4 + T0 裁决）：唯一合法 DOWNLOAD 写入口。
/// <para>
/// 写序列 = SET_MTA → DOWNLOAD×⌈n/4⌉（从机 MTA 按 nbytes 自增，T0 源码裁决）→
/// 重臂 SET_MTA → UPLOAD×⌈n/7⌉ 回读 → 与 Encode 结果逐字节比对（D4 每写必回读）。
/// BUSY 负响应重试（D1），非 BUSY 负响应/超时直接失败。single-flight：写序列内
/// 不可插其他命令（XcpMaster 单飞行 + 本层串行）。
/// </para>
/// <para>
/// 拒绝面（零线上流量，§1 宁可不写不错写）：非标定对象 / DataType 缺失 /
/// 物理值非有限 / Encode 拒绝（ConversionUnsupported）。
/// </para>
/// </summary>
public sealed class XcpCalibrationWriter : IAsyncDisposable
{
    /// <summary>单帧 DOWNLOAD 数据上限（CTO 8B − 4B header；与从机 maxCto 校验一致）。</summary>
    private const int MaxDownloadBytes = XcpCtoFrame.MaxByteLength - 4;

    /// <summary>单帧 UPLOAD 数据上限（CTO 8B − PID 1B）。</summary>
    private const int MaxUploadBytes = XcpCtoFrame.MaxByteLength - 1;

    private readonly XcpMaster _master;
    private readonly XcpCalibrationWriterOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);

    public XcpCalibrationWriter(XcpMaster master, XcpCalibrationWriterOptions? options = null)
    {
        _master = master ?? throw new ArgumentNullException(nameof(master));
        _options = options ?? new XcpCalibrationWriterOptions();
        _timeProvider = _options.TimeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 写单标定对象：物理值经包侧 <c>ValueContract.Encode</c> 编码 → 写序列 → 回读校验。
    /// 地址必须已过段映射（调用侧 <c>XcpAddressMap.TryTranslate</c> 唯一入口）。
    /// </summary>
    public async Task<CalibrationWriteOutcome> WriteAsync(
        ValueContract contract, uint physicalAddress, double physicalValue,
        byte addressExtension = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contract);

        // ---- 拒绝面（零线上流量）----
        if (contract.Category != A2lObjectCategory.Characteristic)
            return CalibrationWriteOutcome.Rejected($"对象 '{contract.ObjectName}' 非标定对象（{contract.Category}），无写入口");
        if (contract.DataType is null)
            return CalibrationWriteOutcome.Rejected($"对象 '{contract.ObjectName}' 元素数据类型未知（RECORD_LAYOUT/FNC_VALUES 缺失）");
        if (!double.IsFinite(physicalValue))
            return CalibrationWriteOutcome.Rejected($"对象 '{contract.ObjectName}' 物理值非有限（{physicalValue}）");
        if (contract.TotalByteLength <= 0)
            return CalibrationWriteOutcome.Rejected($"对象 '{contract.ObjectName}' 字节长度为 0");

        var raw = new byte[contract.TotalByteLength];
        try
        {
            contract.Encode(physicalValue, raw);
        }
        catch (DecodeException ex)
        {
            return CalibrationWriteOutcome.Rejected($"对象 '{contract.ObjectName}' 编码拒绝：{ex.Message}");
        }

        // ---- 写序列（single-flight）----
        await _singleFlight.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SendAndDecodeAsync(XcpCommandEncoder.SetMta(addressExtension, physicalAddress),
                r => { XcpResponseDecoder.SetMta(r); return true; }, ct).ConfigureAwait(false);

            for (var offset = 0; offset < raw.Length; offset += MaxDownloadBytes)
            {
                var n = Math.Min(MaxDownloadBytes, raw.Length - offset);
                await SendDownloadWithBusyRetryAsync(raw.AsSpan(offset, n).ToArray(), ct).ConfigureAwait(false);
            }

            // D4 每写必回读：重臂 SET_MTA（DOWNLOAD 已把 MTA 推到队尾）再 UPLOAD 比对。
            await SendAndDecodeAsync(XcpCommandEncoder.SetMta(addressExtension, physicalAddress),
                r => { XcpResponseDecoder.SetMta(r); return true; }, ct).ConfigureAwait(false);
            var readBack = new byte[raw.Length];
            for (var offset = 0; offset < raw.Length; offset += MaxUploadBytes)
            {
                var n = Math.Min(MaxUploadBytes, raw.Length - offset);
                var response = await SendAndDecodeAsync(XcpCommandEncoder.Upload((byte)n),
                    r => XcpResponseDecoder.Upload(r), ct).ConfigureAwait(false);
                response.CopyTo(readBack, offset);
            }

            if (!readBack.AsSpan().SequenceEqual(raw))
                return CalibrationWriteOutcome.ReadBackMismatch(
                    $"对象 '{contract.ObjectName}' 回读不一致（写 {Convert.ToHexString(raw)}，读 {Convert.ToHexString(readBack)}）");

            return CalibrationWriteOutcome.Written();
        }
        catch (XcpErrorResponseException ex)
        {
            return CalibrationWriteOutcome.WriteFailed($"对象 '{contract.ObjectName}' 从机负响应：{ex.Response.Code}");
        }
        catch (XcpTimeoutException ex)
        {
            return CalibrationWriteOutcome.WriteFailed($"对象 '{contract.ObjectName}' 命令超时：{ex.Message}");
        }
        finally
        {
            _singleFlight.Release();
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

    public async ValueTask DisposeAsync()
    {
        _singleFlight.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
