using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Xcp.Calibration;

/// <summary>参数集单条目在当前 ECU/A2L 上的处置结果（D3 结果单行）。</summary>
public enum CalibrationEntryStatus
{
    /// <summary>ECU 当前值与目标一致（原始字节级），跳过不写。</summary>
    NoDifference,

    /// <summary>检测到差异待写（仅 ReconcileAsync 终态）。</summary>
    DiffFound,

    /// <summary>已写入且回读一致。</summary>
    Written,

    /// <summary>写序列失败（负响应/超时），Detail 承载归因。</summary>
    WriteFailed,

    /// <summary>写后回读不一致。</summary>
    ReadBackMismatch,

    /// <summary>写前拒绝（非标定对象 / DataType 缺失 / 编码拒绝）。</summary>
    Rejected,

    /// <summary>对象不在当前 A2L（指纹一致但对象缺失——变体差异）。</summary>
    ObjectMissing,

    /// <summary>地址未映射（段映射覆盖不到，宁可不写不错写）。</summary>
    AddressUnmapped,

    /// <summary>多段对象 v0.1 不支持（连续写需跨段编排，S6 扩展）。</summary>
    MultiSegmentUnsupported,

    /// <summary>多元素对象 v0.1 不支持（Encode 只写首元素，S5 评审 P1-1；S6 扩元素广播）。</summary>
    MultiElementUnsupported,
}

/// <summary>结果单行。</summary>
public sealed record CalibrationReconcileEntry(
    string ObjectName,
    double TargetPhysical,
    CalibrationEntryStatus Status,
    string Detail,
    double? CurrentPhysical);

/// <summary>批量结果单（D3：成功/失败/跳过对账单）。</summary>
public sealed record CalibrationReconcileReport(IReadOnlyList<CalibrationReconcileEntry> Entries)
{
    public int WrittenCount => Count(CalibrationEntryStatus.Written);
    public int NoDifferenceCount => Count(CalibrationEntryStatus.NoDifference);
    public int WriteFailedCount => Count(CalibrationEntryStatus.WriteFailed);
    public int ReadBackMismatchCount => Count(CalibrationEntryStatus.ReadBackMismatch);
    public int RejectedCount => Count(CalibrationEntryStatus.Rejected);
    public int SkippedCount => Count(CalibrationEntryStatus.ObjectMissing)
        + Count(CalibrationEntryStatus.AddressUnmapped)
        + Count(CalibrationEntryStatus.MultiSegmentUnsupported);

    private int Count(CalibrationEntryStatus status) => Entries.Count(e => e.Status == status);
}

/// <summary>
/// S5-T3 对账编排器（spec D3/D4）：加载参数集 → 逐对象读当前值（SET_MTA + UPLOAD，
/// 原始字节级比对）→ 差异清单；<see cref="ApplyAsync"/> 只写差异项（D3 默认），
/// 单项失败不中断批量，重跑幂等（已写项在对账时命中 NoDifference 被跳过）。
/// 地址链路 = 契约 Segments[0].Address → <c>XcpAddressMap.TryTranslate</c> 唯一入口。
/// </summary>
public sealed class CalibrationReconciler : IAsyncDisposable
{
    private const int MaxUploadBytes = XcpCtoFrame.MaxByteLength - 1;

