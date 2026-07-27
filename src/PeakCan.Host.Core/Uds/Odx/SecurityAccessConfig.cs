namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// ODX-derived 0x27 SecurityAccess parameters. Returned by
/// <see cref="SecurityAccessExtractor"/>. Null if ODX has no 0x27 definition.
/// Shape aligns with spec §8.3 (2026-07-24-uds-flash-pipeline-phase-2-design.md)
/// IFlashConfigurationProvider.GetSecurityAccessConfig() return type.
/// </summary>
/// <param name="Level">Security level (0x27 sub-function). E.g. 0x01, 0x11.</param>
/// <param name="SeedLength">Seed byte length from ODX POS-RESPONSE BIT-LENGTH. Null if ODX omits the structure.</param>
public sealed record SecurityAccessConfig(
    byte Level,
    int? SeedLength);
