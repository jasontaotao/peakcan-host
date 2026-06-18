namespace PeakCan.Host.Core;

/// <summary>
/// Failure payload for a <see cref="Result{T}"/>. Immutable.
/// </summary>
/// <param name="Code">Categorical error type (see <see cref="ErrorCode"/>).</param>
/// <param name="Message">Human-readable, single-sentence description.</param>
public sealed record Error(ErrorCode Code, string Message);