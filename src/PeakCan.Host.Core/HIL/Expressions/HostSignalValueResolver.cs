using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.Expressions;

/// <summary>
/// Host 端信号值解析器。将 "MessageName.SignalName" 格式的引用解析为 CAN 信号值。
/// 通过 IAssertionContext.GetSignalValue 查询信号缓存，未命中返回 Undefined。
/// </summary>
public sealed class HostSignalValueResolver : ISignalValueResolver
{
    private readonly IAssertionContext _ctx;

    public HostSignalValueResolver(IAssertionContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
    }

    /// <summary>
    /// 尝试获取信号值。信号名格式为 "MessageName.SignalName"（如 "BMS.EngineRPM"）。
    /// </summary>
    public bool TryGetSignal(string msgDotSig, out ExpressionValue value)
    {
        var signal = _ctx.GetSignalValue(msgDotSig);
        if (signal.HasValue)
        {
            value = ExpressionValue.FromDouble(signal.Value);
            return true;
        }
        value = ExpressionValue.Undefined;
        return false;
    }
}