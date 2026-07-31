using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Generators;

/// <summary>Minimal IEcuContext implementation for unit tests.</summary>
internal sealed class FakeContext : IEcuContext
{
    private readonly Dictionary<string, object> _store = new();

    public T? Get<T>(string key)
    {
        if (_store.TryGetValue(key, out var val) && val is T typed)
            return typed;
        return default;
    }

    public void Set<T>(string key, T value) => _store[key] = value!;

    public bool HasKey(string key) => _store.ContainsKey(key);

    public void Clear() => _store.Clear();
}
