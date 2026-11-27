using FluentAssertions;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

/// <summary>
/// 抑制正响应位（suppressPosRspMsgIndicationBit，0x80）的协议知识。
/// <para>
/// <b>这是用户生产日志里抓到的真实 bug 的根因：</b>分析器不认识这一位，
/// 把 <c>3E 80</c>（TesterPresent + 抑制）当成「ECU 没响应」，
/// 进而归咎「ECU 内部状态」。按 ISO 14229-1，ECU 对这类请求<b>不应</b>回正响应。
/// </para>
/// </summary>
public sealed class UdsSubFunctionTests
{
    [Theory]
    [InlineData(0x10)]  // DiagnosticSessionControl
    [InlineData(0x11)]  // ECUReset
    [InlineData(0x19)]  // ReadDTCInformation
    [InlineData(0x27)]  // SecurityAccess
    [InlineData(0x28)]  // CommunicationControl
    [InlineData(0x31)]  // RoutineControl
    [InlineData(0x3E)]  // TesterPresent
    [InlineData(0x85)]  // ControlDTCSetting
    public void Services_defined_with_a_sub_function_parameter(byte sid)
    {
        UdsSubFunction.HasSubFunction(sid).Should().BeTrue();
    }

    /// <summary>
    /// <c>0x22</c> / <c>0x2E</c> 的第二字节是 DID 高字节，不是子功能。
    /// 把它们当作带子功能的服务会把 DID 掩码改错（0xF190 → 0x7110）。
    /// </summary>
    [Theory]
    [InlineData(0x22)]
    [InlineData(0x2E)]
    [InlineData(0x34)]
    [InlineData(0x36)]
    [InlineData(0x37)]
    public void Services_without_a_sub_function_parameter(byte sid)
    {
        UdsSubFunction.HasSubFunction(sid).Should().BeFalse();
    }

    /// <summary>
    /// <b>用户的原始 bug：</b><c>3E 80</c> 是「不要回正响应」。
    /// </summary>
    [Fact]
    public void TesterPresent_with_the_suppress_bit_does_not_expect_a_response()
    {
        UdsSubFunction.SuppressesPositiveResponse(new byte[] { 0x3E, 0x80 }).Should().BeTrue();
        UdsSubFunction.ExpectsPositiveResponse(new byte[] { 0x3E, 0x80 }).Should().BeFalse();
    }

    [Fact]
    public void TesterPresent_without_the_suppress_bit_expects_a_response()
    {
        UdsSubFunction.SuppressesPositiveResponse(new byte[] { 0x3E, 0x00 }).Should().BeFalse();
        UdsSubFunction.ExpectsPositiveResponse(new byte[] { 0x3E, 0x00 }).Should().BeTrue();
    }

    /// <summary>
    /// 抑制位对带子功能的服务都成立，不只 TesterPresent。
    /// </summary>
    [Theory]
    [InlineData(0x10, 0x83)]  // 编程会话 + 抑制
    [InlineData(0x11, 0x81)]  // ECUReset hard + 抑制
    [InlineData(0x31, 0x81)]  // RoutineControl start + 抑制
    [InlineData(0x85, 0x82)]  // ControlDTCSetting on + 抑制
    [InlineData(0x28, 0x81)]  // CommunicationControl + 抑制
    public void Suppress_bit_applies_to_every_service_with_a_sub_function(byte sid, byte sub)
    {
        UdsSubFunction.SuppressesPositiveResponse(new[] { sid, sub }).Should().BeTrue();
    }

    /// <summary>
    /// <b>DID 高字节 ≥ 0x80 不能被误判成抑制位。</b>
    /// <c>22 F190</c> 的 <c>0xF1</c> 最高位是 1，若不加服务判定就会判成「抑制」，
    /// 于是所有 F1xx 的读 DID 请求都被认为「不该有响应」。
    /// </summary>
    [Fact]
    public void A_DID_whose_high_byte_has_the_top_bit_set_is_not_a_suppress_request()
    {
        UdsSubFunction.SuppressesPositiveResponse(new byte[] { 0x22, 0xF1, 0x90 }).Should().BeFalse();
        UdsSubFunction.ExpectsPositiveResponse(new byte[] { 0x22, 0xF1, 0x90 }).Should().BeTrue();
    }

    [Theory]
    [InlineData(new byte[] { 0x3E, 0x80 }, 0x00)]
    [InlineData(new byte[] { 0x10, 0x83 }, 0x03)]
    [InlineData(new byte[] { 0x31, 0x81, 0xFF, 0x00 }, 0x01)]
    public void Effective_sub_function_masks_the_suppress_bit(byte[] request, byte expected)
    {
        UdsSubFunction.EffectiveSubFunction(request).Should().Be(expected);
    }

    [Fact]
    public void Effective_sub_function_is_null_for_services_without_one()
    {
        UdsSubFunction.EffectiveSubFunction(new byte[] { 0x22, 0xF1, 0x90 }).Should().BeNull();
    }

    [Theory]
    [InlineData(new byte[] { 0x3E })]           // 只有 SID
    [InlineData(new byte[0])]                   // 空
    public void Too_short_requests_do_not_claim_suppression(byte[] request)
    {
        UdsSubFunction.SuppressesPositiveResponse(request).Should().BeFalse();
    }

    /// <summary>
    /// <b><c>0x2F</c> 的第二字节是 DID 高字节，不是子功能。</b>
    /// 把 0x2F 算成「带子功能的服务」会让 <c>2F F1 90 …</c>（IO 控制 VIN 类 DID）
    /// 被误判成「抑制正响应」——响应被丢、事务变 Suppressed、零结论。
    /// </summary>
    [Theory]
    [InlineData(0x2F)]
    [InlineData(0x22)]
    [InlineData(0x2E)]
    [InlineData(0x34)]
    public void DID_based_services_are_never_suppress_requests(byte sid)
    {
        UdsSubFunction.HasSubFunction(sid).Should().BeFalse();
        UdsSubFunction.SuppressesPositiveResponse(new byte[] { sid, 0xF1, 0x90 }).Should().BeFalse(
            $"0x{sid:X2} 的第二字节是 DID 高字节，不是子功能");
    }
}
