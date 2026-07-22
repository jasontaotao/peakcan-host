using PeakCan.Host.Core.Uds.FlashPipeline;

namespace PeakCan.Host.App.ViewModels.Uds.FlashPipeline;

/// <summary>
/// Builds a <see cref="ISecondaryFlashStack"/> for one flash run from the profile's
/// programming CAN-ID pair + the SecurityAccess step (which selects the key-algorithm
/// shape — Manual=Placeholder, Dll=runtime-loaded OEM DLL). Factory seam lets the
/// <see cref="FlashPanelViewModel"/> be unit-tested against a recording stack without
/// touching the wire or loading a native DLL.
/// </summary>
internal interface ISecondaryFlashStackFactory
{
    /// <summary>
    /// Build the secondary stack. The caller (<see cref="FlashPanelViewModel"/>) is
    /// responsible for <see cref="ISecondaryFlashStack.AttachToRouter"/> + Dispose ordering.
    /// </summary>
    /// <param name="securityStep">The SecurityAccess step snapshot (level + mode + key/dll).</param>
    /// <param name="profile">The profile supplying the programming CAN-ID pair.</param>
    ISecondaryFlashStack Build(FlashStepSnapshot securityStep, FlashProfile profile);
}
