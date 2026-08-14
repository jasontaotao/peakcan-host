namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// P1-5: one tab in the dual-TabControl main area / right panel. The view is
/// created lazily on first <see cref="View"/> access — keeps the shell ctor
/// STA-free (xunit runs MTA) and defers heavy views like ScriptView's WebView2
/// until the tab is first selected. The cached instance is reused on tab
/// round-trips (DataGrid virtualization state / WebView2 survive).
/// </summary>
public sealed class TabSpec
{
    public string Header { get; }

    private readonly Func<object> _factory;
    private object? _view;

    public object View => _view ??= _factory();

    public TabSpec(string header, Func<object> factory)
    {
        Header = header;
        _factory = factory;
    }
}
