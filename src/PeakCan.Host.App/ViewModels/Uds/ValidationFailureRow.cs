using System.Globalization;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// 一条回显校验失败的可读行。
/// <para>
/// <b>为什么要有它：</b>报告原本只有「响应校验失败 N」这个总数。用户知道有
/// 三帧没过校验，但不知道失败在哪一帧、哪个字段——「3 帧没过」和「F190 的
/// 回显不对」是完全不同量级的线索。
/// </para>
/// </summary>
public sealed class ValidationFailureRow
{
    public ValidationFailureRow(
        string failedCheck, int responseEvidenceIndex, int? transactionNumber = null)
    {
        FailedCheck = failedCheck;
        ResponseEvidenceIndex = responseEvidenceIndex;
        TransactionNumber = transactionNumber;
    }

    /// <summary>校验器给出的原始原因码（<c>DidEcho</c> / <c>SessionTypeEcho</c> …）。</summary>
    public string FailedCheck { get; }

    /// <summary>失败响应的证据序号，可直接回到原始帧。</summary>
    public int ResponseEvidenceIndex { get; }

    /// <summary>
    /// 这条失败响应归属的事务序号。
    /// <para>
    /// <b>证据号是帧序号，不是事务号</b>——它落在几十万条帧的空间里，事务表里
    /// 根本不显示，用户对着 <c>证据 #123456</c> 无从下手。带上事务号后，
    /// 点一下就能落到事务表那一行，右栏「原始证据」里再按证据号找到具体帧。
    /// </para>
    /// </summary>
    public int? TransactionNumber { get; }

    /// <summary>中文说明；未知原因码原样显示，不猜。</summary>
    public string Description => FailedCheck switch
    {
        "DidEcho" => "响应回显的 DID 与请求不一致",
        "SessionTypeEcho" => "响应回显的会话类型与请求不一致",
        "SubFunctionEcho" => "响应回显的子功能与请求不一致",
        "RoutineIdEcho" => "响应回显的例程 ID 与请求不一致",
        "BlockSequenceEcho" => "响应回显的块序号与请求不一致",
        "PayloadTooShort" => "响应长度不足",
        "RequestTooShort" => "请求长度不足",
        _ => FailedCheck,
    };

    public string Display => TransactionNumber is { } number
        ? string.Create(CultureInfo.InvariantCulture,
            $"事务 #{number} · 证据 #{ResponseEvidenceIndex}：{Description}（{FailedCheck}）")
        : string.Create(CultureInfo.InvariantCulture,
            $"证据 #{ResponseEvidenceIndex}：{Description}（{FailedCheck}）");
}
