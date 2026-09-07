using Microsoft.Extensions.DependencyInjection;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Views;

namespace PeakCan.Host.Mobile.Platform;

public sealed class TracePageFactory(IServiceProvider services) : ITracePageFactory
{
    public ContentPage Create(string cachedFilePath)
        => new TracePage(
            services.GetRequiredService<IUiDispatcher>(),
            services.GetRequiredService<IStreamingSourceFactory>(),
            cachedFilePath);
}