    private readonly XcpCalibrationWriter _writer;
    private readonly XcpMaster _master;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);

    public CalibrationReconciler(XcpCalibrationWriter writer, XcpMaster master)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _master = master ?? throw new ArgumentNullException(nameof(master));
    }

    /// <summary>差异对账（只读不写）：逐对象分类并读当前值。</summary>
    public Task<CalibrationReconcileReport> ReconcileAsync(
        CalibrationParameterSet set, ContractSet contracts, CancellationToken ct = default) =>
        RunAsync(set, contracts, apply: false, onlyChanged: true, ct);

    /// <summary>批量下发（D3：默认只写差异项；单项失败不中断）。</summary>
    public Task<CalibrationReconcileReport> ApplyAsync(
        CalibrationParameterSet set, ContractSet contracts, bool onlyChanged = true, CancellationToken ct = default) =>
        RunAsync(set, contracts, apply: true, onlyChanged, ct);

    private async Task<CalibrationReconcileReport> RunAsync(
        CalibrationParameterSet set, ContractSet contracts, bool apply, bool onlyChanged, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(contracts);

        var entries = new List<CalibrationReconcileEntry>();
        await _singleFlight.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var item in set.Entries)
            {
                ct.ThrowIfCancellationRequested();
                entries.Add(await ProcessOneAsync(item, contracts, apply, onlyChanged, ct).ConfigureAwait(false));
            }
        }
        finally
        {
            _singleFlight.Release();
        }
        return new CalibrationReconcileReport(entries);
    }

    private async Task<CalibrationReconcileEntry> ProcessOneAsync(
        CalibrationEntry item, ContractSet contracts, bool apply, bool onlyChanged, CancellationToken ct)
    {
        // ---- 离线分类（零线上流量）----
        if (!contracts.TryGet(item.Name, out var contract))
            return new(item.Name, item.Physical, CalibrationEntryStatus.ObjectMissing, "对象不在当前 A2L", null);

        if (contract.Category != A2lObjectCategory.Characteristic)
            return new(item.Name, item.Physical, CalibrationEntryStatus.Rejected, $"非标定对象（{contract.Category}）", null);
        if (contract.DataType is null)
            return new(item.Name, item.Physical, CalibrationEntryStatus.Rejected, "元素数据类型未知（RECORD_LAYOUT/FNC_VALUES 缺失）", null);
        if (contract.Segments.Count != 1)
            return new(item.Name, item.Physical, CalibrationEntryStatus.MultiSegmentUnsupported,
                $"多段对象（{contract.Segments.Count} 段）v0.1 不支持", null);
        if (!double.IsFinite(item.Physical))
            return new(item.Name, item.Physical, CalibrationEntryStatus.Rejected, $"物理值非有限（{item.Physical}）", null);
        // S5 评审 P1-1：多元素对象（CURVE/MAP/VAL_BLK）Encode 只写首元素——拒绝静默清零。
        var elementBytes = ByteLayout.SizeOf(contract.DataType.Value);
        if (contract.TotalByteLength > elementBytes)
            return new(item.Name, item.Physical, CalibrationEntryStatus.MultiElementUnsupported,
                $"多元素对象（总 {contract.TotalByteLength}B > 元素 {elementBytes}B）v0.1 不支持", null);

        var targetRaw = new byte[contract.TotalByteLength];
        try
        {
            contract.Encode(item.Physical, targetRaw);
        }
        catch (DecodeException ex)
        {
            return new(item.Name, item.Physical, CalibrationEntryStatus.Rejected, $"编码拒绝：{ex.Message}", null);
        }

        if (!XcpAddressMap.TryTranslate(contracts.Document, contract.Segments[0].Address, out var physical))
            return new(item.Name, item.Physical, CalibrationEntryStatus.AddressUnmapped, "段映射覆盖不到该地址", null);

        // ---- 读当前值（原始字节级比对，物理值比对有浮点舍入歧义）----
        byte[] currentRaw;
        try
        {
            using var sequence = await _master.EnterMemorySequenceAsync(ct).ConfigureAwait(false);
            currentRaw = await ReadRawAsync(contract, (uint)physical, ct).ConfigureAwait(false);
        }
        catch (XcpErrorResponseException ex)
        {
            return new(item.Name, item.Physical, CalibrationEntryStatus.WriteFailed, $"读当前值负响应：{ex.Response.Code}", null);
        }
        catch (XcpTimeoutException ex)
        {
            return new(item.Name, item.Physical, CalibrationEntryStatus.WriteFailed, $"读当前值超时：{ex.Message}", null);
        }

        if (currentRaw.AsSpan().SequenceEqual(targetRaw))
            return new(item.Name, item.Physical, CalibrationEntryStatus.NoDifference, "ECU 当前值与目标一致", contract.Decode(currentRaw));

        var currentPhysical = SafeDecode(contract, currentRaw);

        if (!apply)
            return new(item.Name, item.Physical, CalibrationEntryStatus.DiffFound, "存在差异待写", currentPhysical);

        if (onlyChanged)
        {
            // D3：只写差异项；写失败不中断（逐项独立）。
            var outcome = await _writer.WriteAsync(contract, (uint)physical, item.Physical, 0, ct).ConfigureAwait(false);
            return outcome.Status switch
            {
                CalibrationWriteStatus.Written => new(item.Name, item.Physical, CalibrationEntryStatus.Written, outcome.Detail, currentPhysical),
                CalibrationWriteStatus.ReadBackMismatch => new(item.Name, item.Physical, CalibrationEntryStatus.ReadBackMismatch, outcome.Detail, currentPhysical),
                _ => new(item.Name, item.Physical, CalibrationEntryStatus.WriteFailed, outcome.Detail, currentPhysical),
            };
        }

        // onlyChanged=false：无差异也强制写（D3 可选项）——直接走 writer。
        var forced = await _writer.WriteAsync(contract, (uint)physical, item.Physical, 0, ct).ConfigureAwait(false);
        return forced.Status switch
        {
            CalibrationWriteStatus.Written => new(item.Name, item.Physical, CalibrationEntryStatus.Written, forced.Detail, currentPhysical),
            CalibrationWriteStatus.ReadBackMismatch => new(item.Name, item.Physical, CalibrationEntryStatus.ReadBackMismatch, forced.Detail, currentPhysical),
            _ => new(item.Name, item.Physical, CalibrationEntryStatus.WriteFailed, forced.Detail, currentPhysical),
        };
    }

    private Task<byte[]> ReadRawAsync(ValueContract contract, uint address, CancellationToken ct)
        // S6-T4：分块 UPLOAD 上提协议原语 XcpUploadReader（序列门由调用方持有，行为不变）。
        => XcpUploadReader.ReadAsync(_master, address, contract.TotalByteLength, ct);

    private static double? SafeDecode(ValueContract contract, byte[] raw)
    {
        try
        {
            return contract.Decode(raw);
        }
        catch (DecodeException)
        {
            return null; // 当前值不可解码照实报 null，不阻断对账（§4.8 不返回假值）。
        }
    }

    public async ValueTask DisposeAsync()
    {
        _singleFlight.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
