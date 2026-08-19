using PeakCan.HIL.Core.HIL.Expressions;
using Xunit;

namespace PeakCan.HIL.Core.Tests.HIL.Expressions;

/// <summary>
/// dtcPresent(code) 内置函数注册表单元测试（方案 B 预查注入）。
/// registry 持可变 HashSet&lt;uint&gt;（引擎预查填 active DTC codes），TryInvoke 同步读 set。
/// 语义对齐 AssertDtcStepExecutor：bit0(testFailed)/bit2(confirmedDTC) 置位 = active。
/// </summary>
public class DtcPresenceFunctionRegistryTests
{
    private static DtcPresenceFunctionRegistry NewReg(params uint[] present)
        => new(new HashSet<uint>(present));

    [Fact]
    public void DtcPresent_CodeInSet_ReturnsTrue()
    {
        var r = NewReg(0x1234);
        var ok = r.TryInvoke("dtcPresent", new[] { ExpressionValue.FromLong(0x1234) }, out var result);

        Assert.True(ok, "函数存在 + 参数有效 → TryInvoke 返 true");
        Assert.True(result.AsBool, "code 在预查 set 中 → DTC present");
    }

    [Fact]
    public void DtcPresent_CodeNotInSet_ReturnsTrueButFalse()
    {
        var r = NewReg(0x1234);
        var ok = r.TryInvoke("dtcPresent", new[] { ExpressionValue.FromLong(0x5678) }, out var result);

        Assert.True(ok, "函数存在 + 参数有效 → TryInvoke 返 true（区别于函数不存在）");
        Assert.False(result.AsBool, "code 不在 set → DTC absent");
    }

    [Fact]
    public void DtcPresent_DoubleArg_ReturnsTrue()
    {
        // FrameStatisticsFunctionRegistry.TryParseUint 接受 double（整数值）—— dtcPresent 对齐
        var r = NewReg(0x1234);
        var ok = r.TryInvoke("dtcPresent", new[] { ExpressionValue.FromDouble(0x1234) }, out var result);

        Assert.True(ok);
        Assert.True(result.AsBool);
    }

    [Fact]
    public void DtcPresent_NonIntegerArg_ReturnsFalse()
    {
        var r = NewReg(0x1234);
        var ok = r.TryInvoke("dtcPresent", new[] { ExpressionValue.FromString("x") }, out _);

        Assert.False(ok, "参数非数值 → 参数不匹配 → false（evaluator 抛 UNKNOWN_FUNCTION）");
    }

    [Fact]
    public void DtcPresent_WrongArgCount_ReturnsFalse()
    {
        var r = NewReg(0x1234);
        var ok = r.TryInvoke("dtcPresent", Array.Empty<ExpressionValue>(), out _);

        Assert.False(ok, "参数个数不对 → false");
    }

    [Fact]
    public void UnknownFunction_ReturnsFalse()
    {
        var r = NewReg(0x1234);
        var ok = r.TryInvoke("nope", new[] { ExpressionValue.FromLong(0x1234) }, out _);

        Assert.False(ok, "非 dtcPresent 函数 → false（由 composite 遍历下一 registry）");
    }
}
