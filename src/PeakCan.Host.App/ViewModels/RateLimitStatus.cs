using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// Shared refresh logic for the rate-limit rejected-count chip pattern
/// (yellow "rate limit rejected: N" Border). Reduces the 3-way duplication
/// introduced by v3.0.8 SendViewModel + v3.0.9 DbcSend + v3.0.9 MultiFrame.
/// <para>
/// <b>Why a static helper, not a base class:</b> CommunityToolkit.Mvvm 8.4.2
/// source-gen requires <c>[ObservableProperty]</c> and
/// <c>partial void OnXxxChanged</c> to live on the declaring class. An abstract
/// <c>RateLimitStatusViewModel : ObservableObject</c> would force inheritance,
/// and (a) all 3 VMs are <c>sealed</c>, (b) they already inherit different
/// things or nothing, (c) C# single-inheritance blocks future
/// <c>IHostedService</c> / <c>IDisposable</c> adoption. Use inheritance for
/// behavior, not for code reuse.
/// </para>
/// <para>
/// <b>Why the field + computed Visibility + OnXxxChanged hook stay on each
/// VM:</b> same source-gen limitation. The field cannot move to a helper
/// class (composition would change the XAML binding shape from
/// <c>{Binding RateLimitRejectedVisibility}</c> to
/// <c>{Binding RateLimit.RateLimitRejectedVisibility}</c> and force 3 XAML
/// edits + 12 test updates for zero behavior gain).
/// </para>
/// <para>
/// <b>Why not an interface (<c>IRateLimitStatus</c>):</b> interface contract
/// would still require the field + property + hook on each VM (interface
/// cannot host source-gen attributes), and the helper has a single shared
/// behavior (try/catch + log + return-currentValue) that an interface would
/// not consolidate.
/// </para>
/// <para>
/// <b>Why <c>Func&lt;long&gt;</c> not <c>IRateLimitRejectedCountProvider</c>:</b>
/// DI factory cost (the interface would need an extra registration), and the
/// field is consumed by a 200ms / 100ms poll, not hot-pathed.
/// <c>Func&lt;long&gt;</c> captures the singleton <c>RateLimitedSendService</c>
/// via closure.
/// </para>
/// <para>
/// <b>Threading note:</b> <see cref="Refresh"/> is a pure function over the
/// captured <c>Func&lt;long&gt;</c> — no shared state, no closure over VM
/// state. The DispatcherTimer still calls <c>Poll()</c> on the UI thread;
/// the helper just collapses 3 statements into 1.
/// </para>
/// </summary>
internal static partial class RateLimitStatus
{
    private static readonly ILogger _defaultLogger = NullLogger.Instance;

    /// <summary>
    /// v3.1.0 MINOR: returns the next rejected-frame count, swallowing
    /// provider exceptions with a warning log. Returns
    /// <paramref name="currentValue"/> when the provider is <c>null</c>
    /// (no rate-limit policy active) or throws (keep last known good
    /// value).
    /// </summary>
    /// <param name="provider">
    /// Optional factory that returns the live rejected-frame count from
    /// the underlying <c>RateLimitedSendService</c>. Production passes
    /// <c>() =&gt; rateLimited.RejectedFrameCount</c>; tests pass a
    /// controllable lambda; <c>null</c> means the UI stays at its current
    /// value (no rate-limit policy active).
    /// </param>
    /// <param name="currentValue">
    /// The count currently displayed by the chip. Returned verbatim when
    /// <paramref name="provider"/> is <c>null</c> or throws, so the chip
    /// never resets to zero on transient provider failure.
    /// </param>
    /// <param name="logger">
    /// Optional logger for the warning emitted when <paramref name="provider"/>
    /// throws. Defaults to <see cref="NullLogger.Instance"/> so test call
    /// sites do not need to pass an explicit logger.
    /// </param>
    public static long Refresh(Func<long>? provider, long currentValue, ILogger? logger = null)
    {
        if (provider is null) return currentValue;
        try
        {
            return provider();
        }
        catch (Exception ex)
        {
            LogPollProviderThrew(logger ?? _defaultLogger, ex);
            return currentValue;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rate-limit rejected-count provider threw during Poll; keeping last value")]
    private static partial void LogPollProviderThrew(ILogger logger, Exception ex);
}