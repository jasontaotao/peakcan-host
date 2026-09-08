using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.ViewModels;
using PeakCan.Host.Mobile.Views;

namespace PeakCan.Host.Mobile.Platform;

public sealed class TracePageFactory(IServiceProvider services) : ITracePageFactory
{
    public ContentPage Create(string cachedFilePath, string sourceName, long fileSizeBytes)
        => new TracePage(
            services.GetRequiredService<IUiDispatcher>(),
            services.GetRequiredService<IStreamingSourceFactory>(),
            cachedFilePath,
            sourceName,
            fileSizeBytes,
            services.GetRequiredService<ILogger<TraceSessionViewModel>>());

    public ContentPage CreateBrowse(long traceId)
        => new ContentPage
        {
            Title = "Trace",
            Content = new Label { Text = $"Cached trace {traceId}" }
        };
}
