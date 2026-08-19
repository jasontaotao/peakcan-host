namespace PeakCan.HIL.Core.HIL.Expressions;

/// <summary>
/// dtcPresent(code) 内置函数注册表（方案 B 预查注入，§3）。
/// 持可变 <see cref="HashSet{UInt32}"/>，由引擎在 if/while 条件求值前 async 预查
/// <c>ReadDtcInformation(0xFF)</c> 后填充 active DTC codes；本注册表 <see cref="TryInvoke"/>
/// 同步读 set。语义对齐 <c>AssertDtcStepExecutor</c>：bit0(testFailed)/bit2(confirmedDTC)
/// 任一置位 = active；code 为 2-byte ushort（对齐 <c>DtcInfo.Code</c>）。
/// </summary>
internal sealed class DtcPresenceFunctionRegistry : IFunctionRegistry
{
    private readonly HashSet<uint> _presentCodes;

    public DtcPresenceFunctionRegistry(HashSet<uint> presentCodes)
        => _presentCodes = presentCodes;

    /// <summary>
    /// dtcPresent(code) = <see cref="_presentCodes"/>.Contains(code)；
    /// 函数存在 + 参数有效 → true（result=bool），否则 false（让 composite 遍历下一 registry / evaluator 抛 UNKNOWN_FUNCTION）。
    /// </summary>
    public bool TryInvoke(string name, ExpressionValue[] args, out ExpressionValue result)
    {
        result = ExpressionValue.Undefined;
        if (name != "dtcPresent") return false;            // 非本 registry 函数
        if (args.Length != 1 || !TryParseUshort(args[0], out var code)) return false; // 参数无效
        result = ExpressionValue.FromBool(_presentCodes.Contains(code));
        return true;
    }

    /// <summary>
    /// 将 ExpressionValue 解析为 ushort（DTC code 2-byte，对齐 DtcInfo.Code）。
    /// 复制自 FrameStatisticsFunctionRegistry.TryParseUint 的解析逻辑，上限改 ushort（0xFFFF）。
    /// </summary>
    private static bool TryParseUshort(ExpressionValue value, out ushort result)
    {
        if (value.Kind == ExpressionValue.ValueKind.Long)
        {
            var l = value.AsLong;
            if (l >= 0 && l <= 0xFFFF) { result = (ushort)l; return true; }
        }
        else if (value.Kind == ExpressionValue.ValueKind.Double)
        {
            var d = value.AsDouble;
            if (d >= 0 && d <= 0xFFFF && d == Math.Truncate(d)) { result = (ushort)d; return true; }
        }
        result = 0;
        return false;
    }
}
