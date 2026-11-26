namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>ZLG 状态码（STATUS_* in zlgcan.h）。</summary>
public static class ZlgError
{
    public const uint Failed = 0;
    public const uint Success = 1;
    public const uint Online = 2;
    public const uint Offline = 3;
    public const uint Unsupported = 4;
}
