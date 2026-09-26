using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core.Xcp.Replay;

namespace PeakCan.Host.App.ViewModels.Xcp;

/// <summary>
/// S6-T4 MAP 只读可视化面板 VM（spec D4）。读路径由组合根注入（在线 UPLOAD / 离线兜底
/// 的选择在组合根，App 不碰协议原语）；渲染面归视图 code-behind（与回放页同款分层）。
/// </summary>
public sealed partial class XcpMapPanelViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<string>> _mapsProvider;
    private readonly Func<string, CancellationToken, Task<XcpMapData>> _readMap;

    public XcpMapPanelViewModel(
        Func<IReadOnlyList<string>>? mapsProvider = null,
        Func<string, CancellationToken, Task<XcpMapData>>? readMap = null)
    {
        _mapsProvider = mapsProvider ?? (() => []);
        _readMap = readMap ?? ((_, _) =>
            Task.FromException<XcpMapData>(
                new InvalidOperationException("组合根未接线 MAP 读函数")));
    }

    public ObservableCollection<string> Maps { get; } = new();

    [ObservableProperty]
    private string? _selectedMap;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public XcpMapData? Result { get; private set; }

    public int RenderRequestCount { get; private set; }

    public event Action? RenderRequested;

    /// <summary>A2L 加载/切换后装配 MAP 清单（真机 15 个，App 只做过滤不解析）。</summary>
    public void RefreshMaps()
    {
        var names = _mapsProvider();
        Maps.Clear();
        foreach (var n in names)
            Maps.Add(n);
        if (SelectedMap is not null && !Maps.Contains(SelectedMap))
            SelectedMap = null;
        StatusText = Maps.Count == 0 ? "当前 A2L 无 MAP 对象（或未加载 A2L）" : $"{Maps.Count} 个 MAP 对象";
    }

    [RelayCommand]
    public async Task ReadAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(SelectedMap))
        {
            StatusText = "请先选择 MAP 对象";
            return;
        }
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            Result = await _readMap(SelectedMap, ct).ConfigureAwait(true);
            StatusText = Result.Detail;
            RequestRender();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            StatusText = $"读取失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RequestRender()
    {
        RenderRequestCount++;
        RenderRequested?.Invoke();
    }
}
