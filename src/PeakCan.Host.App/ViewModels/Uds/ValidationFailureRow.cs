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
    public ValidationFailureRow(string failedCheck, int responseEvidenceIndex)
    {
        FailedCheck = failedCheck;
        ResponseEvidenceIndex = responseEvidenceIndex;
    }

    /// <summary>校验器给出的原始原因码（<c>DidEcho</c> / <c>SessionTypeEcho</c> …）。</summary>
    public string FailedCheck { get; }

    /// <summary>失败响应的证据序号，可直接回到原始帧。</summary>
    public int ResponseEvidenceIndex { get; }

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

    public string Display => string.Create(CultureInfo.InvariantCulture,
        $"证据 #{ResponseEvidenceIndex}：{Description}（{FailedCheck}）");
}
