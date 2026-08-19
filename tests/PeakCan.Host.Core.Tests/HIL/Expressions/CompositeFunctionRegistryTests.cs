using PeakCan.HIL.Core.HIL.Expressions;
using Xunit;

namespace PeakCan.HIL.Core.Tests.HIL.Expressions;

/// <summary>
/// CompositeFunctionRegistry 单元测试：包装多个 IFunctionRegistry，TryInvoke 遍历首个命中。
/// host StepScopeFactory 用它把 FrameStatisticsFunctionRegistry + DtcPresenceFunctionRegistry 挂进
/// scope.FunctionRegistry（scope 单字段，不改 hil-core）。
/// </summary>
public class CompositeFunctionRegistryTests
{
    /// <summary>可控 stub：命中指定函数名时返回预设 ok/result，否则 false。</summary>
    private sealed class StubRegistry(string name, bool ok, ExpressionValue result) : IFunctionRegistry
    {
        public bool TryInvoke(string n, ExpressionValue[] args, out ExpressionValue r)
        {
            if (n == name) { r = result; return ok; }
            r = ExpressionValue.Undefined;
            return false;
        }
    }

    [Fact]
    public void FirstRegistry_Hit_ReturnsItsResult()
    {
        var composite = new CompositeFunctionRegistry(
            new StubRegistry("frameSeen", true, ExpressionValue.FromBool(true)),
            new StubRegistry("dtcPresent", true, ExpressionValue.FromBool(false)));

        var ok = composite.TryInvoke("frameSeen", Array.Empty<ExpressionValue>(), out var result);

        Assert.True(ok);
        Assert.True(result.AsBool);
    }

    [Fact]
    public void SecondRegistry_Hit_WhenFirstMisses_ReturnsSecondResult()
    {
        var composite = new CompositeFunctionRegistry(
            new StubRegistry("frameSeen", true, ExpressionValue.FromBool(true)),
            new StubRegistry("dtcPresent", true, ExpressionValue.FromBool(false)));

        var ok = composite.TryInvoke("dtcPresent", new[] { ExpressionValue.FromLong(0x1234) }, out var result);

        Assert.True(ok);
        Assert.False(result.AsBool);
    }

    [Fact]
    public void AllRegistriesMiss_ReturnsFalse()
    {
        var composite = new CompositeFunctionRegistry(
            new StubRegistry("frameSeen", true, ExpressionValue.FromBool(true)),
            new StubRegistry("dtcPresent", true, ExpressionValue.FromBool(false)));

        var ok = composite.TryInvoke("nope", Array.Empty<ExpressionValue>(), out _);

        Assert.False(ok);
    }

    [Fact]
    public void Empty_Registry_ReturnsFalse()
    {
        var composite = new CompositeFunctionRegistry();

        var ok = composite.TryInvoke("anything", Array.Empty<ExpressionValue>(), out _);

        Assert.False(ok);
    }
}
