using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// DBC 信号布局摘要（spec §10.1 的补充）。
/// <para>
/// <b>为什么必须有</b>：canonical profile hash 只覆盖报文/节点/布局/时序，
/// 而分析器还直接消费 DBC 的信号定义（<c>signal_change</c> 事件、NMSts 编码交叉校验、
/// <c>SignalDecode</c> 能力）。只改 DBC 信号会让缓存 key 不变、命中旧报告。
/// </para>
/// <para>
/// <b>但也不能用整份文件哈希</b>：那会造成「DBC 只改注释也失效」（spec §10.1 明令移除）。
/// </para>
/// </summary>
public class NmDbcSignalHashTests
{
    private const string BaseDbc = """
        VERSION ""

        BO_ 1280 NM_Message: 8 ECU_A
         SG_ Counter : 0|8@1+ (1,0) [0|255] "" ECU_B
         SG_ Flag : 8|1@1+ (1,0) [0|1] "" ECU_B
        """;

    /// <summary>只改注释（CM_ 行）的同一份 DBC。</summary>
    private const string CommentOnlyDbc = """
        VERSION ""

        BO_ 1280 NM_Message: 8 ECU_A
         SG_ Counter : 0|8@1+ (1,0) [0|255] "" ECU_B
         SG_ Flag : 8|1@1+ (1,0) [0|1] "" ECU_B

        CM_ SG_ 1280 Counter "this comment changed";
        """;

    /// <summary>Counter 的起始位从 0 改成 1（解码结果会变）。</summary>
    private const string StartBitChangedDbc = """
        VERSION ""

        BO_ 1280 NM_Message: 8 ECU_A
         SG_ Counter : 1|8@1+ (1,0) [0|255] "" ECU_B
         SG_ Flag : 8|1@1+ (1,0) [0|1] "" ECU_B
        """;

    /// <summary>新增一个信号（SignalDecode 能力与事件都会变）。</summary>
    private const string SignalAddedDbc = """
        VERSION ""

        BO_ 1280 NM_Message: 8 ECU_A
         SG_ Counter : 0|8@1+ (1,0) [0|255] "" ECU_B
         SG_ Flag : 8|1@1+ (1,0) [0|1] "" ECU_B
         SG_ Extra : 16|8@1+ (1,0) [0|255] "" ECU_B
        """;

    /// <summary>信号声明顺序对调（语义不变）。</summary>
    private const string ReorderedDbc = """
        VERSION ""

        BO_ 1280 NM_Message: 8 ECU_A
         SG_ Flag : 8|1@1+ (1,0) [0|1] "" ECU_B
         SG_ Counter : 0|8@1+ (1,0) [0|255] "" ECU_B
        """;

    /// <summary>Factor 从 1 改成 2（解码出的物理值会变）。</summary>
    private const string FactorChangedDbc = """
        VERSION ""

        BO_ 1280 NM_Message: 8 ECU_A
         SG_ Counter : 0|8@1+ (2,0) [0|255] "" ECU_B
         SG_ Flag : 8|1@1+ (1,0) [0|1] "" ECU_B
        """;

    private static DbcDocument Parse(string text) => DbcParser.Parse(text).Value!;

    [Fact]
    public void Compute_SameContent_IsStable()
    {
        Assert.Equal(
            NmDbcSignalHash.Compute(Parse(BaseDbc)),
            NmDbcSignalHash.Compute(Parse(BaseDbc)));
    }

    [Fact]
    public void Compute_CommentOnlyChange_DoesNotChangeHash()
    {
        // spec §10.1：DBC 只改注释不得让缓存失效。
        Assert.Equal(
            NmDbcSignalHash.Compute(Parse(BaseDbc)),
            NmDbcSignalHash.Compute(Parse(CommentOnlyDbc)));
    }

    [Fact]
    public void Compute_SignalOrderChange_DoesNotChangeHash()
    {
        // 声明顺序不影响解码语义；不该产生新的缓存项。
        Assert.Equal(
            NmDbcSignalHash.Compute(Parse(BaseDbc)),
            NmDbcSignalHash.Compute(Parse(ReorderedDbc)));
    }

    [Fact]
    public void Compute_StartBitChange_ChangesHash()
    {
        // 这是 HIGH 缺陷的核心场景：改了信号位布局，NMSts 交叉校验与 signal_change
        // 事件都会变，缓存必须失效。
        Assert.NotEqual(
            NmDbcSignalHash.Compute(Parse(BaseDbc)),
            NmDbcSignalHash.Compute(Parse(StartBitChangedDbc)));
    }

    [Fact]
    public void Compute_SignalAdded_ChangesHash()
    {
        Assert.NotEqual(
            NmDbcSignalHash.Compute(Parse(BaseDbc)),
            NmDbcSignalHash.Compute(Parse(SignalAddedDbc)));
    }

    [Fact]
    public void Compute_FactorChange_ChangesHash()
    {
        Assert.NotEqual(
            NmDbcSignalHash.Compute(Parse(BaseDbc)),
            NmDbcSignalHash.Compute(Parse(FactorChangedDbc)));
    }

    [Fact]
    public void Compute_NoDbc_DiffersFromAnyDbc()
    {
        Assert.NotEqual("no-dbc", NmDbcSignalHash.Compute(Parse(BaseDbc)));
    }

    [Fact]
    public void Compute_NullDbc_IsStable()
    {
        Assert.Equal(NmDbcSignalHash.Compute(null), NmDbcSignalHash.Compute(null));
    }
}
