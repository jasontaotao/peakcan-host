namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// <c>0x27 SecurityAccess</c> 的两个阶段。子功能奇偶决定语义，单独建模比
/// 让消费方各自做 <c>sub &amp; 1</c> 可靠。
/// </summary>
public enum UdsSecurityStep
{
    /// <summary>奇数子功能（0x01 / 0x03 …）：向 ECU 请求 seed。</summary>
    RequestSeed,

    /// <summary>偶数子功能（0x02 / 0x04 …）：把 key 交给 ECU 验证。</summary>
    SendKey,
}

/// <summary>
/// 一个事务操作了什么——协议层对象，不含显示文案。
/// <para>
/// <b>为什么要单独一层：</b>UI 要显示「DID F190」，P1 的状态机要按
/// <see cref="SecurityLevel"/> 判断「这个等级解锁了吗」，finding 生成器要按
/// <see cref="Nrc"/> 查语义表。三者消费同一份事实，避免各自从 payload 重新解析
/// ——那样每加一个消费方就多一处可能与别人不一致的解析逻辑。
/// </para>
/// <para>
/// <b>负响应也必须填满对象字段。</b>改前 <see cref="UdsServiceInterpreter"/>
/// 遇到 <c>0x7F</c> 立即返回，DID / 例程名全空，于是「写 DID 失败」在界面上
/// 退化成「有个 0x2E 失败了」——用户不知道该去查哪个 DID。
/// </para>
/// </summary>
/// <param name="Sid">请求的服务 ID。空 payload 时为 0。</param>
/// <param name="ServiceName">服务名；未知 SID 为 <c>SID 0xNN</c>，不猜相近服务。</param>
/// <param name="SessionType">0x10 的会话类型。</param>
/// <param name="SecurityLevel">0x27 的安全等级（子功能去掉奇偶与抑制位）。</param>
/// <param name="SecurityStep">0x27 处于 seed 阶段还是 key 阶段。</param>
/// <param name="DidId">0x22 / 0x2E 的 DID。</param>
/// <param name="RoutineId">0x31 的例程 ID。</param>
/// <param name="RoutineControlType">0x31 的子功能（start / stop / results）。</param>
/// <param name="BlockSequence">0x36 的块序号。</param>
/// <param name="Nrc">最终负响应的 NRC；正响应或未闭合时为 null。</param>
/// <param name="NrcName"><see cref="Nrc"/> 的标准名；未知值或 null 时为 null。</param>
public sealed record UdsOperationFacts(
    byte Sid,
    string ServiceName,
    byte? SessionType,
    byte? SecurityLevel,
    UdsSecurityStep? SecurityStep,
    ushort? DidId,
    ushort? RoutineId,
    byte? RoutineControlType,
    byte? BlockSequence,
    byte? Nrc,
    string? NrcName);
