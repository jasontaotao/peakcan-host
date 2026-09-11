namespace PeakCan.Host.Mobile.Core.Chat;

/// <summary>Outcome of a vendor connectivity probe (e.g. GET /models).</summary>
public enum ChatConnectionResult : byte
{
    /// <summary>Credentials accepted (2xx).</summary>
    Ok,

    /// <summary>API key rejected (401/403).</summary>
    Unauthorized,

    /// <summary>Network / server / other failure.</summary>
    Failed,
}

/// <summary>
/// Probes a vendor chat endpoint with an API key without spending tokens
/// (GET /models). Abstracted so the settings VM is unit-testable without a
/// live HTTP stack; the MAUI implementation uses <c>HttpClient</c>.
/// </summary>
public interface IChatConnectionTester
{
    Task<ChatConnectionResult> TestAsync(string apiBase, string apiKey, CancellationToken ct = default);
}
