using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.Expressions;

/// <summary>
/// Host 端 StepScope 工厂（v11.1 Ruling 1）。
/// 从 host 服务（IAssertionContext / IStepVariableStore / IFrameStatistics）构造 core StepScope，
/// 注入 HostSignalValueResolver / HostDidValueResolver / FrameStatisticsFunctionRegistry，
/// 转换 Variables 和 Parameters 为 ExpressionValue 字典。
/// </summary>
public static class StepScopeFactory
{
    /// <summary>
    /// 创建注入完成的 <see cref="StepScope"/>。
    /// B2-R1：store 与 frameStats 均改为 nullable——ctx 不实现 IStepVariableStore 时
    /// （如非控制流 suite / FakeAssertionContext）Variables=null、DidResolver 仍可构造
    /// （读取空 Variables 字典）；frameStats=null 时 FunctionRegistry=null，frameCount/
    /// frameSeen/elapsedMs 在求值器侧退化为 UNKNOWN_FUNCTION（Cli 场景可接受）。
    /// </summary>
    /// <param name="ctx">断言上下文（信号查询）。</param>
    /// <param name="store">步骤变量存储（DID 值）；null 时 Variables 层为 null。</param>
    /// <param name="frameStats">帧统计（elapsedMs / frameCount）；null 时帧函数不可用。</param>
    /// <param name="caseStart">case 开始时的 IFrameStatistics.Now 值（ms）；frameStats=null 时无意义，传 0。</param>
    /// <param name="suiteParams">Suite 级参数（可空）。</param>
    /// <param name="caseParams">Case 级参数（可空）。</param>
    /// <param name="loopIndexVar">当前 Loop 索引变量（可空）。</param>
    /// <param name="outerLoopIndexVar">外层 Loop 索引变量（可空）。</param>
    public static StepScope Create(
        IAssertionContext ctx,
        IStepVariableStore? store,
        IFrameStatistics? frameStats,
        long caseStart,
        IReadOnlyDictionary<string, ParameterValue>? suiteParams = null,
        IReadOnlyDictionary<string, ParameterValue>? caseParams = null,
        IReadOnlyDictionary<string, ExpressionValue>? loopIndexVar = null,
        IReadOnlyDictionary<string, ExpressionValue>? outerLoopIndexVar = null)
    {
        // 创建 resolver 实现：DidResolver 内部 TryGetDid 访问 store.Variables，
        // store=null 时 Variables 为空字典（NSubstitute 默认）→ TryGetDid 返回 false。
        var signalResolver = new HostSignalValueResolver(ctx);
        var didResolver = new HostDidValueResolver(store ?? NullStepVariableStore.Instance);
        // frameStats=null → 不注册帧函数注册表（求值器遇 frameCount/frameSeen/elapsedMs → UNKNOWN_FUNCTION）
        IFunctionRegistry? functionRegistry = frameStats is null
            ? null
            : new FrameStatisticsFunctionRegistry(frameStats, caseStart);

        // 转换 Variables（store=null → null，${name} 解析退化为 Undefined）
        var variables = ConvertVariables(store?.Variables);

        // 转换 Parameters
        var suiteParamsConverted = ConvertParameters(suiteParams);
        var caseParamsConverted = ConvertParameters(caseParams);

        return new StepScope(
            variables: variables,
            suiteParams: suiteParamsConverted,
            caseParams: caseParamsConverted,
            loopIndexVar: loopIndexVar,
            outerLoopIndexVar: outerLoopIndexVar,
            functionRegistry: functionRegistry,
            signalResolver: signalResolver,
            didResolver: didResolver);
    }

    /// <summary>
    /// 从 <see cref="IStepVariableStore.Variables"/> 重建 ExpressionValue 字典快照。
    /// 引擎在 Assign / 任何写入 Variables 的叶步骤（如 ReadDid）之后调用，
    /// 使后续 <c>${name}</c> 引用能读到最新写入（spec §7 读穿透 Variables）。
    /// </summary>
    internal static IReadOnlyDictionary<string, ExpressionValue>? RefreshVariables(IStepVariableStore? store)
        => ConvertVariables(store?.Variables);

    /// <summary>
    /// 空 IStepVariableStore：store=null 时的占位，HostDidValueResolver.TryGetDid 恒返回 false。
    /// 避免 NSubstitute Substitute.For() 在生产路径（HilRunnerService）创建代理的开销。
    /// </summary>
    private sealed class NullStepVariableStore : IStepVariableStore
    {
        public static readonly NullStepVariableStore Instance = new();
        public IDictionary<string, object> Variables { get; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// 将 IStepVariableStore.Variables（IDictionary&lt;string, object&gt;）转换为 ExpressionValue 字典。
    /// 委托给 HostDidValueResolver.ConvertObjectToExpressionValue 做类型转换。
    /// </summary>
    private static IReadOnlyDictionary<string, ExpressionValue>? ConvertVariables(
        IDictionary<string, object>? source)
    {
        if (source is null || source.Count == 0)
            return null;

        var dict = new Dictionary<string, ExpressionValue>(source.Count);
        foreach (var kvp in source)
        {
            dict[kvp.Key] = HostDidValueResolver.ConvertObjectToExpressionValue(kvp.Value);
        }
        return dict;
    }

    /// <summary>
    /// 将 ParameterValue 字典转换为 ExpressionValue 字典。
    /// Number→FromDouble, Integer→FromLong, Bool→FromBool, String→FromString, HexBytes→FromBytes。
    /// </summary>
    internal static IReadOnlyDictionary<string, ExpressionValue>? ConvertParameters(
        IReadOnlyDictionary<string, ParameterValue>? source)
    {
        if (source is null || source.Count == 0)
            return null;

        var dict = new Dictionary<string, ExpressionValue>(source.Count);
        foreach (var kvp in source)
        {
            dict[kvp.Key] = ConvertParameterValue(kvp.Value);
        }
        return dict;
    }

    /// <summary>
    /// 单个 ParameterValue 转换为 ExpressionValue。
    /// </summary>
    private static ExpressionValue ConvertParameterValue(ParameterValue pv)
    {
        if (pv.Value is null)
            return ExpressionValue.Undefined;

        return pv.Kind switch
        {
            ParameterKind.Number => ExpressionValue.FromDouble(Convert.ToDouble(pv.Value)),
            ParameterKind.Integer => ExpressionValue.FromLong(Convert.ToInt64(pv.Value)),
            ParameterKind.Bool => ExpressionValue.FromBool((bool)pv.Value),
            ParameterKind.String => ExpressionValue.FromString((string)pv.Value),
            ParameterKind.HexBytes => ExpressionValue.FromBytes((byte[])pv.Value),
            _ => ExpressionValue.Undefined,
        };
    }
}