using System.IO;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Mobile.Core.Platform;

namespace PeakCan.Host.Mobile.Platform;

public sealed class StreamingSourceFactory : IStreamingSourceFactory
{
    public IStreamingTraceSource Create(string cachedFilePath)
    {
        Func<Stream> streamFactory = () => new FileStream(
            cachedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Path.GetExtension(cachedFilePath).Equals(".blf", StringComparison.OrdinalIgnoreCase)
            ? new BlfStreamingSource(streamFactory)
            : new AscStreamingSource(streamFactory);
    }
}
