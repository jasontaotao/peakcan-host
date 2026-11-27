namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// 子功能（sub-function）相关的协议知识：哪些服务带子功能、抑制位怎么读。
/// <para>
/// <b>为什么必须集中一份：</b>改前这张表根本不存在——整棵分析树里只有两处
/// 顺手写的 <c>&amp; 0x7F</c> 掩码（<c>0x27</c> 的事实提取、<c>0x27/0x3E</c> 的回显校验），
/// 其余全部按原始字节比较。于是：
/// </para>
/// <list type="bullet">
/// <item><c>3E 80</c>（TesterPresent + 抑制位）被当成「ECU 没响应」报警——按 ISO 14229-1，ECU <b>不应</b>回正响应。</item>
/// <item><c>10 83</c> 的合法回显 <c>50 03</c> 被判回显失败，事务因此判未闭合，再被归咎 ECU。</item>
/// <item>被抑制的请求留在「等待响应」队列里，会吃掉下一条同 SID 请求的响应。</item>
/// </list>
/// <para>
/// 这张表按 ISO 14229-1 的服务定义给出：带子功能参数的服务才允许设置
/// <c>suppressPosRspMsgIndicationBit</c>（0x80）。
/// </para>
/// </summary>
public static class UdsSubFunction
{
    /// <summary>抑制正响应位（suppressPosRspMsgIndicationBit）。</summary>
    public const byte SuppressPositiveResponseBit = 0x80;

    /// <summary>子功能实际取值的掩码（低 7 位）。</summary>
    public const byte SubFunctionMask = 0x7F;

    /// <summary>
    /// 带子功能参数的服务。
    /// <para>
    /// 只有这些服务的第二个字节才是「子功能」，也只有它们才允许设置抑制位。
    /// 对 <c>0x22</c> / <c>0x2E</c> 这类服务，第二个字节是 DID 的高字节，
    /// 拿 <c>&amp; 0x7F</c> 去掩码会把 DID 改错。
    /// </para>
    /// </summary>
    private static readonly HashSet<byte> ServicesWithSubFunction = new()
    {
        0x10, // DiagnosticSessionControl
        0x11, // ECUReset
        0x19, // ReadDTCInformation
        0x27, // SecurityAccess
        0x28, // CommunicationControl
        0x29, // Authentication
        0x31, // RoutineControl
        0x3E, // TesterPresent
        0x83, // AccessTimingParameter
        0x85, // ControlDTCSetting
        0x86, // ResponseOnEvent
        0x87, // LinkControl
        // 0x2F InputOutputControlByIdentifier 不在此列：它的请求格式是
        // `2F <DID_H> <DID_L> <controlParameter>…`，第二字节是 DID 高字节而不是子功能。
        // 把 0x2F 算进来会让 `2F F1 90 …`（IO 控制 VIN 类 DID）被误判成「抑制正响应」，
        // 于是响应被丢、事务变 Suppressed、零结论——与 0x22/0x2E 同类的陷阱。
    };

    /// <summary>该服务是否带子功能参数。</summary>
    public static bool HasSubFunction(byte sid) => ServicesWithSubFunction.Contains(sid);

    /// <summary>
    /// 请求是否声明了「不要回正响应」。
    /// <para>
    /// 只有带子功能的服务才可能设置这一位。对 <c>0x22</c> 这类服务恒为 false，
    /// 否则 DID 高字节为 0x80 以上时会被误判成「抑制响应」。
    /// </para>
    /// </summary>
    public static bool SuppressesPositiveResponse(ReadOnlySpan<byte> request)
        => request.Length >= 2
           && HasSubFunction(request[0])
           && (request[1] & SuppressPositiveResponseBit) != 0;

    /// <summary>
    /// 子功能的实际取值（去掉抑制位）。不带子功能或长度不足时返回 null。
    /// </summary>
    public static byte? EffectiveSubFunction(ReadOnlySpan<byte> request)
        => request.Length >= 2 && HasSubFunction(request[0])
            ? (byte)(request[1] & SubFunctionMask)
            : null;

    /// <summary>
    /// 这个请求是否应当收到正响应。
    /// <para>
    /// <b>这是「未闭合」归因的前置判据。</b>对声明了抑制位的请求，没有响应
    /// 是协议要求的行为，不是 ECU 故障——把它报成「ECU 未在 P2* 内响应」
    /// 会让用户去查一台没问题的 ECU。
    /// </para>
    /// </summary>
    public static bool ExpectsPositiveResponse(ReadOnlySpan<byte> request)
        => !SuppressesPositiveResponse(request);
}
