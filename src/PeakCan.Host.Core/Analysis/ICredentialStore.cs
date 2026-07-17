namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.53.1 PATCH P1a: Core-side abstraction for secure credential
/// storage. Implementations are platform-specific (Windows Credential
/// Manager, macOS Keychain, Linux libsecret, in-memory for tests).
/// Per v3.52.0 hard-boundary: API keys MUST NEVER be stored in plaintext
/// appsettings.json or logged — they MUST flow through this interface.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Get a credential by key. Returns null if not found.
    /// Throws <see cref="CredentialStoreException"/> on platform errors.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Store a credential. Overwrites if exists.
    /// Throws <see cref="CredentialStoreException"/> on platform errors.</summary>
    Task SetAsync(string key, string value, CancellationToken ct = default);

    /// <summary>Delete a credential. No-op if not found.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>Platform-level credential access failure.
/// Wraps Win32 HRESULT / libsecret error codes with user-friendly message.</summary>
public sealed class CredentialStoreException : Exception
{
    public string Key { get; }
    public CredentialStoreException(string key, string message, Exception? inner = null)
        : base(message, inner) { Key = key; }
}