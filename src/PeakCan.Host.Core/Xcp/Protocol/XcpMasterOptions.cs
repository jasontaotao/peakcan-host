using PeakCan.HIL.Core;

namespace PeakCan.Host.Core.Xcp.Protocol;

/// <summary>
/// XcpMaster 行为参数（spec §3 Protocol 超时策略）。
/// 参数走构造器以便在构造期立即校验（init 属性在对象初始化器阶段赋值，
/// 构造器校验不到）。主站发送 CAN ID 必显式给出——协议引擎不做台架默认值
/// 的静默兜底，且响应分流按 PID 首字节进行（spec 写死禁止按 CAN ID 分流），
/// ID 只用于构造发送帧。
/// </summary>
public sealed class XcpMasterOptions
{
    /// <summary>T1 命令超时默认值（A2L 声明 2000ms）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(2000);

    /// <summary>超时后的默认重试次数（N=1，即最多 2 次尝试）。</summary>
    public const int DefaultMaxRetries = 1;

    /// <summary>主站 → 从机命令帧 CAN ID（bench 基线 0x18FFF667，29 位扩展）。</summary>
    public CanId MasterCanId { get; }

    /// <summary>T1 命令超时。超时即判失败并中止 pending。</summary>
    public TimeSpan Timeout { get; }

    /// <summary>超时后的重试次数。重试只覆盖超时路径；负响应立即上抛不重试。</summary>
    public int MaxRetries { get; }

    public XcpMasterOptions(CanId masterCanId, TimeSpan? timeout = null, int maxRetries = DefaultMaxRetries)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), effectiveTimeout, "Timeout must be positive.");
        if (maxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetries), maxRetries, "MaxRetries must be non-negative.");

        MasterCanId = masterCanId;
        Timeout = effectiveTimeout;
        MaxRetries = maxRetries;
    }
}
