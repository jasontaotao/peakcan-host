namespace PeakCan.Host.App.Services;

/// <summary>
/// v1.6.6 PATCH Item 1: opt-in limits applied by
/// <see cref="DbcService.LoadAsync"/> to bound the work a single
/// DBC load can perform. Both caps default to
/// <see cref="Unlimited"/> (i.e. <c>0</c> = unlimited) so existing
/// callers and tests see no behavior change.
/// <para>
/// <b>Bound from <c>appsettings.json</c>:</b> the App DI factory
/// (<see cref="Composition.AppHostBuilder.Build"/>) reads
/// <c>Dbc:MaxFileSizeBytes</c> + <c>Dbc:MaxMessageCount</c> at
/// host startup and constructs this record. Tests may construct
/// it directly or use <see cref="Unlimited"/>.
/// </para>
/// <para>
/// <b>Why in App (not Core):</b> config-bound options belong in
/// the App layer per NetArchTest rule 2 (Core must not depend
/// on PEAK SDK; option records are part of the App-layer DI seam).
/// </para>
/// <para>
/// <b>Failure envelope:</b> caps reject by failing the load via
/// <see cref="Result{T}.Fail(ErrorCode.ParseFailure, ...)"/> with a
/// disambiguating message string ("exceeds MaxFileSizeBytes N" or
/// "exceeds MaxMessageCount N"). The internal
/// <c>DbcErrorCode.FileTooLarge</c> slot at
/// <c>DbcErrorCode.cs:19</c> is intentionally unused — the shared
/// <c>ErrorCode</c> envelope is the canonical error channel (see
/// <c>DbcService.cs:120,129</c>).
/// </para>
/// </summary>
/// <param name="MaxFileSizeBytes">
/// Maximum raw byte length of the DBC file on disk. Checked before
/// <c>File.ReadAllBytesAsync</c> at <c>DbcService.LoadAsync</c> entry.
/// <c>0</c> = unlimited. Negative values treated as 0.
/// </param>
/// <param name="MaxMessageCount">
/// Maximum number of top-level <c>BO_</c> messages per parse.
/// Enforced mid-parse inside <c>DbcParser.ParseMessage</c>.
/// <c>0</c> = unlimited. Negative values treated as 0.
/// </param>
internal sealed record DbcOptions(long MaxFileSizeBytes, int MaxMessageCount)
{
    /// <summary>
    /// Both caps set to 0 — disables all v1.6.6 PATCH limits.
    /// Equivalent to the v1.6.5 PATCH baseline behavior (no caps).
    /// </summary>
    public static DbcOptions Unlimited { get; } = new(0, 0);
}
