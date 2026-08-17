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
    /// </summary>
    /// <param name="ctx">断言上下文（信号查询）。</param>
    /// <param name="store">步骤变量存储（DID 值）。</param>
    /// <param name="frameStats">帧统计（elapsedMs / frameCount）。</param>
    /// <param name="caseStart">case 开始时的 IFrameStatistics.Now 值（ms）。</param>
    /// <param name="suiteParams">Suite 级参数（可空）。</param>
    /// <param name="caseParams">Case 级参数（可空）。</param>
    /// <param name="loopIndexVar">当前 Loop 索引变量（可空）。</param>
    /// <param name="outerLoopIndexVar">外层 Loop 索引变量（可空）。</param>
    public static StepScope Create(
        IAssertionContext ctx,
        IStepVariableStore store,
        IFrameStatistics frameStats,
        long caseStart,
        IReadOnlyDictionary<string, ParameterValue>? suiteParams = null,
        IReadOnlyDictionary<string, ParameterValue>? caseParams = null,
        IReadOnlyDictionary<string, ExpressionValue>? loopIndexVar = null,
        IReadOnlyDictionary<string, ExpressionValue>? outerLoopIndexVar = null)
    {
        // 创建 resolver 实现
        var signalResolver = new HostSignalValueResolver(ctx);
        var didResolver = new HostDidValueResolver(store);
        var functionRegistry = new FrameStatisticsFunctionRegistry(frameStats, caseStart);

        // 转换 Variables
        var variables = ConvertVariables(store.Variables);

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