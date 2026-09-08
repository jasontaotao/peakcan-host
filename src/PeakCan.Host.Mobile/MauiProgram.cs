using LiveChartsCore.SkiaSharpView.Maui;
using Microsoft.Extensions.Logging;
using SkiaSharp.Views.Maui.Controls.Hosting;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;
using PeakCan.Host.Mobile.Platform;
using PeakCan.Host.Mobile.Views;

namespace PeakCan.Host.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseSkiaSharp()
            .UseMauiApp<App>()
            .UseLiveCharts()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddSingleton<IUiDispatcher, PlatformUiDispatcher>();
        builder.Services.AddSingleton<IFilePickerGateway, MauiFilePickerGateway>();
        builder.Services.AddSingleton<IStreamingSourceFactory, StreamingSourceFactory>();
        builder.Services.AddSingleton<TraceFileCache>(_ => new TraceFileCache(FileSystem.CacheDirectory));
        builder.Services.AddSingleton<ITraceCacheStore>(_ =>
            new TraceCacheStore(Path.Combine(FileSystem.CacheDirectory, "trace-cache.sqlite3")));
        builder.Services.AddSingleton<ITraceCacheSinkFactory, TraceCacheWriterFactory>();
        builder.Services.AddSingleton<IDbcCatalogProvider, MauiDbcCatalogProvider>();
        builder.Services.AddSingleton<DbcCatalogHolder>();
        builder.Services.AddSingleton<ITracePageFactory, TracePageFactory>();
        builder.Services.AddTransient<FilesPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}





