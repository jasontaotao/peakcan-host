using PeakCan.Host.Core.Uds.FlashPipeline;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.App.ViewModels.Uds.FlashPipeline;

/// <summary>
/// Provides ODX-derived flash configuration to the flashing panel.
/// Defined in spec §8.3 (2026-07-24-uds-flash-pipeline-phase-2-design.md).
/// Implementation is registered at ODX-import time; the flash panel reads it.
/// </summary>
public interface IFlashConfigurationProvider
{
    /// <summary>
    /// Return the erase routine ID for a given address range, or null if ODX
    /// doesn't define it (Phase 2 optional).
    /// </summary>
    ushort? GetEraseRoutineId(uint startAddress, uint size);

    /// <summary>
    /// Return the ODX-derived SecurityAccess config, or null if unavailable.
    /// </summary>
    SecurityAccessConfig? GetSecurityAccessConfig();

    /// <summary>
    /// Return the checksum algorithm type (defaults to Crc32 if not ODX-derived).
    /// </summary>
    ChecksumAlgorithm GetChecksumAlgorithm();
}
