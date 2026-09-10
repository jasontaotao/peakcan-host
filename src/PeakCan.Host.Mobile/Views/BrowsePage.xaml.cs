using PeakCan.Host.Mobile.Core.Services;
using PeakCan.Host.Mobile.Core.ViewModels;

namespace PeakCan.Host.Mobile.Views;

public partial class BrowsePage : ContentPage
{
    private readonly TraceBrowseViewModel _vm;

    public BrowsePage(ITraceCacheStore cacheStore, DbcCatalogHolder dbcHolder, long traceId)
    {
        InitializeComponent();
        _vm = new TraceBrowseViewModel(cacheStore);
        _vm.SetDbc(dbcHolder.Current);
        BindingContext = _vm;
        _ = InitializeAsync(traceId);
    }

    private async Task InitializeAsync(long traceId)
    {
        try
        {
            await _vm.OpenAsync(traceId);
        }
        catch (Exception ex)
        {
            _vm.ErrorMessage = ex.Message;
        }
    }

    private void OnFirst(object? sender, EventArgs e)
    {
        _ = _vm.FirstAsync();
    }
    private void OnPrevious(object? sender, EventArgs e) => _vm.PreviousCommand.Execute(null);
    private void OnNext(object? sender, EventArgs e) => _vm.NextCommand.Execute(null);

    private void OnApplyFilter(object? sender, EventArgs e)
    {
        _vm.FilterText = FilterEntry.Text;
        _vm.ApplyFilterCommand.Execute(null);
    }

    private async void OnJumpFirstClicked(object? sender, EventArgs e) => await JumpAsync(first: true);

    private async void OnJumpNextClicked(object? sender, EventArgs e) => await JumpAsync(first: false);

    private async Task JumpAsync(bool first)
    {
        if (!TryParseJumpId(out var id)) return;
        var ok = await _vm.JumpToAsync(id, first);
        JumpStatus.Text = ok ? $"已定位 0x{id:X3}" : "未找到";
    }

    private bool TryParseJumpId(out uint id)
    {
        id = 0;
        var text = JumpEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parsed = PeakCan.Host.Core.Replay.CanIdListParser.Parse(text);
        // Browse 搜索与 Trace 同语义：仅单个纯 CAN ID；PGN token 拒绝
        if (parsed.AllowList is not { Count: 1 } || parsed.PgnAllowList is not null)
        {
            JumpStatus.Text = "搜索仅支持单个 CAN ID";
            return false;
        }
        id = parsed.AllowList.First();
        return true;
    }
}



