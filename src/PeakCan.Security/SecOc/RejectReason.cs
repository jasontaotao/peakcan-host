namespace PeakCan.Security.SecOc;

/// <summary>
/// 验签拒绝分类（spec D6.5，与 §6.2 分类表一一对应）。
/// <see cref="MissingKey"/> 为防御性枚举：密钥缺失由 D4 启动拦截，
/// 正常流程不可达，保留它是为了让下游（trace 渲染 / Assert*）
/// 能对任何拒绝原因做无默认分支的穷举处理。
/// </summary>
public enum RejectReason
{
    /// <summary>无候选 freshness 通过 MAC 校验（覆盖篡改数据/MAC/FV 字节）。</summary>
    BadMac,

    /// <summary>MAC 合法但 freshness 与最近接受帧相同（同帧重发）。</summary>
    Replay,

    /// <summary>MAC 合法但 freshness 回退到窗口内旧值（回放较旧帧）。</summary>
    FvRollback,

    /// <summary>MAC 合法但 freshness 越出 ±Window（大步进/重同步，v2 同步协议职责）。</summary>
    FvAnomaly,

    /// <summary>防御性：keyId 在 KeyStore 中不存在（正常流程不可达）。</summary>
    MissingKey,

    /// <summary>帧长不足 / 结构非法，无法解析。</summary>
    Malformed,
}