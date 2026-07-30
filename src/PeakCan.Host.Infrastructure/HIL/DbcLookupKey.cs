namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Shared CAN ID → DBC lookup key conversion.
/// </summary>
internal static class DbcLookupKey
{
    internal static uint ToLookupKey(uint rawId, bool isExtended) =>
        isExtended ? rawId | 0x80000000u : rawId;
}
