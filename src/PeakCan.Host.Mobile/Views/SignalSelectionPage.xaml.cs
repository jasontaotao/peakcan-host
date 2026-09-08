using PeakCan.Host.Mobile.Core.Services;
using PeakCan.Host.Mobile.Core.ViewModels;

namespace PeakCan.Host.Mobile.Views;

public partial class SignalSelectionPage : ContentPage
{
    private readonly TraceChartViewModel _chart;

    public SignalSelectionPage(TraceChartViewModel chart)
    {
        InitializeComponent();
        _chart = chart;
        BuildRows();
    }

    private record Choice(
        SignalCatalogMessage Message,
        SignalCatalogSignal Signal,
        string DisplayName,
        bool IsSelected)
    {
        public SignalSelectionKey Key => new(
            Message.CanId,
            Message.IsExtended,
            Message.Name,
            Signal.Name);
    }

    private void BuildRows()
    {
        var rows = _chart.Messages
            .SelectMany(message => message.Signals.Select(signal => new Choice(
                message,
                signal,
                $"{message.Name}.{signal.Name}",
                IsSelected(message, signal))))
            .ToArray();
        SignalList.ItemsSource = rows;
    }

    private bool IsSelected(SignalCatalogMessage message, SignalCatalogSignal signal) =>
        _chart.SelectedSignals.Any(i => i.Key.CanId == message.CanId
            && i.Key.IsExtended == message.IsExtended
            && i.Key.MessageName == message.Name
            && i.Key.SignalName == signal.Name);

    private async void OnSignalTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject { BindingContext: Choice choice }) return;

        if (IsSelected(choice.Message, choice.Signal))
        {
            _chart.Deselect(choice.Key);
        }
        else if (!_chart.Select(choice.Key))
        {
            await DisplayAlertAsync("无法选择", "最多选择 2 个信号。", "确定");
            return;
        }

        BuildRows();
    }

    private async void OnDoneClicked(object? sender, EventArgs e)
    {
        await Navigation.PopAsync();
    }
}
