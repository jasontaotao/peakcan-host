namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 报文在**用户可见文本**里的标识格式（spec §10.2）：「名字 (CAN ID)」。
/// <para>
/// <b>适用面</b>：求值器 reason、finding 正文、M1 结论文本、事件表「对象」列。
/// 分散手工拼「名字 (ID)」会漂移成多套口径——实测就是同一个面板里
/// 时间条显示报文名、标定面板只显示 CAN ID。
/// </para>
/// <para>
/// <b>不适用面（有意保留差异）</b>：
/// </para>
/// <list type="bullet">
/// <item>时间条行标签是「ID 名字 (发送者)」——括号里放的是发送节点，
/// 换成 ID 会丢掉发送者信息。</item>
/// <item>补全候选是「ID(名字)」——那里是选择列表，ID 在前便于按序扫。</item>
/// </list>
/// <para>
/// <b>为什么名字与 ID 都要</b>：排查时人想的是「BodyCan 的 NM 报文」，不是
/// 十六进制；但 DBC 可能没给名字（<c>Name</c> 为 null），不同网段也可能重名，
/// 而 CAN ID 唯一且稳定，必须保留。
/// </para>
/// </summary>
public static class NmDisplayName
{
    /// <summary>「名字 (CAN ID)」；名字缺失时退化成纯 CAN ID（不渲染空括号）。</summary>
    public static string Of(string canIdHex, string? name)
        => string.IsNullOrWhiteSpace(name) ? canIdHex : $"{name} ({canIdHex})";
}
