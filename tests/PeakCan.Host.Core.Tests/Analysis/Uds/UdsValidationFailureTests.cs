using FluentAssertions;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

/// <summary>
/// 回显校验失败必须留下具体原因。
/// <para>
/// 改前 <see cref="UdsTransactionBuilder"/> 用 <c>out _</c> 丢弃
/// <see cref="UdsResponseValidator.TryValidate"/> 算出的 <c>DidEcho</c> /
/// <c>SessionTypeEcho</c> / <c>BlockSequenceEcho</c>，报告里只剩一个总数
/// 「响应校验失败 3」。用户知道有三帧没过校验，但不知道失败在哪一帧、哪个字段。
/// </para>
/// </summary>
public sealed class UdsValidationFailureTests
{
    private static UdsOfflinePdu Pdu(UdsDirectionRole role, double ts, params byte[] payload)
        => new(role, payload, ts, ts, (int)(ts * 1000), (int)(ts * 1000),
            new[] { (int)(ts * 1000) }, false, true, Array.Empty<string>());

    [Fact]
    public void Did_echo_mismatch_records_specific_check_name()
    {
        var result = new UdsTransactionBuilder().Build(new[]
        {
            Pdu(UdsDirectionRole.Request, 0.0, 0x22, 0xF1, 0x90),
            // 回的是 0xF191，与请求的 0xF190 不符
            Pdu(UdsDirectionRole.Response, 0.01, 0x62, 0xF1, 0x91, 0x41),
        });

        var failure = result.ValidationFailureDetails.Should().ContainSingle().Subject;
        failure.FailedCheck.Should().Be("DidEcho");
        failure.ResponseEvidenceIndex.Should().Be(10, "证据序号来自响应帧的时间戳");
    }

    [Fact]
    public void Session_type_echo_mismatch_is_recorded()
    {
        var result = new UdsTransactionBuilder().Build(new[]
        {
            Pdu(UdsDirectionRole.Request, 0.0, 0x10, 0x03),
            Pdu(UdsDirectionRole.Response, 0.01, 0x50, 0x02, 0x00, 0x32, 0x01, 0xF4),
        });

        result.ValidationFailureDetails.Should().ContainSingle()
            .Which.FailedCheck.Should().Be("SessionTypeEcho");
    }

    [Fact]
    public void Block_sequence_echo_mismatch_is_recorded()
    {
        var result = new UdsTransactionBuilder().Build(new[]
        {
            Pdu(UdsDirectionRole.Request, 0.0, 0x36, 0x03, 0xAA),
            Pdu(UdsDirectionRole.Response, 0.01, 0x76, 0x04),
        });

        result.ValidationFailureDetails.Should().ContainSingle()
            .Which.FailedCheck.Should().Be("BlockSequenceEcho");
    }

    [Fact]
    public void Valid_response_produces_no_failure_detail()
    {
        var result = new UdsTransactionBuilder().Build(new[]
        {
            Pdu(UdsDirectionRole.Request, 0.0, 0x22, 0xF1, 0x90),
            Pdu(UdsDirectionRole.Response, 0.01, 0x62, 0xF1, 0x90, 0x41),
        });

        result.ValidationFailureDetails.Should().BeEmpty();
    }

    /// <summary>
    /// 校验失败帧仍然计入噪声——明细是补充，不是替换。
    /// </summary>
    [Fact]
    public void Validation_failures_still_appear_in_the_pdu_list()
    {
        var result = new UdsTransactionBuilder().Build(new[]
        {
            Pdu(UdsDirectionRole.Request, 0.0, 0x22, 0xF1, 0x90),
            Pdu(UdsDirectionRole.Response, 0.01, 0x62, 0xF1, 0x91, 0x41),
        });

        result.ValidationFailures.Should().ContainSingle();
        result.ValidationFailureDetails.Should().ContainSingle();
    }

    /// <summary>
    /// 明细必须能关联回事务，否则用户知道「F190 的回显错了」却找不到是哪次请求。
    /// </summary>
    [Fact]
    public void Failure_detail_links_back_to_the_transaction()
    {
        var result = new UdsTransactionBuilder().Build(new[]
        {
            Pdu(UdsDirectionRole.Request, 0.0, 0x22, 0xF1, 0x90),
            Pdu(UdsDirectionRole.Response, 0.01, 0x62, 0xF1, 0x91, 0x41),
        });

        var failure = result.ValidationFailureDetails.Single();
        failure.TransactionRequestEvidenceIndex.Should().Be(0,
            "失败的响应属于哪个请求，要能定位回去");
    }
}
