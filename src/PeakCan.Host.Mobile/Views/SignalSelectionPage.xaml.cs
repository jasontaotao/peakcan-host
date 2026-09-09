using CommunityToolkit.Mvvm.ComponentModel;
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

    private sealed partial class Choice : ObservableObject
    {
        public Choice(
            SignalCatalogMessage message,
            SignalCatalogSignal signal,
            string displayName,
            bool isSelected)
        {
            Message = message;
            Signal = signal;
            DisplayName = displayName;
            _isSelected = isSelected;
        }

        public SignalCatalogMessage Message { get; }
        public SignalCatalogSignal Signal { get; }
        public string DisplayName { get; }

        public SignalSelectionKey Key => new(
            Message.CanId,
            Message.IsExtended,
            Message.Name,
            Signal.Name);

        // INPC 勾选标记就地更新，避免重建 ItemsSource 造成列表滚动复位。
        [ObservableProperty]
        private bool _isSelected;
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

        if (choice.IsSelected)
        {
            _chart.Deselect(choice.Key);
            choice.IsSelected = false;
            return;
        }

        if (_chart.Select(choice.Key))
        {
            choice.IsSelected = true;
            return;
        }

        await DisplayAlertAsync("无法选择", "最多选择 4 个信号。", "确定");
    }

    private async void OnDoneClicked(object? sender, EventArgs e)
    {
        await Navigation.PopAsync();
    }
}
