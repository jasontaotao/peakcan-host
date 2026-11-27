namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// 日志里直接观测到的动作方——<b>这是事实，不是判断</b>。
/// <para>
/// <c>0x7F</c> 由 ECU 发出，只能证明「ECU 拒绝了该请求」。它不证明拒绝不合理，
/// 也不证明问题出在 ECU。把这两个概念分开建模，UI 才不会写出一句
/// 工程师无法反驳的判责。
/// </para>
/// </summary>
public enum UdsObservedAction
{
    /// <summary>ECU 回了非 0x78 的负响应。</summary>
    EcuRejected,

    /// <summary>
    /// ECU 回了 <c>0x78</c> 表示仍在处理，且<b>已超过</b>自己声明的 P2* 仍无最终响应。
    /// <para>
    /// <b>判据必须是「真的超窗了」。</b>只收到 <c>0x78</c> 但日志在 P2* 之内就结束，
    /// 属于 <see cref="EcuStillProcessing"/>——那时说「超窗」是没有证据的事实，
    /// 会和同一条 finding 的「延长记录时间」建议直接矛盾。
    /// </para>
    /// </summary>
    EcuRespondedLate,

    /// <summary>
    /// ECU 回了 <c>0x78</c>，日志在其声明的 P2* 之内结束——<b>还在处理，没超时</b>。
    /// <para>
    /// 与 <see cref="EcuRespondedLate"/> 分开：两者都没有最终响应，但一个证明
    /// 「ECU 超了自己承诺的窗口」，另一个只证明「记录时间不够」。
    /// </para>
    /// </summary>
    EcuStillProcessing,

    /// <summary>同 SID 请求重叠，或同一请求收到重复最终响应——配对本身不可确定。</summary>
    ClientOverlapped,

    /// <summary>
    /// 测试仪<b>跳过了</b>某个本该发出的步骤（如刷写该发 <c>0x37</c> 却没发），
    /// 而它自己还在继续工作。
    /// <para>
    /// <b>与 <see cref="ClientOverlapped"/> 分开：</b>重叠指两条请求同时在途；
    /// 这里指的是一条都没重叠，只是序列少了一步。两者在日志上的形态完全不同，
    /// 合成一个值会让「测试仪没发 0x37」显示成「请求重叠」——用户会去查一个
    /// 根本不存在的现象。
    /// </para>
    /// </summary>
    ClientSkippedStep,

    /// <summary>
    /// 测试仪没有等待该请求的响应就发了下一条，或干脆停止发送。
    /// <para>
    /// <b>与 <see cref="ClientOverlapped"/> 分开：</b>重叠指两条请求同时在途；
    /// 这里指的是一条请求发出去之后，测试仪自己不再前进——ECU 没有机会回复，
    /// 因此「ECU 未响应」不成立。
    /// </para>
    /// </summary>
    ClientStopped,

    /// <summary>ISO-TP 重组不完整或日志在传输中途结束。</summary>
    RecordingIncomplete,

    /// <summary>回显校验失败——响应可能来自并行诊断源，或记录本身有误。</summary>
    RecordingInvalid,
}

/// <summary>
/// 优先排查方向——<b>这是推断，不是断言</b>。
/// <para>
/// 命名刻意用 <c>Suspect</c> 而不是 <c>Culprit</c>：报告给的是「先查哪边」，
/// 不是「哪边有 bug」。UI 文案固定为「优先排查」，不得写成「根因是」。
/// </para>
/// </summary>
public enum UdsSuspect
{
    /// <summary>请求参数、key 计算、块序号递增等客户端可自检的东西。</summary>
    ClientParameters,

    /// <summary>会话/安全前置顺序未满足。</summary>
    ClientSequencing,

    /// <summary>ECU 响应慢于其声明的时序。</summary>
    EcuTiming,

    /// <summary>ECU 内部条件/状态不满足，需要 ECU 侧诊断。</summary>
    EcuInternalState,

    /// <summary>日志本身漏帧、截断或混入其它诊断源。</summary>
    LogIntegrity,

    /// <summary>证据不足，不做倾向性判断。</summary>
    Indeterminate,
}

