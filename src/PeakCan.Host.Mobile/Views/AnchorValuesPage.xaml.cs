using System.Globalization;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Mobile.Core.ViewModels;

namespace PeakCan.Host.Mobile.Views;

public partial class AnchorValuesPage : ContentPage
{
    private readonly AnchorValuesViewModel _vm;
    private readonly double _timestamp;
    private readonly ILogger? _logger;

    public AnchorValuesPage(AnchorValuesViewModel vm, double timestamp, ILogger? logger = null)
    {
        InitializeComponent();
        _vm = vm;
        _timestamp = timestamp;
        _logger = logger;
        TitleLabel.Text = $"锚点 {timestamp.ToString("F6", CultureInfo.InvariantCulture)}s";
        BindingContext = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            await _vm.LoadAsync(_timestamp);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "锚点信号值加载失败 ts={Timestamp}", _timestamp);
        }
    }
}