using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// DBC 信号布局的语义摘要（spec §10.1 的补充：DBC 内容不全部经由 <see cref="NmProfile"/> 进 key）。
/// <para>
/// <b>为什么需要它</b>：<see cref="NmCanonicalProfileHash"/> 覆盖的是报文/节点/布局/时序，
/// 但分析器还直接消费 DBC 的信号定义——
/// <list type="bullet">
/// <item><c>NmSignalSummary.Build</c> 把信号解码结果写成 <c>signal_change</c> 事件；</item>
/// <item><c>NmStsResolver</c> 用 DBC 信号编码交叉校验 NMSts 位，决定时间条按状态切段还是退回推断；</item>
/// <item><c>NmCapabilityGate</c> 的 <c>SignalDecode</c> 能力取决于报文有没有信号。</item>
/// </list>
/// 这些都不在 <see cref="NmProfile.Messages"/> 里，只改 DBC 信号会让 key 不变、命中旧报告。
/// </para>
/// <para>
/// <b>为什么不直接拼 DBC 文件哈希</b>：那会造成「DBC 只改注释也失效」（spec §10.1 明令移除）。
/// 这里只取影响解码结果的字段，排除 <c>Comment</c> / <c>Unit</c> / <c>Receivers</c> /
/// <c>ValueTableName</c> 等纯展示信息。
/// </para>
/// </summary>
public static class NmDbcSignalHash
{
    /// <summary>
    /// 计算摘要。<paramref name="dbc"/> 为 null 时返回固定串
    /// （无 DBC 与「有 DBC」必须产生不同 key）。
    /// </summary>
    public static string Compute(DbcDocument? dbc)
    {
        if (dbc is null) return "no-dbc";

        var builder = new StringBuilder();
        // 按报文 ID 排序：DBC 里的声明顺序不该影响 key。
        foreach (var message in dbc.Messages.OrderBy(m => m.Id))
        {
            // 报文身份 + 信号集合。报文本身也参与：新增/删除报文会改变 SignalDecode 能力。
            builder.Append(CultureInfo.InvariantCulture, $"{message.Id:X8}/{message.Signals.Count}");
            // 信号按 (起始位, 名称) 排序：同一报文的信号声明顺序不该影响 key。
            foreach (var signal in message.Signals
                         .OrderBy(s => s.StartBit).ThenBy(s => s.Name, StringComparer.Ordinal))
            {
                // 只取影响「解码出什么值」与「位布局是否冲突」的字段。
                builder.Append(CultureInfo.InvariantCulture,
                    $"|{signal.Name},{signal.StartBit},{signal.Length},{(int)signal.Order}," +
                    $"{(int)signal.ValueType},{signal.Factor:R},{signal.Offset:R}");
            }
            builder.Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
