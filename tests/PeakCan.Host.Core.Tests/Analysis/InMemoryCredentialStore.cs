using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.Core.Tests.Analysis;

/// <summary>Test-only in-memory ICredentialStore. NOT production code.
/// Used by interface contract tests + WindowsCredentialManagerStore
/// cross-validation in tests/.../App.Tests/Services/CredentialStore.</summary>
internal sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _store = new();

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_store.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _store.Remove(key);
        return Task.CompletedTask;
    }
}