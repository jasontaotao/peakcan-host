namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>
/// Key-value store for ECU simulation context. Holds stateful data across
/// UDS requests (e.g. security seed, DTC list, session state).
/// Thread-safe for single-writer / multiple-reader scenarios typical in HIL.
/// </summary>
internal sealed class EcuContextStore : IEcuContext
{
    private readonly Dictionary<string, object> _store = new();

    /// <inheritdoc/>
    public T? Get<T>(string key)
    {
        lock (_store)
        {
            if (_store.TryGetValue(key, out var value) && value is T typed)
                return typed;
            return default!;
        }
    }

    /// <summary>Set a value for a key. Overwrites any existing value.</summary>
    public void Set<T>(string key, T value)
    {
        lock (_store)
        {
            _store[key] = value!;
        }
    }

    /// <summary>True when the key exists in the store.</summary>
    public bool HasKey(string key)
    {
        lock (_store) { return _store.ContainsKey(key); }
    }

    /// <summary>Remove all keys.</summary>
    public void Clear()
    {
        lock (_store) { _store.Clear(); }
    }
}
