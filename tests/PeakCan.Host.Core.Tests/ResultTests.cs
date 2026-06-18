using FluentAssertions;
using PeakCan.Host.Core;
using Xunit;

namespace PeakCan.Host.Core.Tests;

public class ResultTests
{
    [Fact]
    public void Ok_Has_Success_True_And_Value()
    {
        var r = Result<int>.Ok(42);
        r.IsSuccess.Should().BeTrue();
        r.Value.Should().Be(42);
        r.Error.Should().BeNull();
    }

    [Fact]
    public void Fail_Has_Success_False_And_Error()
    {
        var r = Result<int>.Fail(ErrorCode.InvalidArgument, "bad");
        r.IsSuccess.Should().BeFalse();
        r.Value.Should().Be(0);
        r.Error!.Code.Should().Be(ErrorCode.InvalidArgument);
        r.Error.Message.Should().Be("bad");
    }

    [Fact]
    public void TryGetValue_Returns_True_For_Ok()
    {
        var r = Result<string>.Ok("x");
        r.TryGetValue(out var v).Should().BeTrue();
        v.Should().Be("x");
    }

    [Fact]
    public void TryGetValue_Returns_False_For_Fail()
    {
        var r = Result<string>.Fail(ErrorCode.Unknown, "x");
        r.TryGetValue(out _).Should().BeFalse();
    }

    [Fact]
    public void Match_Invokes_OnOk_For_Success()
    {
        var r = Result<int>.Ok(7);
        var observed = r.Match(v => $"ok:{v}", e => $"fail:{e.Code}");
        observed.Should().Be("ok:7");
    }

    [Fact]
    public void Match_Invokes_OnFail_For_Failure()
    {
        var r = Result<int>.Fail(ErrorCode.NotFound, "missing");
        var observed = r.Match(v => $"ok:{v}", e => $"fail:{e.Code}");
        observed.Should().Be("fail:NotFound");
    }

    [Fact]
    public void Implicit_Conversion_From_Value_Produces_Ok()
    {
        Result<int> r = 5;
        r.IsSuccess.Should().BeTrue();
        r.Value.Should().Be(5);
    }

    [Fact]
    public void ErrorCode_Has_Expected_Default_Ordering()
    {
        ErrorCode.Unknown.Should().Be(ErrorCode.Unknown);
        ((int)ErrorCode.Unknown).Should().Be(0);
    }
}