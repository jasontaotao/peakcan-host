namespace PeakCan.Host.Core.Uds;

/// <summary>
/// 时序参数（P2 / P2*）当前值的来源，供界面如实标注。
/// <para>
/// 三态而非两态：用户在通讯参数面板填了自定义值后，若界面仍显示「本地默认」，
/// 用户会以为配置没生效；若显示「ECU 协商」，那是更严重的谎报。来源标注错了比
/// 数值错了更难查——用户会相信标注。
/// </para>
/// <para>优先级：<see cref="EcuNegotiated"/> &gt; <see cref="UserConfig"/> &gt; <see cref="Default"/>。</para>
/// </summary>
public enum UdsTimingSource
{
    /// <summary>内置默认（P2 = 50 ms，P2* = 5000 ms）。</summary>
    Default = 0,

    /// <summary>用户在通讯参数面板里配置的值。</summary>
    UserConfig = 1,

    /// <summary>ECU 在 DiagnosticSessionControl 正响应里报告的值（P2Server_max / P2*Server_max）。</summary>
    EcuNegotiated = 2,
}
