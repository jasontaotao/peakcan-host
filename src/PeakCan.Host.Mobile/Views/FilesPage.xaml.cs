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
        foreach (var path in Directory.EnumerateFiles(_cache.CacheDirectory))
        {
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".asc", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".blf", StringComparison.OrdinalIgnoreCase))
                continue;

            var info = new FileInfo(path);
            var name = ParseCachedDisplayName(info.Name, extension);
            if (name is null || cachedIds.Contains((name, info.Length))) continue;
            items.Add(new RecentItem(name, $"{info.Length / 1024} KB", path, null, info.Length));
        }

        RecentList.ItemsSource = items;
    }

    /// <summary>Cache names are "{stem}.{size}{extension}"; restore the original display name.</summary>
    private static string? ParseCachedDisplayName(string fileName, string extension)
    {
        if (!fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return null;
        var withoutExtension = fileName[..^extension.Length];
        var lastDot = withoutExtension.LastIndexOf('.');
        if (lastDot <= 0) return null;
        if (!long.TryParse(withoutExtension[(lastDot + 1)..], out _)) return null;
        return withoutExtension[..lastDot] + extension;
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
            var extension = Path.GetExtension(uri.ToString());
            if (!extension.Equals(".asc", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".blf", StringComparison.OrdinalIgnoreCase))
            {
                await DisplayAlertAsync("不支持的文件", "仅支持 .asc 或 .blf 格式文件", "确定");
                return;
            }

            var dest = Path.Combine(
                _cache.CacheDirectory,
                $"shared-{DateTime.Now:yyyyMMdd-HHmmss}{extension.ToLowerInvariant()}");
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
