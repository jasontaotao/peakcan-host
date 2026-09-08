using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.Expressions;

namespace PeakCan.Host.Core.HIL.Expressions;

/// <summary>
/// SecOC verdict-statistics whitelist functions (spec §5-D6.2):
/// secocAccepted(id) / secocRejected(id) → bool; secocLastReason(id) → string
/// ("" when the PDU has no rejection yet). Unknown CAN ids read as zero counts.
/// </summary>
public sealed class SecOcFunctionRegistry : IFunctionRegistry
{
    private readonly ISecOcStats _stats;

    public SecOcFunctionRegistry(ISecOcStats stats)
        => _stats = stats ?? throw new ArgumentNullException(nameof(stats));

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

        if (!_stats.TryGet(id, out var bucket))
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
