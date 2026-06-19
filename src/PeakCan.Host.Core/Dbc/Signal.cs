namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// One signal inside a <see cref="Message"/>. Immutable. The multiplexor
/// fields (<see cref="IsMultiplexor"/>, <see cref="IsMultiplexed"/>,
/// <see cref="MultiplexValue"/>) are populated by Task 6.
/// </summary>
/// <param name="Name">DBC signal identifier.</param>
/// <param name="StartBit">0-based bit offset on the wire.</param>
/// <param name="Length">Bit width. Up to 64 for CAN FD.</param>
/// <param name="Order">Byte order on the wire.</param>
/// <param name="ValueType">Numeric interpretation.</param>
/// <param name="Factor">Engineering scale: <c>physical = raw * factor + offset</c>.</param>
/// <param name="Offset">Engineering offset.</param>
/// <param name="Min">Valid range lower bound (after scale).</param>
/// <param name="Max">Valid range upper bound (after scale).</param>
/// <param name="Unit">Display unit string. Empty if unspecified.</param>
/// <param name="Receivers">Node names subscribed to this signal.</param>
/// <param name="IsMultiplexor">True iff this signal is the multiplexor (selector).</param>
/// <param name="IsMultiplexed">True iff this signal's value is only valid for a specific mux value.</param>
/// <param name="MultiplexValue">When <see cref="IsMultiplexed"/>, the mux value gating this signal.</param>
/// <param name="ValueTableName">Optional <c>VAL_TABLE_</c> name attached via <c>VAL_</c> (Task 6).</param>
public sealed record Signal(
    string Name,
    byte StartBit,
    byte Length,
    ByteOrder Order,
    ValueType ValueType,
    double Factor,
    double Offset,
    double Min,
    double Max,
    string Unit,
    IReadOnlyList<string> Receivers,
    bool IsMultiplexor = false,
    bool IsMultiplexed = false,
    ushort? MultiplexValue = null,
    string? ValueTableName = null);
