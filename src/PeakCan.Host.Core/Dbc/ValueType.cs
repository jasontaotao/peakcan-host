namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// DBC signal numeric type. The trailing <c>+</c> / <c>-</c> after the
/// byte-order marker maps to Unsigned / Signed; Float / Double are Vector
/// extensions accepted at parse time but not yet decoded (Task 7).
/// </summary>
public enum ValueType : byte
{
    Unsigned = 0,
    Signed = 1,
    Float = 2,
    Double = 3,
}