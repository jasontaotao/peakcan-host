using System.ComponentModel;
using OxyPlot;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.2.0 MINOR: metadata for a single loaded trace in a multi-trace
/// overlay session. The registry owns the underlying
/// <see cref="PeakCan.Host.Core.Replay.ITraceViewerService"/> for
/// each <see cref="TraceSource"/>; consumers should not hold direct
/// references to the service — go through the registry.
/// <para>
/// v3.4.3 PATCH: <see cref="CanIdFilter"/> is a mutable per-source
/// filter string with manual <see cref="INotifyPropertyChanged"/>.
/// The other five fields stay init-only to preserve v3.2.0-v3.4.2
/// back-compat at the LoadAsync construction site. Pure Core-style
/// design: no <c>CommunityToolkit.Mvvm</c> dependency in this file —
/// callers must subscribe to <see cref="PropertyChanged"/> directly.
/// </para>
/// </summary>
public sealed class TraceSource : INotifyPropertyChanged
{
    public string SourceId { get; }
    public string DisplayName { get; }
    public string Path { get; }
    public OxyColor Color { get; }
    public LineStyle StrokeStyle { get; }

    private string _canIdFilter = "";

    /// <summary>
    /// v3.4.3 PATCH: per-source comma-separated CAN ID allow-list
    /// (decimal or 0x-hex, case-insensitive). Empty = inherit the
    /// Trace Viewer's global filter. Non-empty = override the global
    /// for this source. Parsed by <c>CanIdFilterParser</c> at the VM
    /// layer, not here — invalid tokens are silently skipped at parse
    /// time. Implements <see cref="INotifyPropertyChanged"/> on this
    /// property only.
    /// </summary>
    public string CanIdFilter
    {
        get => _canIdFilter;
        set
        {
            if (_canIdFilter == value) return;
            _canIdFilter = value ?? "";
            PropertyChanged?.Invoke(this,
                new PropertyChangedEventArgs(nameof(CanIdFilter)));
        }
    }

    // Explicit ctor preserves the v3.2.0 positional call-site shape:
    //   new TraceSource("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid)
    public TraceSource(
        string sourceId,
        string displayName,
        string path,
        OxyColor color,
        LineStyle strokeStyle = LineStyle.Solid)
    {
        SourceId = sourceId ?? throw new ArgumentNullException(nameof(sourceId));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Color = color;
        StrokeStyle = strokeStyle;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}