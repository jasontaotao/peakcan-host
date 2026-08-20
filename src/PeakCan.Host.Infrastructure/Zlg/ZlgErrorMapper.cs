using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>ZLG 错误码 → 规范 ErrorCode 映射。</summary>
public static class ZlgErrorMapper
{
    /// <summary>VCI_* 函数返回码是否为成功。</summary>
    public static bool IsOk(uint raw) => raw == ZlgError.Success;

    /// <summary>
    /// 将 VCI_* 函数返回码映射为 (ErrorCode, message)。
    /// 返回 1=成功，0=失败（通用错误），其他值=未知。
    /// </summary>
    public static (ErrorCode Code, string Message) ToErrorCode(uint raw)
    {
        return raw switch
        {
            ZlgError.Success => (ErrorCode.Ok, "OK"),
            ZlgError.Failed => (ErrorCode.Unknown, "Operation failed"),
            _ => (ErrorCode.Unknown, $"Unknown ZLG status 0x{raw:X8}"),
        };
    }
}