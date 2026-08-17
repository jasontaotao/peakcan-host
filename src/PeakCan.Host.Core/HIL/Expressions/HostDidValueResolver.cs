using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.Uds;

namespace PeakCan.HIL.Core.HIL.Expressions;

/// <summary>
/// Host 端 DID 值解析器。将 DID 地址映射为 <see cref="IStepVariableStore.Variables"/> 中的存储值。
/// 键格式由 <see cref="DidVariableKey.Format"/> 统一管理。
/// </summary>
public sealed class HostDidValueResolver : IDidValueResolver
{
    private readonly IStepVariableStore _store;

    public HostDidValueResolver(IStepVariableStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// 尝试获取 DID 值。从 Variables 字典中查找 "did_0xXXXX" 格式的键。
    /// </summary>
    public bool TryGetDid(ushort did, out ExpressionValue value)
    {
        var key = DidVariableKey.Format(did);
        if (_store.Variables.TryGetValue(key, out var raw))
        {
            value = ConvertObjectToExpressionValue(raw);
            return true;
        }
        value = ExpressionValue.Undefined;
        return false;
    }

    /// <summary>
    /// 将 <see cref="IStepVariableStore.Variables"/> 中的 object 值转换为 ExpressionValue。
    /// byte[]→FromBytes, double→FromDouble, int/long/short/byte→FromLong,
    /// bool→FromBool, string→FromString, null→Undefined, 未知类型→Undefined。
    /// </summary>
    internal static ExpressionValue ConvertObjectToExpressionValue(object? raw)
    {
        return raw switch
        {
            byte[] bytes => ExpressionValue.FromBytes(bytes),
            double d => ExpressionValue.FromDouble(d),
            int i => ExpressionValue.FromLong(i),
            long l => ExpressionValue.FromLong(l),
            short s => ExpressionValue.FromLong(s),
            byte b => ExpressionValue.FromLong(b),
            bool bVal => ExpressionValue.FromBool(bVal),
            string sVal => ExpressionValue.FromString(sVal),
            null => ExpressionValue.Undefined,
            _ => ExpressionValue.Undefined,
        };
    }
}