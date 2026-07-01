using System.Collections.ObjectModel;

namespace PeakCan.Host.Core.Uds.Database;

/// <summary>
/// In-memory DTC store, populated via ODX import or programmatic
/// seeding. Mirrors <see cref="DidDatabase"/> shape (no built-in
/// JSON loader — DTCs come from ODX, which is the canonical
/// configuration source). Single-threaded; concurrent writers
/// must serialize externally.
/// </summary>
public sealed class DtcDatabase
{
    private readonly List<DtcDefinition> _dtcs = new();

    /// <summary>All known DTCs (ordered by insertion).</summary>
    public IReadOnlyList<DtcDefinition> All => _dtcs.AsReadOnly();

    /// <summary>Create an empty database.</summary>
    public DtcDatabase() { }

    /// <summary>Create a database seeded with <paramref name="initial"/>.</summary>
    public DtcDatabase(IEnumerable<DtcDefinition> initial)
        => _dtcs.AddRange(initial);

    /// <summary>
    /// Add a range of DTCs. On duplicate Code, last-wins +
    /// "DuplicateId" warning emitted.
    /// </summary>
    public void AddRange(IEnumerable<DtcDefinition> defs, out IReadOnlyList<string> warnings)
    {
        var warnList = new List<string>();
        foreach (var d in defs)
        {
            var existingIdx = _dtcs.FindIndex(x => x.Code == d.Code);
            if (existingIdx >= 0)
            {
                warnList.Add($"Duplicate DTC code 0x{d.Code:X}; last value wins.");
                _dtcs[existingIdx] = d;
            }
            else
            {
                _dtcs.Add(d);
            }
        }
        warnings = warnList;
    }

    /// <summary>Look up a DTC by 3-byte code. Returns null if missing.</summary>
    public DtcDefinition? FindByCode(uint code)
    {
        foreach (var d in _dtcs)
        {
            if (d.Code == code) return d;
        }
        return null;
    }
}
