namespace PeakCan.Host.Core.Path;

/// <summary>
/// v1.6.10 PATCH Item 2: opt-in extension of v1.6.4 PATCH's hardcoded
/// <c>[PathNormalizer.LocalAppDataPeakCanRoot]</c> allowlist. Binds from
/// <c>Path:AllowedRoots:[]</c> in <c>appsettings.json</c> via
/// <c>AppHostBuilder</c> DI.
/// <para>
/// <b>Back-compat:</b> if <c>Path:AllowedRoots</c> is absent from
/// configuration, AppHostBuilder falls back to <see cref="Default"/>
/// (v1.6.4 PATCH behavior). If configured as an empty array
/// (<c>Path:AllowedRoots:[]</c>), the empty allowlist rejects every
/// path (security hardening per <see cref="PathNormalizer.NormalizeRestricted"/>'s
/// convention).
/// </para>
/// </summary>
/// <param name="AllowedRoots">
/// Case-insensitive root directories the user may load JSON files from.
/// Forwarded to <see cref="PathNormalizer.NormalizeRestricted"/>'s
/// <c>IReadOnlyCollection&lt;string&gt;</c> parameter.
/// </param>
internal sealed record PathOptions(IReadOnlyList<string> AllowedRoots)
{
    /// <summary>
    /// v1.6.4 PATCH back-compat default: only
    /// <c>%LOCALAPPDATA%\PeakCan.Host</c>.
    /// </summary>
    public static PathOptions Default { get; } =
        new([PathNormalizer.LocalAppDataPeakCanRoot]);
}