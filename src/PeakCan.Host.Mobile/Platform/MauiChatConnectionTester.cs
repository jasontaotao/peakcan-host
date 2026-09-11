using System.Net.Http.Headers;
using PeakCan.Host.Mobile.Core.Chat;

namespace PeakCan.Host.Mobile.Platform;

/// <summary>
/// <see cref="IChatConnectionTester"/> via GET <c>{apiBase}/models</c>
/// (no tokens spent). 401/403 → <see cref="ChatConnectionResult.Unauthorized"/>;
/// 2xx → <see cref="ChatConnectionResult.Ok"/>; everything else
/// (network errors, non-2xx) → <see cref="ChatConnectionResult.Failed"/>.
/// </summary>
public sealed class MauiChatConnectionTester : IChatConnectionTester
{
    public async Task<ChatConnectionResult> TestAsync(string apiBase, string apiKey, CancellationToken ct = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase.TrimEnd('/')}/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return ChatConnectionResult.Unauthorized;
            return response.IsSuccessStatusCode ? ChatConnectionResult.Ok : ChatConnectionResult.Failed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ChatConnectionResult.Failed;
        }
    }
}
