using System.Text.Json;
using PeakCan.HIL.Core.Analysis;

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
        // Key format "PeakCan/deepseek/default" -> "HIL_PEAKCAN_DEEPSEEK_DEFAULT_API_KEY"
        var envVarName = $"HIL_{key.ToUpperInvariant().Replace("/", "_").Replace("-", "_")}_API_KEY";
        var env = System.Environment.GetEnvironmentVariable(envVarName);
        if (env is not null) return Task.FromResult<string?>(env);

        // 2b. Backward compat: old key name "deepseek-api-key" -> HIL_DEEPSEEK_API_KEY
        if (key == "PeakCan/deepseek/default")
        {
            var legacyEnv = System.Environment.GetEnvironmentVariable("HIL_DEEPSEEK_API_KEY");
            if (legacyEnv is not null) return Task.FromResult<string?>(legacyEnv);
        }

        // 3. Check ~/.hil/credentials file
        var credPath = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".hil", "credentials");
        if (File.Exists(credPath))
        {
            try
            {
                var json = File.ReadAllText(credPath);
                var creds = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (creds is { } c)
                {
                    if (c.TryGetValue(key, out var fileVal))
                        return Task.FromResult<string?>(fileVal);
                    // 3b. Backward compat: old key name in credentials file
                    if (key == "PeakCan/deepseek/default"
                        && c.TryGetValue("deepseek-api-key", out var legacyVal))
                        return Task.FromResult<string?>(legacyVal);
                }
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
