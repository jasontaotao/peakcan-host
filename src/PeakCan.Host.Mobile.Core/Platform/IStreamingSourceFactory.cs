using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Mobile.Core.Platform;

/// <summary>Factory of <see cref="IStreamingTraceSource"/> for a file path. Abstracted for testability.</summary>
public interface IStreamingSourceFactory
{
    IStreamingTraceSource Create(string cachedFilePath);
}
