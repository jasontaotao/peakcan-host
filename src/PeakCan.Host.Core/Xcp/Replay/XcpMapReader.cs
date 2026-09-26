using A2lEditor.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Xcp.Replay;

/// <summary>MAP 读回数据（只读可视化数据源，spec D4）。</summary>
/// <param name="Grid">[yIndex, xIndex] 物理值；离线/失效元素为 NaN。</param>
public sealed record XcpMapData(
    string ObjectName,
    IReadOnlyList<double> XValues,
    IReadOnlyList<double> YValues,
    double[,] Grid,
    string? Unit,
    bool IsOffline,
    string Detail);

/// <summary>
/// S6-T4 MAP 只读读取器（spec D4 定案）：在线 = SET_MTA + UPLOAD 拉对象全元素 + 两轴
/// （序列门内，零 DOWNLOAD，不碰写门禁）；离线 = 仅结构（索引轴 + NaN 网格）。
/// 两轴来源 = A2L 文档模型 AXIS_DESCR→AXIS_PTS（AxisInfo 只承载第一轴，第二轴走文档直读）。
/// </summary>
public static class XcpMapReader
{
    public static XcpMapData ReadOffline(ContractSet contracts, string objectName)
    {
        var (map, mc, xCount, yCount, _, _, _) = Resolve(contracts, objectName);

        var grid = new double[yCount, xCount];
        for (var y = 0; y < yCount; y++)
        for (var x = 0; x < xCount; x++)
            grid[y, x] = double.NaN;

        return new XcpMapData(
            objectName,
            Enumerable.Range(0, xCount).Select(i => (double)i).ToArray(),
            Enumerable.Range(0, yCount).Select(i => (double)i).ToArray(),
            grid, mc.Unit, IsOffline: true,
            "离线模式：无 ECU 数据（仅结构）");
    }

    public static async Task<XcpMapData> ReadOnlineAsync(
        XcpMaster master, ContractSet contracts, string objectName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(master);
        var (map, mc, xCount, yCount, mapAddr, xAddr, yAddr) = Resolve(contracts, objectName);

        var mapRaw = await ReadUnderGateAsync(master, mc, mapAddr, ct).ConfigureAwait(false);
        var xContract = AxisContract(contracts, map, 0);
        var yContract = AxisContract(contracts, map, 1);
        var xRaw = await ReadUnderGateAsync(master, xContract, xAddr, ct).ConfigureAwait(false);
        var yRaw = await ReadUnderGateAsync(master, yContract, yAddr, ct).ConfigureAwait(false);

        // T7 评审 P1-1：网格字节数必须与轴点数乘积一致——不一致的 A2L 让
        // raw.AsSpan 越界裸抛，按 §4.8 fail-loud 拒绝（不猜布局）。
        var elemBytes = ByteLayout.SizeOf(mc.DataType!.Value);
        if (mapRaw.Length < xCount * yCount * elemBytes)
            throw new InvalidOperationException(
                $"MAP '{objectName}' 数据不足：读回 {mapRaw.Length}B < 网格 {xCount}×{yCount}×{elemBytes}B（A2L 布局不一致，拒绝渲染）");

        var grid = new double[yCount, xCount];
        for (var y = 0; y < yCount; y++)
        for (var x = 0; x < xCount; x++)
        {
            var off = (y * xCount + x) * elemBytes;
            grid[y, x] = DecodeElement(mc, mapRaw, off);
        }

        var xValues = DecodeAxis(xContract, xRaw);
        var yValues = DecodeAxis(yContract, yRaw);

        return new XcpMapData(objectName, xValues, yValues, grid, mc.Unit,
            IsOffline: false, $"在线读取：{xCount}×{yCount}（SET_MTA+UPLOAD，零 DOWNLOAD）");
    }

    // ---------------- 解析（零线上流量） ----------------

