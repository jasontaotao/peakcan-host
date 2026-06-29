using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit.Sdk;

namespace PeakCan.Host.App.Tests.TestHelpers;

/// <summary>
/// v1.6.1 PATCH Item 3: shared wait/jitter/retry utilities for cyclic-send
/// race tests. Addresses known transient-flaky patterns in
/// <c>CyclicSendServiceRaceTests</c> + <c>CyclicDbcSendServiceRaceTests</c>
/// where a Timer tick can be queued mid-Start/Stop window. Production
/// race-fix invariants remain independent per v1.5.1 PATCH Decision 7 —
/// this harness is test-only, NOT a production base class extraction.
/// <para>
/// Mirrors the "CI re-run 3× if fails" CI gate (memory v1.5.1 PATCH process
/// lesson 5) inside the test method: <see cref="AssertWithinAsync"/> retries
/// up to 3 times before giving up, so transient flakes no longer require
/// human CI re-trigger. Each retry logs to <see cref="Console.Error"/>
/// so flake frequency stays visible in test output.
/// </para>
/// </summary>
internal static class CyclicTimerTestHarness
{
    private const int DefaultRetryCount = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Wait until <paramref name="predicate"/> returns true or
    /// <paramref name="timeout"/> elapses. Polls every 5ms.
    /// Returns true on success, false on timeout.
    /// </summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }
            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// Assert <paramref name="predicate"/> becomes true within
    /// <paramref name="timeout"/>, retrying up to 3 times on timeout.
    /// Each retry is separated by <see cref="RetryDelay"/>. Throws
    /// <see cref="XunitException"/> on the final failure with a
    /// diagnostic message that names <paramref name="what"/> and reports
    /// total elapsed time.
    /// </summary>
    public static async Task AssertWithinAsync(
        Func<bool> predicate, TimeSpan timeout, string what)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentException.ThrowIfNullOrEmpty(what);

        var sw = Stopwatch.StartNew();
        for (int attempt = 1; attempt <= DefaultRetryCount; attempt++)
        {
            if (await WaitUntilAsync(predicate, timeout).ConfigureAwait(false))
            {
                return;
            }
            if (attempt < DefaultRetryCount)
            {
                Console.Error.WriteLine(
                    $"[CyclicTimerTestHarness] retry {attempt}/{DefaultRetryCount - 1} " +
                    $"waiting for '{what}' after {sw.ElapsedMilliseconds}ms");
                await Task.Delay(RetryDelay).ConfigureAwait(false);
            }
        }
        throw new XunitException(
            $"Timed out waiting for '{what}' after {DefaultRetryCount} attempts " +
            $"({sw.ElapsedMilliseconds}ms total)");
    }
}
