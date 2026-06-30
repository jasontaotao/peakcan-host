using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;

namespace PeakCan.Host.App.Composition;

/// <summary>
/// Raw <see cref="SendService"/> instance used by callers exempt from
/// the v1.6.5 PATCH token-bucket rate-limit decorator
/// (<see cref="RateLimitedSendService"/>).
/// <para>
/// The DI container registers this concrete type separately from
/// <see cref="SendService"/>: UI callers (CanApi, SendViewModel,
/// DbcSendViewModel, CyclicSendService, CyclicDbcSendService) resolve
/// <see cref="SendService"/> and receive the decorator via C#
/// polymorphism; callers with rate-unfriendly semantics (Replay timeline
/// — must honor ASC timestamps — and IsoTpLayer — ISO 15765-2 has its
/// own STmin pacing) resolve <see cref="CoreSendService"/> directly
/// and bypass the rate gate.
/// </para>
/// <para>
/// v1.6.5 PATCH Item 1 — see
/// <c>docs/superpowers/specs/2026-06-30-v1-6-5-patch-design.md</c>.
/// </para>
/// </summary>
internal sealed partial class CoreSendService : SendService
{
    /// <summary>
    /// Forward the logger dependency to the base
    /// <see cref="SendService"/>; behavior is identical to a direct
    /// <c>SendService</c> instance.
    /// </summary>
    public CoreSendService(ILogger<SendService> logger) : base(logger)
    {
    }
}