/// <summary>
/// 一个 NRC 的解释结果。消费方只读，不参与构造。
/// <para>
/// <b><see cref="Display"/> 与 <see cref="NextCheck"/> 必须分开。</b>
/// 前者进 finding 的事实行（只陈述协议含义），后者进「下一步」行
/// （可以带方向性建议）。合在一起会让判责混进事实行，见设计文档 §8.8。
/// </para>
/// </summary>
/// <param name="Name">标准枚举名；未知或 OEM 范围值为 null，不借用相近名称。</param>
/// <param name="Suspect">优先排查方向（客户端策略）。</param>
/// <param name="Display">纯协议含义：<c>0x33 SecurityAccessDenied — 安全访问被拒绝</c>。</param>
/// <param name="NextCheck">方向性建议；协议未定义具体条件时为 null。</param>
public sealed record UdsNrcDescription(
    string? Name,
    UdsSuspect Suspect,
    string Display,
    string? NextCheck);

/// <summary>
/// NRC 语义表：标准名称 + 协议含义 + 优先排查侧 + 下一步建议。
/// <para>
/// <b>「优先排查侧」是客户端策略，不是 ISO 14229 的规定。</b>协议只定义 NRC 的
/// 含义，不定义「谁该去查」。这张表反映的是「在只有 CAN 日志的前提下，哪个方向
/// 最可能有可操作的东西」，工程师完全可以基于现场经验反驳并调整。
/// </para>
/// <para>
/// <b>严禁反向使用：</b>不能因为 NRC 是 <c>0x35 InvalidKey</c> 就在 UI 上写
/// 「根因：key 算错」。日志只能证明 ECU 拒绝了 key；算法是否算错需要本地按同一
/// seed 重算比对，不在日志范围内。
/// </para>
/// <para>
/// <b><see cref="Meaning"/> 里不许出现「通常是……」。</b>那句话会被拼进
/// finding 的事实行，等于把判责写进事实。方向性内容一律放
/// <see cref="Next"/>。
/// </para>
/// </summary>
public static class UdsNrcSemantics
{
    private static readonly IReadOnlyDictionary<byte, (string Name, UdsSuspect Suspect, string Meaning, string? Next)>
        Table = new Dictionary<byte, (string, UdsSuspect, string, string?)>
        {
            [0x10] = ("GeneralReject", UdsSuspect.Indeterminate,
                "一般性拒绝，协议未定义具体条件", null),
            [0x11] = ("ServiceNotSupported", UdsSuspect.ClientParameters,
                "ECU 不支持该服务", "确认客户端发送的服务 ID 是否被该 ECU 支持"),
            [0x12] = ("SubFunctionNotSupported", UdsSuspect.ClientParameters,
                "ECU 不支持该子功能", "检查子功能编码是否在 ECU 支持范围内"),
            [0x13] = ("IncorrectMessageLength", UdsSuspect.ClientParameters,
                "请求长度错误", "自检报文长度与格式"),
            [0x14] = ("ResponseTooLong", UdsSuspect.ClientParameters,
                "响应超出请求声明的长度上限", null),
            [0x22] = ("ConditionsNotCorrect", UdsSuspect.EcuInternalState,
                "ECU 条件不满足", "结合会话、安全等级与车辆状态判断 ECU 条件"),
            [0x24] = ("RequestSequenceError", UdsSuspect.ClientSequencing,
                "请求顺序错误", "确认前置步骤是否都已成功执行"),
            [0x25] = ("NoResponseFromSubnet", UdsSuspect.EcuInternalState,
                "ECU 的下级组件无响应", null),
            [0x26] = ("FailurePreventsExecution", UdsSuspect.EcuInternalState,
                "ECU 内部故障阻止执行", null),
            [0x31] = ("RequestOutOfRange", UdsSuspect.ClientParameters,
                "参数越界", "确认 DID / 例程 ID 存在，且当前状态可写"),
            [0x33] = ("SecurityAccessDenied", UdsSuspect.ClientSequencing,
                "安全访问被拒绝", "检查此前同安全等级的 0x27 是否已完成（会话切换或复位会清除解锁状态）"),
            [0x35] = ("InvalidKey", UdsSuspect.ClientParameters,
                "key 未被接受", "检查 key 计算、seed 使用与安全等级参数"),
            [0x36] = ("ExceededNumberOfAttempts", UdsSuspect.ClientParameters,
                "尝试次数超限", "检查是否反复提交了错误的 key"),
            [0x37] = ("RequiredTimeDelayNotExpired", UdsSuspect.ClientSequencing,
                "未满足最小延时", "检查客户端的重试间隔是否满足 ECU 要求"),
            [0x70] = ("UploadDownloadNotAccepted", UdsSuspect.ClientParameters,
                "下载/上传请求未被接受", "检查数据格式与大小参数"),
            [0x71] = ("TransferDataSuspended", UdsSuspect.EcuInternalState,
                "传输被 ECU 暂停", null),
            [0x72] = ("GeneralProgrammingFailure", UdsSuspect.EcuInternalState,
                "ECU 编程失败", "检查写 Flash 的地址与数据是否符合 ECU 要求"),
            [0x73] = ("WrongBlockSequenceCounter", UdsSuspect.ClientParameters,
                "块序号不被接受", "检查客户端块序号递增逻辑，以及日志是否漏帧"),
            [0x78] = ("RequestCorrectlyReceivedResponsePending", UdsSuspect.Indeterminate,
                "ECU 已收到请求、仍在处理（不是失败）", null),
            [0x7E] = ("SubFunctionNotSupportedInActiveSession", UdsSuspect.ClientSequencing,
                "当前会话不允许该子功能", "检查会话切换是否成功"),
            [0x7F] = ("ServiceNotSupportedInActiveSession", UdsSuspect.ClientSequencing,
                "当前会话不允许该服务", "检查会话切换是否成功"),
        };

