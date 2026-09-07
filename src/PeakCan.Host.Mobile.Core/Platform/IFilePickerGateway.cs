namespace PeakCan.Host.Mobile.Core.Platform;

public sealed record PickedTraceFile(string DisplayName, long SizeBytes, Func<CancellationToken, Task<Stream>> OpenReadAsync);

/// <summary>File picking + cache-directory provider. Abstracted so VM/tests
/// don't depend on MAUI FilePicker/FileSystem APIs directly.</summary>
public interface IFilePickerGateway
{
    string CacheDirectory { get; }

    /// <summary>Open the system file picker for trace files. Returns null if the user cancelled.</summary>
    Task<PickedTraceFile?> PickTraceFileAsync(CancellationToken ct = default);
}
