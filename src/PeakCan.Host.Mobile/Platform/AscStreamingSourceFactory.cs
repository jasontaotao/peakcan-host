using PeakCan.Host.Core.Replay;
using PeakCan.Host.Mobile.Core.Platform;

namespace PeakCan.Host.Mobile.Platform;

public sealed class AscStreamingSourceFactory : IStreamingSourceFactory
{
    public IStreamingTraceSource Create(string cachedFilePath)
        => new AscStreamingSource(() => new FileStream(cachedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read));
}
