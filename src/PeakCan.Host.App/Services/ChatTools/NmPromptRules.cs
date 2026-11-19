namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// M1 NM 提示词规则。与 M2 规则分开维护，避免 M1 引用企标/profile。
/// </summary>
public static class NmPromptRules
{
    public static string BuildM1() => """
网络管理（NM）分析规则：
1. 任何 NM/网络管理/睡眠/唤醒/掉线问题，先调用 get_nm_profile；只分析 DBC 标记为 authoritative 或用户在「网络管理 → 补全」对话框确认过的报文。
2. 只引用工具返回的数字与 *_label；禁止自行换算时间。
3. “trace 里没有 NM 帧”只说明该窗口/文件内没有，不等于睡眠/掉线。
4. NID/CBV/PN/用户数据/睡眠/企标：先看 capabilities 与 missing_inputs；缺输入就说缺什么、影响哪条结论、怎么补（打开补全对话框或提供 sidecar），未补前不下结论，不许脑补。
5. 只列周期偏差、静默、观察突发、原始字节/位/信号变化、配置冲突，并给证据时间。
6. 允许给根因假设，但必须标“假设”，并给下一步排查建议。
7. 与 anomaly_scan 的分工：NM 行为由 NM 工具负责；anomaly_scan 只在用户明确要求全信号扫描时使用。
8. 可以用现有 seek_to / propose_to_watch_list 跳转或加 watch；不改 DBC/配置。
9. 不替用户填 sidecar、不直接改配置；用户口述的值只能复述并请其在对话框确认。
""";
}
