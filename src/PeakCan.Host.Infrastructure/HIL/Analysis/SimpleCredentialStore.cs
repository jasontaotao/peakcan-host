using System.Text.Json;
using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.Infrastructure.HIL.Analysis;

/// <summary>
/// Sprint 14: Simple credential store for CLI/headless environments.
/// Checks in-memory store first, then environment variable, then ~/.hil/credentials file.
/// </summary>
public sealed class SimpleCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _store = new();

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        // 1. Check in-memory store (SetAsync writes here)
        if (_store.TryGetValue(key, out var val))
            return Task.FromResult<string?>(val);

        // 2. Check environment variable (key-specific: HIL_{KEY_UPPER}_API_KEY)
        var envVarName = $"HIL {key.ToUpperInvariant().Replace("-", "_")}_API_KEY";
        var env = Environment.GetEnvironmentVariable(envVarName);
        if (env is not null) return Task.FromResult<string?>(env);
        // Fallback for the well-known deepseek key
        if (key == "deepseek-api-key")
        {
            var fallback = Environment.GetEnvironmentVariable("HIL_DEEPSEEK_API_KEY");
            if (fallback is not null) return Task.FromResult<string?>(fallback);
        }

        // 3. Check ~/.hil/credentials file
        var credPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".hil", "credentials");
        if (File.Exists(credPath))
        {
            try
            {
                var json = File.ReadAllText(credPath);
                var creds = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (creds is { } c && c.TryGetValue(key, out var fileVal))
                    return Task.FromResult<string?>(fileVal);
            }
            catch { /* file corrupted or no permission — degrade */ }
        }

        return Task.FromResult<string?>(null);
    }

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
