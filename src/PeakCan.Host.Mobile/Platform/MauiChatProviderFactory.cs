using PeakCan.HIL.Core.Analysis;
using PeakCan.HIL.Core.Analysis.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Mobile.Core.Chat;

namespace PeakCan.Host.Mobile.Platform;

/// <summary>
/// <see cref="IChatProviderFactory"/> that builds an
/// <see cref="OpenAiCompatibleChatProvider"/> per vendor selection (a fresh
/// <c>HttpClient</c> each time, matching the desktop <c>BuildSettingsProvider</c>).
/// </summary>
public sealed class MauiChatProviderFactory(ICredentialStore credentials) : IChatProviderFactory
{
    public IChatProvider Create(string apiBase, string model, string credentialKey)
    {
        var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        return new OpenAiCompatibleChatProvider(
            httpClient,
            new LlmOptions { ApiBase = apiBase, Model = model },
            credentials,
            credentialKey,
            NullLogger<OpenAiCompatibleChatProvider>.Instance);
    }
}
