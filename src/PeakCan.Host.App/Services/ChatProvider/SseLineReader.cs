using System.IO;
using System.Runtime.CompilerServices;

namespace PeakCan.Host.App.Services.ChatProvider;

/// <summary>
/// Reads an SSE <c>text/event-stream</c> line-by-line with a per-line
/// silence timeout. Extracted from <c>DeepSeekProvider.ReadLineWithTimeoutAsync</c>
/// so <see cref="DeepSeekChatProvider"/> shares the same resilient read
/// pattern (a slow model that pauses longer than the timeout is
/// interrupted instead of hanging the chat loop forever).
/// </summary>
internal static class SseLineReader
{
    /// <summary>Yield non-empty <c>data: </c> payloads until EOF. Throws
    /// <see cref="OperationCanceledException"/> on per-line timeout
    /// (distinct from caller cancellation).</summary>
    public static async IAsyncEnumerable<string> ReadDataLinesAsync(
        StreamReader reader,
        TimeSpan readTimeout,
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(readTimeout);

            string? line;
            try
            {
                line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    $"SSE stream read timed out (no data for {readTimeout.TotalSeconds}s)");
            }

            if (line is null) yield break;
            if (line.Length == 0) continue;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var payload = line["data: ".Length..];
            if (payload == "[DONE]") yield break;
            yield return payload;
        }
    }
}