    public static UdsNrcDescription Describe(byte nrc)
    {
        if (Table.TryGetValue(nrc, out var entry))
            return new(entry.Name, entry.Suspect,
                $"0x{nrc:X2} {entry.Name} — {entry.Meaning}", entry.Next);

        return new(null, UdsSuspect.Indeterminate,
            $"0x{nrc:X2} 未知 NRC（OEM 范围）— 无标准语义", null);
    }

    /// <summary>
    /// <see cref="UdsSuspect"/> 的中文描述。
    /// <para>
    /// <b>放在 Core 而不是各展示层：</b>UI、导出、AI 提示词三处都要说同一件事。
    /// 各存一份必然漂移——改了一处忘另一处，用户会看到界面和导出文件对同一
    /// 条结论给出不同的排查方向。
    /// </para>
    /// <para>
    /// <b>文案固定为「优先排查」语气。</b>这是推断，不是断言。
    /// </para>
    /// </summary>
    /// <summary>
    /// 「直接动作」的中文陈述——<b>只陈述日志观测到的事实，不含倾向性判断</b>。
    /// <para>
    /// <b>放 Core 而不是各展示层：</b>UI 与导出各自拼一份必然漂移（已经漂移过一次：
    /// <c>RecordingIncomplete</c> 在 UI 是「记录不完整」、在导出多了一句括号）。
    /// </para>
    /// </summary>
    public static string? DescribeAction(UdsObservedAction? action) => action switch
    {
        UdsObservedAction.EcuRejected => "ECU 拒绝了请求",
        // 判的是 P2* 窗口（0x78 pending 之后的续期），不是 P2（请求→首个响应）。
        UdsObservedAction.EcuRespondedLate => "ECU 超过自己声明的响应窗口（P2*）仍未给出最终响应",
        UdsObservedAction.EcuStillProcessing => "ECU 已回 0x78 表示仍在处理，日志在其 P2* 内结束",
        UdsObservedAction.ClientOverlapped => "请求重叠 / 重复最终响应",
        UdsObservedAction.ClientSkippedStep => "测试仪跳过了该步骤（自身仍在继续发送）",
        // 两种形态共用这一个值：没等就发下一条 / 干脆停发。
        // 文案不能只说后者——那条 finding 的 Message 里往往写着「20 ms 后就发出了下一条请求」。
        UdsObservedAction.ClientStopped => "测试仪未等待该请求的响应（已发下一条或已停止发送）",
        UdsObservedAction.RecordingIncomplete => "记录不完整（截断或传输未完成）",
        UdsObservedAction.RecordingInvalid => "响应未通过回显校验",
        _ => null,
    };

    public static string DescribeSuspect(UdsSuspect suspect) => suspect switch
    {
        UdsSuspect.ClientParameters => "客户端请求参数（key 计算 / 块序号 / 对象 ID）",
        UdsSuspect.ClientSequencing => "客户端前置顺序（会话切换 / 安全解锁）",
        UdsSuspect.EcuTiming => "ECU 响应时序",
        UdsSuspect.EcuInternalState => "ECU 内部条件（需 ECU 侧诊断）",
        UdsSuspect.LogIntegrity => "日志完整性（漏帧 / 截断 / 地址配置）",
        _ => "无法判定，需更多证据",
    };
}
