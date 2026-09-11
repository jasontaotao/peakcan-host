using PeakCan.HIL.Core.Analysis.Chat;

namespace PeakCan.Host.Mobile.Core.Chat;

/// <summary>
/// Builds an <see cref="IChatProvider"/> for a specific vendor configuration.
/// Implemented in the MAUI layer so HttpClients and the credential store
/// stay platform-side; Mobile.Core only sees this factory interface.
/// </summary>
public interface IChatProviderFactory
{
    /// <summary>Create a provider for <paramref name="apiBase"/>/<paramref name="model"/>
    /// reading the API key from <paramref name="credentialKey"/> via the injected
    /// credential store.</summary>
    IChatProvider Create(string apiBase, string model, string credentialKey);
}
