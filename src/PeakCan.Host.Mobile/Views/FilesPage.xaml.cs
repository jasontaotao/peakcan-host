using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;
using PeakCan.Host.Mobile.Platform;

namespace PeakCan.Host.Mobile.Views;

public partial class FilesPage : ContentPage
{
    private readonly IFilePickerGateway _picker;
    private readonly TraceFileCache _cache;
    private readonly ITracePageFactory _tracePageFactory;

    public record RecentItem(string DisplayName, string Subtitle, string CachedPath);

    public FilesPage(IFilePickerGateway picker, TraceFileCache cache, ITracePageFactory tracePageFactory)
    {
        InitializeComponent();
        _picker = picker;
        _cache = cache;
        _tracePageFactory = tracePageFactory;
        RefreshRecent();
        MainActivity.FileUriReceived += OnFileUriReceived;
    }

    private async void OnFileUriReceived(Android.Net.Uri uri)
    {
        await HandleIntentUriAsync(uri);
    }

    private void RefreshRecent()
    {
        var items = Directory.GetFiles(_cache.CacheDirectory, "*.asc")
            .Select(p =>
            {
                var info = new FileInfo(p);
                var suffix = $".{info.Length}.asc";
                var name = info.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    ? info.Name[..^suffix.Length]
                    : info.Name;
                return new RecentItem(name, $"{info.Length / 1024} KB", p);
            })
            .ToList();
        RecentList.ItemsSource = items;
    }

    private async void OnOpenClicked(object? sender, EventArgs e)
    {
        try
        {
            var picked = await _picker.PickTraceFileAsync();
            if (picked is null) return;
            var path = await _cache.ImportAsync(picked);
            RefreshRecent();
            await Navigation.PushAsync(_tracePageFactory.Create(path));
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
            await Navigation.PushAsync(_tracePageFactory.Create(item.CachedPath));
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("无法打开文件", ex.Message, "确定");
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var uri = MainActivity.TakePendingFileUri();
        if (uri is null) return;
        await HandleIntentUriAsync(uri);
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

            RefreshRecent();
            await Navigation.PushAsync(_tracePageFactory.Create(dest));
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("无法打开文件", ex.Message, "确定");
        }
    }
}



