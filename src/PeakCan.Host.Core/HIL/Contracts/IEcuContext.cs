namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>
/// Shared ECU context for stateful data (seed values, unlock counters, etc.).
/// Thread-safe: implementations use ConcurrentDictionary or lock-based Dict.
/// </summary>
public interface IEcuContext
{
    /// <summary>Get a stored value, or default.</summary>
    T? Get<T>(string key);

    /// <summary>Store a value.</summary>
    void Set<T>(string key, T value);

    /// <summary>Check if a key exists.</summary>
    bool HasKey(string key);

    /// <summary>Clear all stored values.</summary>
    void Clear();
}
