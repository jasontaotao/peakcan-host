using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PeakCan.Tools.PromptCacheProbe;

/// <summary>
/// Sends non-streaming chat-completion requests with a growing message
/// prefix so each round carries the previous round's payload verbatim —
/// the exact condition DeepSeek's automatic context caching keys on.
/// </summary>
public sealed class ProbeRunner
{
    private readonly HttpClient _http;
    private readonly string _apiBase;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly string _systemPrompt;

    public ProbeRunner(
        HttpClient http, string apiBase, string model, string apiKey, string systemPrompt)
    {
        _http = http;
        _apiBase = apiBase.TrimEnd('/');
        _model = model;
        _apiKey = apiKey;
        _systemPrompt = systemPrompt;
    }

    /// <summary>
    /// Build the message list for <paramref name="round"/> (1-based):
    /// system + the first <c>round</c> user/assistant turns, so round N
    /// is exactly round N-1's byte-identical prefix plus one new turn.
    /// </summary>
    public static IReadOnlyList<ProbeMessage> BuildGrowingMessages(
        IReadOnlyList<ProbeTurn> turns, string systemPrompt, int round)
    {
        var messages = new List<ProbeMessage> { new("system", systemPrompt) };
        for (int i = 0; i < round; i++)
        {
            messages.Add(new ProbeMessage("user", turns[i].User));
            messages.Add(new ProbeMessage("assistant", turns[i].Assistant));
        }
        return messages;
    }

    /// <summary>Send one round and return its parsed usage.</summary>
    public async Task<UsageInfo> SendRoundAsync(
        IReadOnlyList<ProbeMessage> messages, CancellationToken ct)
    {
        var body = new
        {
            model = _model,
            stream = false,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
        };
        var payload = JsonSerializer.Serialize(body);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/chat/completions")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // 先读正文再抛, 把服务端错误详情 (余额不足/上下文超长/鉴权失败) 拼进异常,
            // 否则诊断"为什么没命中缓存"时看不到真正原因。
            var errorBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"API 返回 {(int)resp.StatusCode} {resp.ReasonPhrase}: {errorBody}");
        }
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return UsageInfo.Parse(text);
    }

    /// <summary>Run <c>rounds</c> rounds with a growing prefix.</summary>
    public async Task<IReadOnlyList<ProbeRound>> SendGrowingAsync(
        IReadOnlyList<ProbeTurn> turns, CancellationToken ct)
    {
        var results = new List<ProbeRound>(turns.Count);
        for (int round = 1; round <= turns.Count; round++)
        {
            var messages = BuildGrowingMessages(turns, _systemPrompt, round);
            var usage = await SendRoundAsync(messages, ct).ConfigureAwait(false);
            results.Add(new ProbeRound(round, messages.Count, usage));
        }
        return results;
    }
}