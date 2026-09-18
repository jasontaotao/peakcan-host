using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.Expressions;

namespace PeakCan.Host.Core.HIL.Expressions;

/// <summary>
/// SecOC verdict-statistics whitelist functions (spec §5-D6.2):
/// secocAccepted(id) / secocRejected(id) → bool; secocLastReason(id) → string
/// ("" when the PDU has no rejection yet). Unknown CAN ids read as zero counts.
/// <para>
/// 项 2（2026-09-17）：逐通道路由（方案 B「跟着步骤走」）。多通道模式下
/// <paramref name="statsResolver"/> 按当前作用通道名解析 stats；表达式所在步骤
/// 声明 TargetChannel 时自动用该通道的验签统计，否则回落默认通道。
/// </para>
/// </summary>
public sealed class SecOcFunctionRegistry : IFunctionRegistry
{
    private readonly ISecOcStats _stats;
    private readonly Func<string?, ISecOcStats?>? _statsResolver;
    private readonly Func<string?>? _currentChannelProvider;

    /// <summary>单通道构造（向后兼容）：固定 stats，无逐通道路由。</summary>
    public SecOcFunctionRegistry(ISecOcStats stats)
        => _stats = stats ?? throw new ArgumentNullException(nameof(stats));

    /// <summary>
    /// 多通道构造：statsResolver 按通道名解析；currentChannelProvider 提供
    /// 当前作用通道（引擎逐 step 设置）。null/空通道 → 默认 stats（fallback）。
    /// </summary>
    public SecOcFunctionRegistry(
        Func<string?, ISecOcStats?> statsResolver,
        Func<string?>? currentChannelProvider = null)
    {
        _statsResolver = statsResolver ?? throw new ArgumentNullException(nameof(statsResolver));
        _currentChannelProvider = currentChannelProvider;
        _stats = null!;
    }

    /// <summary>解析当前应使用的 stats：优先按当前通道，回落构造注入的默认。</summary>
    private ISecOcStats? CurrentStats()
    {
        if (_statsResolver is null)
            return _stats;
        var channel = _currentChannelProvider?.Invoke();
        // null/空通道名 → 默认通道（fallback 到默认 stats）。
        return string.IsNullOrEmpty(channel)
            ? _statsResolver(null)
            : _statsResolver(channel);
    }

    public bool TryInvoke(string name, ExpressionValue[] args, out ExpressionValue result)
    {
        if (name is not ("secocAccepted" or "secocRejected" or "secocLastReason"))
        {
            result = default;
            return false; // let the composite/engine report UNKNOWN_FUNCTION
        }
        if (args.Length != 1 || !TryParseCanId(args[0], out var id))
        {
            result = default;
            return false;
        }

        var stats = CurrentStats();
        if (stats is null || !stats.TryGet(id, out var bucket))
        {
            result = name switch
            {
                "secocAccepted" => ExpressionValue.FromBool(false),
                "secocRejected" => ExpressionValue.FromBool(false),
                "secocLastReason" => ExpressionValue.FromString(""),
                _ => default,
            };
            return true;
        }

        result = name switch
        {
            "secocAccepted" => ExpressionValue.FromBool(bucket.Accepted > 0),
            "secocRejected" => ExpressionValue.FromBool(bucket.Rejected > 0),
            "secocLastReason" => ExpressionValue.FromString(bucket.LastReason ?? ""),
            _ => default,
        };
        return true;
    }

    private static bool TryParseCanId(ExpressionValue value, out uint id)
    {
        id = 0;
        if (value.Kind == ExpressionValue.ValueKind.String)
        {
            var text = value.AsString.Trim();
            var isHex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            return uint.TryParse(
                isHex ? text[2..] : text,
                isHex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out id);
        }
        if (value.Kind == ExpressionValue.ValueKind.Long)
        {
            id = unchecked((uint)value.AsLong);
            return true;
        }
        if (value.Kind == ExpressionValue.ValueKind.Double)
        {
            id = unchecked((uint)value.AsDouble);
            return true;
        }
        return false;
    }
}
