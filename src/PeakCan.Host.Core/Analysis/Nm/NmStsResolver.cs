using System.Globalization;
using PeakCan.HIL.Core.Dbc;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 一条报文上 NMSts 位的读取器（spec §7.5.1）。构造前已确认编码可用；
/// 逐帧仍可能读不出（DLC 不足），由 <see cref="Read"/> 返回 null 表达。
/// </summary>
public sealed record NmStsProbe(byte Byte, int Bit)
{
    /// <summary>
    /// 读一帧的 NMSts：true = 1，false = 0，<b>null = 该帧读不出</b>。
    /// <para>
    /// null 不得被折叠成 false：DLC 不足的帧没有状态位，把它当成 NMSts=0
    /// 会凭空画出一段 Repeat Message State（spec §7.5.1.2）。
    /// </para>
    /// </summary>
    public bool? Read(NmFrame frame)
    {
        var data = frame.Frame.Data;
        if (data.Length <= Byte) return null;
        return ((data[Byte] >> Bit) & 1) != 0;
    }
}

/// <summary>
/// 把 profile 的 NMSts 位声明解析成可用的 <see cref="NmStsProbe"/>（spec §7.5.1 / §7.5.1.2）。
/// <para>
/// 三态结果，必须区分：
/// <list type="bullet">
/// <item>profile 未声明 NMSts → 返回 null 且原因为 null：这是<b>正常回退</b>，退回时间推断。</item>
/// <item>声明了且编码可用 → 返回 probe。</item>
/// <item>声明了但编码与 DBC 冲突 → 返回 null 且原因为非 null：必须<b>显式上报</b>，
/// 否则用户以为时间条是按状态位画的。</item>
/// </list>
/// </para>
/// </summary>
public static class NmStsResolver
{
    /// <summary>spec §7.5.1.2：DBC 信号编码与「单 bit 无符号 Intel」不符时的浮点余量。</summary>
    private const double Epsilon = 1e-9;

    /// <summary>
    /// 解析一条报文的 NMSts 位。
    /// </summary>
    /// <param name="roles">展平后的企标位角色；null = 未选企标。</param>
    /// <param name="dbc">DBC 文档；null = 无 DBC，此时只按 profile 声明读，不做编码交叉校验。</param>
    /// <param name="canIdHex">报文 ID（形如 <c>0x18FFFAF4</c>）。</param>
    /// <param name="isExtended">帧类型；标准/扩展同数值 ID 是两条报文。</param>
    /// <param name="unavailableReason">
    /// 不可用的原因；null 表示「未声明」（正常回退）或「可用」。
    /// 调用方必须把非 null 值写进报告的未知项，不得丢弃。
    /// </param>
    public static NmStsProbe? TryResolve(
        IReadOnlyList<NmBitRole>? roles,
        DbcDocument? dbc,
        string canIdHex,
        bool isExtended,
        out string? unavailableReason)
    {
        unavailableReason = null;

        var role = NmProfilePrecedence.FindNmStsRole(roles);
        if (role is null) return null; // 未声明：正常回退，不是缺陷。

        // profile 只描述「哪个 bit」，编码信息只能来自 DBC。
        // 没有 DBC 时不臆造冲突：profile 的单 bit 声明本身就是充分的读法。
        if (dbc is null) return new NmStsProbe(role.Byte, role.Bit);

        if (!TryFindMessage(dbc, canIdHex, isExtended, out var message))
            return new NmStsProbe(role.Byte, role.Bit);

        var absoluteBit = role.Byte * 8 + role.Bit;
        var signal = message.Signals.FirstOrDefault(
            s => Math.Abs(s.StartBit - absoluteBit) < Epsilon);
        // DBC 没在这一位上定义信号：不构成冲突，按 profile 读。
        // （spec §7.5.1.2 表里的「信号未定义」指 profile 与 DBC 都没有该位。）
        if (signal is null) return new NmStsProbe(role.Byte, role.Bit);

        // 以下三条任何一条不满足，`data[byte] >> bit` 的读法就不再等价于该信号的值。
        if (signal.Length != 1)
        {
            unavailableReason =
                $"DBC 信号 {signal.Name} 位宽为 {signal.Length}，不是单 bit（spec §7.5.1.2）";
            return null;
        }
        if (signal.Order != ByteOrder.LittleEndian)
        {
            unavailableReason =
                $"DBC 信号 {signal.Name} 为 Motorola 字节序，与 NMSts 的 Intel 读法不一致（spec §7.5.1.2）";
            return null;
        }
        if (signal.ValueType != DbcValueType.Unsigned)
        {
            unavailableReason =
                $"DBC 信号 {signal.Name} 为 {signal.ValueType}，NMSts 必须是无符号位（spec §7.5.1.2）";
            return null;
        }

        return new NmStsProbe(role.Byte, role.Bit);
    }

    /// <summary>
    /// 按 ID 与帧类型查 DBC 报文。与 <see cref="NmSignalSummary"/> 同一口径：
    /// <see cref="DbcDocument.MessagesById"/> 的键抹掉了 IDE 位，必须再核对格式，
    /// 否则会拿标准报文的信号表去校验扩展帧。
    /// </summary>
    private static bool TryFindMessage(
        DbcDocument dbc, string canIdHex, bool isExtended, out Message message)
    {
        message = null!;
        if (!canIdHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return false;
        if (!uint.TryParse(canIdHex.AsSpan(2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var id)) return false;
        if (!dbc.MessagesById.TryGetValue(id, out var found)) return false;
        if (((found.Id & 0x80000000u) != 0) != isExtended) return false;
        message = found;
        return true;
    }
}
