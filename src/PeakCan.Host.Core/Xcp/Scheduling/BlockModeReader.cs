using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Xcp.Scheduling;

/// <summary>
/// 从机块模式（BLOCK SLAVE）实测参数——台架 A-10 数据，S2 无 A2L 声明来源
/// （spec §1：无从机声明，参数只能台架实测）。
/// </summary>
/// <param name="MaxBs">MAX_BS：从机连续块内应答帧数上限（A-10 实测）。</param>
/// <param name="MinSt">MIN_ST：块内相邻应答帧的最小间隔（A-10 实测）。</param>
public sealed record BlockModeParameters(int MaxBs, int MinSt);

/// <summary>
/// 从机块模式读取器（S2-T12 骨架，spec §1 + §3 Scheduling）。
/// <para>
/// <b>禁用守卫（定死）</b>：默认禁用；启用必须同时满足显式
/// <c>enabled: true</c> 与 A-10 实测参数（<see cref="BlockModeParameters"/>）
/// 注入——缺实测参数时启用构造直接抛 <see cref="InvalidOperationException"/>
/// （宁可不采，不许拿猜测参数上线）。禁用态下任何操作调用即抛。
/// </para>
/// <para>
/// <b>S2 边界</b>：仅交付参数化骨架——块模式协议逻辑（多帧应答窗口、
/// MIN_ST 节流、XCP 块模式应答序列）等 A-10 实测数据落地后另行实现，
/// 启用态操作显式抛 <see cref="NotSupportedException"/>（与禁用守卫可区分）。
/// </para>
/// </summary>
public sealed class BlockModeReader
{
    private readonly XcpMaster _master;

    /// <summary>是否已按 A-10 实测参数显式启用。</summary>
    public bool Enabled { get; }

    /// <summary>实测参数（仅启用态非空）。</summary>
    public BlockModeParameters? Parameters { get; }

    /// <summary>
    /// 构造。<paramref name="enabled"/> 缺省 false（A-10 前唯一合法形态）；
    /// 启用则必须携带实测参数，否则抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public BlockModeReader(XcpMaster master, BlockModeParameters? parameters = null, bool enabled = false)
    {
        _master = master ?? throw new ArgumentNullException(nameof(master));

        if (enabled)
        {
            if (parameters is null)
                throw new InvalidOperationException(
                    "BlockModeReader cannot be enabled without measured MAX_BS/MIN_ST parameters " +
                    "from bench run A-10 (slave declares none; guessing is forbidden, spec §1).");
            if (parameters.MaxBs < 1)
                throw new ArgumentOutOfRangeException(nameof(parameters), parameters.MaxBs, "MaxBs must be >= 1.");
            if (parameters.MinSt < 0)
                throw new ArgumentOutOfRangeException(nameof(parameters), parameters.MinSt, "MinSt must be >= 0.");
        }

        Enabled = enabled;
        Parameters = enabled ? parameters : null;
    }

    /// <summary>
    /// 读取一个对象的块模式数据流。S2 骨架：禁用态抛
    /// <see cref="InvalidOperationException"/>（守卫）；启用态抛
    /// <see cref="NotSupportedException"/>（协议路径等 A-10 实测后实现）。
    /// </summary>
    public Task<byte[]> ReadAsync(string objectName, CancellationToken ct = default)
    {
        _ = objectName;
        _ = ct;
        if (!Enabled)
            throw new InvalidOperationException(
                "BlockModeReader is disabled (default until bench run A-10 provides MAX_BS/MIN_ST).");

        throw new NotSupportedException(
            "Block mode protocol path is not implemented in S2 (parameterized skeleton only); " +
            "implementation awaits A-10 bench measurements.");
    }
}
