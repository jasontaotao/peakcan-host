namespace PeakCan.Host.App.Services;

/// <summary>
/// v1.2.11 PATCH Item 5: persisted named-CAN-frame library. Backed by a
/// JSON file in <c>%APPDATA%\PeakCan.Host\send-library.json</c>.
/// <para>
/// Task 7 adds the type as a parameter on <see cref="ViewModels.SendViewModel"/>
/// so the VM can be constructed with a library dependency ready for Task 8
/// (JSON persistence + Load/Save) and Task 9 (library commands).
/// </para>
/// </summary>
public sealed class SendFrameLibrary
{
    /// <summary>Task 7 stub — Task 8 expands with the real record shape.</summary>
    public sealed record SavedFrame(
        string Name,
        uint RawId,
        bool IsExtended,
        bool IsFd,
        bool IsRtr,
        bool BitRateSwitch,
        string DataHex,
        DateTimeOffset SavedAt);
}