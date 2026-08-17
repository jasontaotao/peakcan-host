using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.Expressions;

/// <summary>
/// 纯 frame-statistics 白名单函数注册表（v11.1 Ruling B1-R1）。
/// 实现 4 个内置帧统计函数：frameSeen / frameCount（单参、双参）/ elapsedMs。
/// 时间基为 <see cref="IFrameStatistics.Now"/>（long ms），case 前向窗口语义（Ruling B1-R2）。
/// isUndefined 是 evaluator 内建（A.5），dtcPresent 推迟到后续 task，均不在本 registry 实现。
/// </summary>
public sealed class FrameStatisticsFunctionRegistry : IFunctionRegistry
{
    private readonly IFrameStatistics _frameStats;
    private readonly long _caseStart;

    public FrameStatisticsFunctionRegistry(IFrameStatistics frameStats, long caseStart)
    {
        _frameStats = frameStats ?? throw new ArgumentNullException(nameof(frameStats));
        _caseStart = caseStart;
    }

    /// <summary>
    /// 尝试调用白名单函数。
    /// </summary>
    public bool TryInvoke(string name, ExpressionValue[] args, out ExpressionValue result)
    {
        // 按函数名分发
        return name switch
        {
            "elapsedMs" => InvokeElapsedMs(out result),
            "frameSeen" => InvokeFrameSeen(args, out result),
            "frameCount" => InvokeFrameCount(args, out result),
            _ => Fail(out result),
        };
    }

    /// <summary>
    /// elapsedMs() = Now - caseStart（long ms）。
    /// </summary>
    private bool InvokeElapsedMs(out ExpressionValue result)
    {
        result = ExpressionValue.FromLong(_frameStats.Now - _caseStart);
        return true;
    }

    /// <summary>
    /// frameSeen(id) = CountSince(id, caseStart, now) > 0。
    /// </summary>
    private bool InvokeFrameSeen(ExpressionValue[] args, out ExpressionValue result)
    {
        if (args.Length != 1 || !TryParseCanId(args[0], out var id))
            return Fail(out result);

        var now = _frameStats.Now;
        var count = _frameStats.CountSince(id, _caseStart, now);
        result = ExpressionValue.FromBool(count > 0);
        return true;
    }

    /// <summary>
    /// frameCount(id) = CountSince(id, caseStart, now)；
    /// frameCount(id, windowMs) = CountSince(id, now - windowMs, now)。
    /// </summary>
    private bool InvokeFrameCount(ExpressionValue[] args, out ExpressionValue result)
    {
        if (args.Length is not (1 or 2) || !TryParseCanId(args[0], out var id))
            return Fail(out result);

        var now = _frameStats.Now;
        long since;

        if (args.Length == 2)
        {
            // 滑动窗口
            if (!TryParseLong(args[1], out var windowMs))
                return Fail(out result);
            since = now - windowMs;
            if (since > now) since = 0; // 防溢出
        }
        else
        {
            // 前向窗口（case 开始 → 现在）
            since = _caseStart;
        }

        var count = _frameStats.CountSince(id, since, now);
        result = ExpressionValue.FromLong(count);
        return true;
    }

    /// <summary>
    /// 将 ExpressionValue 解析为 CanId。
    /// 支持 double/long 类型，Standard 11-bit 或 Extended 29-bit 自动识别。
    /// </summary>
    private static bool TryParseCanId(ExpressionValue value, out CanId id)
    {
        if (!TryParseUint(value, out var raw))
        {
            id = default;
            return false;
        }

        var format = raw > 0x7FFu ? FrameFormat.Extended : FrameFormat.Standard;
        id = new CanId(raw, format);
        return true;
    }

    /// <summary>
    /// 将 ExpressionValue 解析为 uint。
    /// </summary>
    private static bool TryParseUint(ExpressionValue value, out uint result)
    {
        if (value.Kind == ExpressionValue.ValueKind.Long)
        {
            var l = value.AsLong;
            if (l >= 0 && l <= 0x1FFFFFFF)
            {
                result = (uint)l;
                return true;
            }
        }
        else if (value.Kind == ExpressionValue.ValueKind.Double)
        {
            var d = value.AsDouble;
            if (d >= 0 && d <= 0x1FFFFFFF && d == Math.Truncate(d))
            {
                result = (uint)d;
                return true;
            }
        }
        result = 0;
        return false;
    }

    /// <summary>
    /// 将 ExpressionValue 解析为 long。
    /// </summary>
    private static bool TryParseLong(ExpressionValue value, out long result)
    {
        if (value.Kind == ExpressionValue.ValueKind.Long)
        {
            result = value.AsLong;
            return true;
        }
        if (value.Kind == ExpressionValue.ValueKind.Double)
        {
            var d = value.AsDouble;
            if (d >= long.MinValue && d <= long.MaxValue && d == Math.Truncate(d))
            {
                result = (long)d;
                return true;
            }
        }
        result = 0;
        return false;
    }

    /// <summary>
    /// 失败路径：返回 false 和 Undefined。
    /// </summary>
    private static bool Fail(out ExpressionValue result)
    {
        result = ExpressionValue.Undefined;
        return false;
    }
}