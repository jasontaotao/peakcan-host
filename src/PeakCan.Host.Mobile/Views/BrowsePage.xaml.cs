using PeakCan.Host.Mobile.Core.Services;
using PeakCan.Host.Mobile.Core.ViewModels;

namespace PeakCan.Host.Mobile.Views;

public partial class BrowsePage : ContentPage
{
    private readonly TraceBrowseViewModel _vm;

    public BrowsePage(ITraceCacheStore cacheStore, long traceId)
    {
        InitializeComponent();
        _vm = new TraceBrowseViewModel(cacheStore);
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

    private void OnFirst(object? sender, EventArgs e) => _vm.FirstCommand.Execute(null);
    private void OnPrevious(object? sender, EventArgs e) => _vm.PreviousCommand.Execute(null);
    private void OnNext(object? sender, EventArgs e) => _vm.NextCommand.Execute(null);

    private void OnApplyFilter(object? sender, EventArgs e)
    {
        _vm.FilterText = FilterEntry.Text;
        _vm.ApplyFilterCommand.Execute(null);
    }
}

