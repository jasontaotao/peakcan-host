namespace PeakCan.Host.Core.Uds.Database;

/// <summary>
/// Definition of a single UDS Data Identifier (DID). Populated from
/// built-in defaults and/or a user JSON file at
/// <c>%APPDATA%\PeakCan.Host\uds-dids.json</c>.
/// </summary>
/// <param name="Id">2-byte DID (e.g. 0xF190 for VIN).</param>
/// <param name="Name">Short human-readable name.</param>
/// <param name="Description">Longer description for UI tooltip / details panel.</param>
/// <param name="LengthBytes">Expected byte length of the DID payload.</param>
/// <param name="Writable">Whether <c>WriteDataByIdentifier (0x2E)</c> is supported.</param>
public sealed record DidDefinition(
    ushort Id,
    string Name,
    string Description,
    int LengthBytes,
    bool Writable)
{
    /// <summary>
    /// Human-readable form with the DID rendered as hex (e.g. "DidDefinition
    /// 0xF190 (VIN, 17 bytes)"). The default record ToString renders
    /// <see cref="Id"/> as decimal, which is misleading for 16-bit UDS DIDs.
    /// </summary>
    public override string ToString() =>
        $"DidDefinition 0x{Id:X4} ({Name}, {LengthBytes} bytes)";
}
