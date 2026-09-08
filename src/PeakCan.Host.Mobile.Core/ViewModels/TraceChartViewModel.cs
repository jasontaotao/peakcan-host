using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Core.ViewModels;

/// <summary>
/// Holds selected DBC signal samples for the trace chart. Ingest is safe on
/// the player thread; render model changes are raised on the UI dispatcher.
/// </summary>
public sealed class TraceChartViewModel : ObservableObject
{
    private const int DefaultRenderBuckets = 512;
    private readonly IUiDispatcher _ui;
    private readonly Dictionary<SignalSelectionKey, SignalSeriesStore> _stores = new();
    private readonly Dictionary<SignalSelectionKey, IReadOnlyList<ChartPoint>> _renderPoints = new();
    private SignalCatalog? _catalog;
    private SignalCursor? _cursor;

    public TraceChartViewModel(DbcCatalog? catalog, IUiDispatcher ui)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        SetCatalog(catalog);
    }

    public IReadOnlyList<SignalCatalogMessage> Messages =>
        _catalog?.Messages ?? [];

    public IReadOnlyList<SignalSelectionItem> SelectedSignals { get; private set; } = [];

    public IReadOnlyDictionary<SignalSelectionKey, IReadOnlyList<ChartPoint>> RenderPoints =>
        _renderPoints;

    public ChartCursor? Cursor =>
        _cursor is null ? null : new ChartCursor(_cursor.Value.Timestamp, _cursor.Value.Minimum, _cursor.Value.Maximum);

    public event EventHandler? RenderChanged;

    /// <summary>Replace the DBC catalog, clear samples, and retain only valid selections.</summary>
    public void SetCatalog(DbcCatalog? catalog)
    {
        _catalog = catalog is null ? null : SignalCatalog.FromDbc(catalog);
        var previous = SelectedSignals.ToList();
        _stores.Clear();
        _renderPoints.Clear();
        _cursor = null;
        SelectedSignals = [];

        foreach (var item in previous)
        {
            if (FindItem(item.Key) is null) continue;
            SelectedSignals = [.. SelectedSignals, item];
            _stores.Add(item.Key, new SignalSeriesStore());
        }

        RaiseRenderChanged();
    }

    /// <summary>Select one signal. Returns false for unknown messages or when two are already selected.</summary>
    public bool Select(SignalSelectionKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_stores.ContainsKey(key)) return true;
        if (_stores.Count >= 2) return false;

        var item = FindItem(key);
        if (item is null) return false;

        var selected = SelectedSignals.ToList();
        selected.Add(item);
        SelectedSignals = selected;
        _stores.Add(key, new SignalSeriesStore());
        RaiseRenderChanged();
        return true;
    }

    /// <summary>Remove a selected signal and its samples.</summary>
    public void Deselect(SignalSelectionKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_stores.Remove(key)) return;

        SelectedSignals = SelectedSignals.Where(i => !i.Key.Equals(key)).ToArray();
        _renderPoints.Remove(key);
        RaiseRenderChanged();
    }

    /// <summary>Clear all samples and the cursor; selections remain.</summary>
    public void Clear()
    {
        foreach (var store in _stores.Values) store.Clear();
        _renderPoints.Clear();
        _cursor = null;
        RaiseRenderChanged();
    }

    /// <summary>Decode and store a frame if it matches a selected signal.</summary>
    public void Ingest(ReplayFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_catalog is null) return;

        foreach (var (key, store) in _stores)
        {
            if (key.CanId != frame.Id || key.IsExtended != frame.IsExtended) continue;
            if (_catalog.TryDecodeSignal(frame.Id, frame.IsExtended, frame.Data, frame.Dlc, key.SignalName, out var value))
                store.Add(frame.Timestamp, value);
        }
    }

    /// <summary>Update the vertical playback cursor.</summary>
    public void UpdateCursor(double timestamp, double? minimum = null, double? maximum = null)
    {
        if (!double.IsFinite(timestamp)) return;
        _cursor = new SignalCursor(timestamp, minimum ?? _cursor?.Minimum, maximum ?? _cursor?.Maximum);
        RaiseRenderChanged();
    }

    /// <summary>Regenerate downsampled render points for selected series.</summary>
    public void RefreshRender()
    {
        _renderPoints.Clear();
        foreach (var (key, store) in _stores)
            _renderPoints.Add(key, store.GetRenderPoints(DefaultRenderBuckets));

        RaiseRenderChanged();
    }

    private SignalSelectionItem? FindItem(SignalSelectionKey key)
    {
        var message = Messages.FirstOrDefault(m =>
            m.CanId == key.CanId && m.IsExtended == key.IsExtended && m.Name == key.MessageName);
        var signal = message?.Signals.FirstOrDefault(s => s.Name == key.SignalName);
        if (message is null || signal is null) return null;

        return new SignalSelectionItem(
            key,
            $"{message.Name}.{signal.Name}",
            signal.Unit);
    }

    private void RaiseRenderChanged()
    {
        _ui.Post(() => RenderChanged?.Invoke(this, EventArgs.Empty));
    }

    private readonly record struct SignalCursor(double Timestamp, double? Minimum, double? Maximum);
}