    private static (A2lCharacteristic Map, ValueContract Contract, int XCount, int YCount,
        uint MapAddr, uint XAddr, uint YAddr) Resolve(ContractSet contracts, string objectName)
    {
        var doc = contracts.Document;
        var map = doc.Modules.SelectMany(m => m.Characteristics).FirstOrDefault(c => c.Name == objectName)
            ?? throw new InvalidOperationException($"MAP 对象不在当前 A2L：'{objectName}'");
        if (map.Type != "MAP")
            throw new InvalidOperationException($"对象 '{objectName}' 不是 MAP（Type={map.Type}）；只读可视化 v0.1 仅支持 MAP");
        if (map.EcuAddress is null)
            throw new InvalidOperationException($"MAP '{objectName}' 缺 ECU_ADDRESS");
        if (map.AxisDescrs.Count != 2)
            throw new InvalidOperationException($"MAP '{objectName}' 需要 2 个 AXIS_DESCR，实际 {map.AxisDescrs.Count}（spec §4.8 不猜）");

        if (!contracts.TryGet(objectName, out var mc) || mc.DataType is null)
            throw new InvalidOperationException($"MAP '{objectName}' 元素数据类型未知（RECORD_LAYOUT/FNC_VALUES 缺失）");
        if (mc.Segments.Count != 1)
            throw new InvalidOperationException($"MAP '{objectName}' 为多段对象（{mc.Segments.Count} 段）——S6-T6 扩展范围");

        var (xCount, xAddr) = ResolveAxis(contracts, map, 0);
        var (yCount, yAddr) = ResolveAxis(contracts, map, 1);
        if (!XcpAddressMap.TryTranslate(doc, mc.Segments[0].Address, out var mapAddr))
            throw new InvalidOperationException($"MAP '{objectName}' 段映射覆盖不到 {mc.Segments[0].Address:X}");
        // T7 评审 P1-3：读原语按 uint 寻址——超界拒绝，绝不静默截断。
        if (mapAddr > uint.MaxValue)
            throw new InvalidOperationException($"MAP '{objectName}' 物理地址 0x{mapAddr:X} 超出 uint 寻址（拒绝读取）");

        return (map, mc, xCount, yCount, (uint)mapAddr, xAddr, yAddr);
    }

    private static (int Count, uint Addr) ResolveAxis(ContractSet contracts, A2lCharacteristic map, int index)
    {
        var descr = map.AxisDescrs[index];
        if (descr.AxisPtsRef is null)
            throw new InvalidOperationException($"MAP '{map.Name}' 轴 {index} 缺 AXIS_PTS_REF");
        if (descr.MaxNumberOfAxisPoints is not { } points)
            throw new InvalidOperationException($"MAP '{map.Name}' 轴 {index} 点数缺失（spec §4.8 不猜）");

        var axis = contracts.Document.Modules.SelectMany(m => m.AxisPts)
            .FirstOrDefault(a => a.Name == descr.AxisPtsRef)
            ?? throw new InvalidOperationException($"MAP '{map.Name}' 轴 {index} 引用的 AXIS_PTS '{descr.AxisPtsRef}' 不存在");
        if (axis.EcuAddress is null)
            throw new InvalidOperationException($"AXIS_PTS '{axis.Name}' 缺 ECU_ADDRESS");
        if (!XcpAddressMap.TryTranslate(contracts.Document, axis.EcuAddress.Value, out var addr))
            throw new InvalidOperationException($"AXIS_PTS '{axis.Name}' 段映射覆盖不到 {axis.EcuAddress.Value:X}");
        if (addr > uint.MaxValue)
            throw new InvalidOperationException($"AXIS_PTS '{axis.Name}' 物理地址 0x{addr:X} 超出 uint 寻址（拒绝读取）");

        return ((int)points, (uint)addr);
    }

    private static ValueContract AxisContract(ContractSet contracts, A2lCharacteristic map, int index)
    {
        var refName = map.AxisDescrs[index].AxisPtsRef!;
        if (!contracts.TryGet(refName, out var contract))
            throw new InvalidOperationException($"AXIS_PTS '{refName}' 无解析期合同（换算不可解）");
        if (contract.DataType is null)
            throw new InvalidOperationException($"AXIS_PTS '{refName}' 元素数据类型未知");
        return contract;
    }

    // ---------------- 读取 / 解码 ----------------

    private static async Task<byte[]> ReadUnderGateAsync(
        XcpMaster master, ValueContract contract, uint address, CancellationToken ct)
    {
        using var sequence = await master.EnterMemorySequenceAsync(ct).ConfigureAwait(false);
        return await XcpUploadReader.ReadAsync(master, address, contract.TotalByteLength, ct).ConfigureAwait(false);
    }

    private static double DecodeElement(ValueContract contract, byte[] raw, int offset)
    {
        try
        {
            return contract.Decode(raw.AsSpan(offset));
        }
        catch (DecodeException)
        {
            return double.NaN; // 单元素换算不可解照实 NaN，不阻断整图（§4.8 不返回假值）
        }
    }

    private static IReadOnlyList<double> DecodeAxis(ValueContract contract, byte[] raw)
    {
        var elemBytes = ByteLayout.SizeOf(contract.DataType!.Value);
        var count = raw.Length / elemBytes;
        var values = new double[count];
        for (var i = 0; i < count; i++)
            values[i] = DecodeElement(contract, raw, i * elemBytes);
        return values;
    }
}


