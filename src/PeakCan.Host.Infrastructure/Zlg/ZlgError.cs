namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>ZLG API 返回码常量。VCI_* 函数返回 1=成功，0=失败。</summary>
public static class ZlgError
{
    /// <summary>操作成功。</summary>
    public const uint Success = 1;

    /// <summary>操作失败（通用错误）。</summary>
    public const uint Failed = 0;
}