using PeakCan.Host.Core.Uds.FlashPipeline;
using PeakCan.Host.Core.Uds.Odx;
using PeakCan.Host.App.ViewModels.Uds.FlashPipeline;

namespace PeakCan.Host.App.Services;

/// <summary>
/// Mutable wrapper that holds the current ODX-derived flash configuration.
/// Registered as a DI singleton; <see cref="OdxImportService"/> calls
/// <see cref="UpdateFromOdx"/> after each import, which updates the
/// internal state and raises <see cref="ConfigUpdated"/> so subscribers
/// (e.g. <see cref="FlashPanelViewModel"/>) can refresh.
/// Implements <see cref="IFlashConfigurationProvider"/> per spec §8.3
/// (2026-07-24-uds-flash-pipeline-phase-2-design.md).
/// </summary>
public sealed class FlashConfigurationService : IFlashConfigurationProvider
{
    private SecurityAccessConfig? _securityAccess;
    private ChecksumAlgorithm _checksum = ChecksumAlgorithm.Crc32;

    /// <summary>Raised after <see cref="UpdateFromOdx"/> so subscribers can refresh.</summary>
    public event Action? ConfigUpdated;

    /// <summary>
    /// Update the SecurityAccess config from a fresh ODX import.
    /// Raises <see cref="ConfigUpdated"/> to notify subscribers.
    /// </summary>
    public void UpdateFromOdx(SecurityAccessConfig? config)
    {
        _securityAccess = config;
        ConfigUpdated?.Invoke();
    }

    /// <inheritdoc/>
    /// <remarks>Phase 2 optional per spec §8.2 — returns null (not yet derived from ODX ECU-MEMORY).</remarks>
    public ushort? GetEraseRoutineId(uint startAddress, uint size) => null;

    /// <inheritdoc/>
    public SecurityAccessConfig? GetSecurityAccessConfig() => _securityAccess;

    /// <inheritdoc/>
    /// <remarks>Phase 2 optional per spec §8.2 — defaults to Crc32.</remarks>
    public ChecksumAlgorithm GetChecksumAlgorithm() => _checksum;
}
