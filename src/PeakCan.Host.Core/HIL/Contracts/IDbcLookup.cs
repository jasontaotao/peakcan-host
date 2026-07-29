namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>
/// DBC message lookup. Implemented by App layer (DbcService), injected into AssertionContext.
/// Breaks the DBC resolution chain across layers.
/// </summary>
public interface IDbcLookup
{
    /// <summary>Find message definition by CAN ID. Returns null if not found.</summary>
    Core.Dbc.Message? FindMessage(uint canId);
}
