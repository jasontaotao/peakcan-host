using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>ZLG 状态码 → 规范 ErrorCode 映射。</summary>
public static class ZlgErrorMapper
{
    /// <summary>函数调用是否成功（STATUS_OK）。</summary>
    public static bool IsOk(uint raw) => raw == ZlgError.Success;

    public static (ErrorCode Code, string Message) ToErrorCode(uint raw)
    {
        return raw switch
        {
            ZlgError.Success => (ErrorCode.Ok, "OK"),
            ZlgError.Failed => (ErrorCode.Unknown, "Operation failed"),
            ZlgError.Online => (ErrorCode.Ok, "Device online"),
            ZlgError.Offline => (ErrorCode.HardwareNotAvailable, "Device offline"),
            ZlgError.Unsupported => (ErrorCode.HardwareParameter, "Unsupported operation"),
            _ => (ErrorCode.Unknown, $"Unknown ZLG status 0x{raw:X8}"),
        };
    }
}
