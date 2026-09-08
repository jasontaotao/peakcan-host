using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;
using PeakCan.Host.Mobile.Platform;

namespace PeakCan.Host.Mobile.Views;

public partial class FilesPage : ContentPage
{
    private readonly IFilePickerGateway _picker;
    private readonly TraceFileCache _cache;
    private readonly ITracePageFactory _tracePageFactory;
    private readonly ITraceCacheStore _cacheStore;

    public record RecentItem(
        string DisplayName,
        string Subtitle,
        string? CachedPath,
        long? TraceId,
        long FileSizeBytes);

    public FilesPage(
        IFilePickerGateway picker,
        TraceFileCache cache,
        ITracePageFactory tracePageFactory,
        ITraceCacheStore cacheStore)
    {
        InitializeComponent();
        _picker = picker;
        _cache = cache;
        _tracePageFactory = tracePageFactory;
        _cacheStore = cacheStore;
        MainActivity.FileUriReceived += OnFileUriReceived;
    }

    private async void OnFileUriReceived(Android.Net.Uri uri)
    {
        await HandleIntentUriAsync(uri);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshRecentAsync();
        var uri = MainActivity.TakePendingFileUri();
        if (uri is not null)
            await HandleIntentUriAsync(uri);
    }

    private async Task RefreshRecentAsync()
    {
        var items = new List<RecentItem>();
        var traces = await _cacheStore.ListTracesAsync();
        foreach (var trace in traces)
        {
            var state = trace.Complete ? "完整" : "部分";
            items.Add(new RecentItem(
                trace.SourceName,
                $"{trace.FileSizeBytes / 1024} KB · {trace.FrameCount} 帧 · {TimeSpan.FromSeconds(trace.Duration):hh\\:mm\\:ss} · {state} · 上次 {TimeSpan.FromSeconds(trace.LastPositionSeconds):hh\\:mm\\:ss}",
                null,
                trace.TraceId,
                trace.FileSizeBytes));
        }

        var cachedIds = traces.Select(t => (t.SourceName, t.FileSizeBytes)).ToHashSet();
        foreach (var path in Directory.GetFiles(_cache.CacheDirectory, "*.asc"))
        {
            var info = new FileInfo(path);
            var suffix = $".{info.Length}.asc";
            var name = info.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? info.Name[..^suffix.Length]
                : info.Name;
            if (cachedIds.Contains((name, info.Length))) continue;
            items.Add(new RecentItem(name, $"{info.Length / 1024} KB", path, null, info.Length));
        }

        RecentList.ItemsSource = items;
    }

    private async void OnOpenClicked(object? sender, EventArgs e)
    {
        try
        {
            var picked = await _picker.PickTraceFileAsync();
            if (picked is null) return;

            var completed = await _cacheStore.FindCompletedAsync(picked.DisplayName, picked.SizeBytes);
            if (completed is not null)
            {
                await Navigation.PushAsync(_tracePageFactory.CreateBrowse(completed.TraceId));
                return;
            }

            var path = await _cache.ImportAsync(picked);
            await RefreshRecentAsync();
            await Navigation.PushAsync(_tracePageFactory.Create(path, picked.DisplayName, picked.SizeBytes));
        }
        catch (InvalidOperationException ex)
        {
            await DisplayAlertAsync("无法打开文件", ex.Message, "确定");
        }
    }

    private async void OnRecentSelected(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (e.CurrentSelection.FirstOrDefault() is not RecentItem item) return;
            RecentList.SelectedItem = null;
            if (item.TraceId is long traceId)
                await Navigation.PushAsync(_tracePageFactory.CreateBrowse(traceId));
            else if (item.CachedPath is string path)
                await Navigation.PushAsync(_tracePageFactory.Create(path, item.DisplayName, item.FileSizeBytes));
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("无法打开文件", ex.Message, "确定");
        }
    }

    private async Task HandleIntentUriAsync(Android.Net.Uri uri)
    {
        try
        {
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is not MainActivity activity) return;
            var ext = Path.GetExtension(uri.ToString());
            if (!ext.Equals(".asc", StringComparison.OrdinalIgnoreCase))
            {
                await DisplayAlertAsync("不支持的文件", "仅支持 .asc 格式文件", "确定");
                return;
            }

            var dest = Path.Combine(_cache.CacheDirectory, $"shared-{DateTime.Now:yyyyMMdd-HHmmss}.asc");
            using var src = activity.ContentResolver?.OpenInputStream(uri);
            if (src is null) return;

            // The ACTION_VIEW/SEND path bypasses FilePicker, so apply the same
            // product size boundary here and fail before creating a huge cache file.
            if (src.CanSeek) TraceFileCache.EnsureSupportedSize(src.Length);

            long copied = 0;
            try
            {
                using var dst = File.Create(dest);
                var buffer = new byte[256 * 1024];
                int n;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    copied += n;
                    TraceFileCache.EnsureSupportedSize(copied);
                    await dst.WriteAsync(buffer.AsMemory(0, n));
                }
            }
            catch
            {
                File.Delete(dest);
                throw;
            }

            var importedInfo = new FileInfo(dest);
            await RefreshRecentAsync();
            await Navigation.PushAsync(_tracePageFactory.Create(dest, importedInfo.Name, importedInfo.Length));
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("无法打开文件", ex.Message, "确定");
        }
    }
}
