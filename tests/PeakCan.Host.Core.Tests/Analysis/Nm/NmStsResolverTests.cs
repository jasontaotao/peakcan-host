using PeakCan.HIL.Core.Dbc;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §7.5.1.2：NMSts 位可读性校验。必须穷尽「编码与读法不等价」的情形——
/// 用 <c>data[byte] &gt;&gt; bit</c> 读一个 2 bit / 有符号 / Motorola 信号会读出错误值，
/// 而错误的状态位比没有状态位更糟：它会让时间条画出看起来很确定但实际错的段。
/// </summary>
public class NmStsResolverTests
{
    private static readonly NmBitRole[] Roles = [new NmBitRole(3, 0, "NmSts")];

    /// <summary>构造一条含单个信号的 DBC 报文。</summary>
    private static DbcDocument DbcWith(Signal signal, uint id = 0x500)
    {
        var message = new Message(id, "NM", 8, "A", new[] { signal },
            IsMultiplexed: false, MultiplexorSignalIndex: null);
        return new DbcDocument(
            "v", Array.Empty<Node>(), new[] { message },
            new Dictionary<uint, Message> { [id] = message },
            new Dictionary<string, ValueTable>());
    }

    private static Signal NmStsSignal(
        ushort startBit = 24, byte length = 1,
        ByteOrder order = ByteOrder.LittleEndian,
        DbcValueType valueType = DbcValueType.Unsigned)
        => new("BMS_NMSts", startBit, length, order, valueType,
            1, 0, 0, 1, "bit", Array.Empty<string>());

    [Fact]
    public void TryResolve_NotDeclared_ReturnsNullWithoutReason()
    {
        // 未声明是**正常回退**，不是缺陷：必须没有原因串，否则报告会出现无谓的告警。
        var probe = NmStsResolver.TryResolve(
            Array.Empty<NmBitRole>(), null, "0x500", false, out var reason);

        Assert.Null(probe);
        Assert.Null(reason);
    }

    [Fact]
    public void TryResolve_DeclaredNoDbc_UsesProfileBit()
    {
        // 没有 DBC 时 profile 的单 bit 声明本身就是充分的读法，不臆造冲突。
        var probe = NmStsResolver.TryResolve(Roles, null, "0x500", false, out var reason);

        Assert.NotNull(probe);
        Assert.Equal(3, probe!.Byte);
        Assert.Equal(0, probe.Bit);
        Assert.Null(reason);
    }

    [Fact]
    public void TryResolve_ValidSingleBitSignal_IsUsable()
    {
        var dbc = DbcWith(NmStsSignal());

        var probe = NmStsResolver.TryResolve(Roles, dbc, "0x500", false, out var reason);

        Assert.NotNull(probe);
        Assert.Null(reason);
    }

    [Fact]
    public void TryResolve_MultiBitSignal_IsUnavailableWithReason()
    {
        // 位宽 ≠ 1：`>> bit & 1` 只取最低位，与信号真值不等价。
        var dbc = DbcWith(NmStsSignal(length: 2));

        var probe = NmStsResolver.TryResolve(Roles, dbc, "0x500", false, out var reason);

        Assert.Null(probe);
        Assert.Contains("位宽", reason);
    }

    [Fact]
    public void TryResolve_MotorolaSignal_IsUnavailableWithReason()
    {
        // 字节序不匹配：Intel 与 Motorola 的 bit 编号方向相反，同一个 (byte,bit) 指向不同位。
        var dbc = DbcWith(NmStsSignal(order: ByteOrder.BigEndian));

        var probe = NmStsResolver.TryResolve(Roles, dbc, "0x500", false, out var reason);

        Assert.Null(probe);
        Assert.Contains("Motorola", reason);
    }

    [Fact]
    public void TryResolve_SignedSignal_IsUnavailableWithReason()
    {
        var dbc = DbcWith(NmStsSignal(valueType: DbcValueType.Signed));

        var probe = NmStsResolver.TryResolve(Roles, dbc, "0x500", false, out var reason);

        Assert.Null(probe);
        Assert.Contains("无符号", reason);
    }

    [Fact]
    public void TryResolve_SignalAbsentFromDbc_IsNotAConflict()
    {
        // DBC 没在这一位定义信号 → 不构成冲突（spec §7.5.1.2 的「信号未定义」
        // 指 profile 与 DBC 都没有该位）。
        var other = new Signal("SomethingElse", 40, 8, ByteOrder.LittleEndian,
            DbcValueType.Unsigned, 1, 0, 0, 255, "", Array.Empty<string>());
        var dbc = DbcWith(other);

        var probe = NmStsResolver.TryResolve(Roles, dbc, "0x500", false, out var reason);

        Assert.NotNull(probe);
        Assert.Null(reason);
    }

    [Fact]
    public void TryResolve_StdAndExtendedSameNumericId_DoesNotCrossMatch()
    {
        // 标准 0x500 与扩展 0x500 是两条报文；拿错信号表会校验错对象。
        var dbc = DbcWith(NmStsSignal(length: 2), id: 0x500);

        // 扩展帧查询：DBC 里只有标准帧，格式不符 → 视为「DBC 无此报文」，不判冲突。
        var probe = NmStsResolver.TryResolve(Roles, dbc, "0x500", isExtended: true, out var reason);

        Assert.NotNull(probe);
        Assert.Null(reason);
    }

    [Fact]
    public void Read_ShortDlc_ReturnsNullNotFalse()
    {
        // DLC 不足必须返回 null。折叠成 false 会凭空画出一段 Repeat Message State。
        var probe = new NmStsProbe(3, 0);
        var def = new NmMessageDef("0x500", false, "NM", "A", 8, true, ["test"]);
        var frame = new NmFrame(def,
            new PeakCan.Host.Core.Replay.ReplayFrame(0, 0x500, 2, new byte[] { 0xF4, 0 }, 0));

        Assert.Null(probe.Read(frame));
    }

    [Fact]
    public void Read_ReadsBitZeroAndOne()
    {
        var probe = new NmStsProbe(3, 0);
        var def = new NmMessageDef("0x500", false, "NM", "A", 8, true, ["test"]);

        var zero = new NmFrame(def, new PeakCan.Host.Core.Replay.ReplayFrame(
            0, 0x500, 8, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }, 0));
        var one = new NmFrame(def, new PeakCan.Host.Core.Replay.ReplayFrame(
            0, 0x500, 8, new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 }, 0));

        Assert.False(probe.Read(zero));
        Assert.True(probe.Read(one));
    }
}
