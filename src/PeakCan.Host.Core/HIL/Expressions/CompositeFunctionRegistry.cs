namespace PeakCan.HIL.Core.HIL.Expressions;

/// <summary>
/// 复合函数注册表：包装多个 <see cref="IFunctionRegistry"/>，<see cref="TryInvoke"/> 顺序遍历，
/// 首个返回 true 即止；全 miss → false。host <c>StepScopeFactory</c> 用它把
/// <c>FrameStatisticsFunctionRegistry</c> + <c>DtcPresenceFunctionRegistry</c> 挂进 scope.FunctionRegistry
/// （scope 单字段，不改 hil-core）。函数名不冲突（frame: elapsedMs/frameSeen/frameCount；dtc: dtcPresent）。
/// </summary>
internal sealed class CompositeFunctionRegistry : IFunctionRegistry
{
    private readonly IFunctionRegistry[] _inner;

    public CompositeFunctionRegistry(params IFunctionRegistry[] inner)
        => _inner = inner;

    public bool TryInvoke(string name, ExpressionValue[] args, out ExpressionValue result)
    {
        // 顺序遍历，首个命中即止；全 miss → false + Undefined
        foreach (var reg in _inner)
        {
            if (reg.TryInvoke(name, args, out result))
                return true;
        }
        result = ExpressionValue.Undefined;
        return false;
    }
}
