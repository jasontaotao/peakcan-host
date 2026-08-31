using System.Collections.ObjectModel;
using System.IO;   // UseWPF 的隐式 using 集不含 System.IO（AppShellViewModel 同款显式引入）
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Nodes;

namespace PeakCan.Host.App.ViewModels.Nodes;

/// <summary>
/// Nodes tab 宿主 VM（DI singleton：运行状态跨 tab 切换保持，spec §10.2）。
/// <para>活动日志并入本 VM 的 <see cref="Activities"/> 集合（brief 裁定：不创建独立
/// NodeActivityViewModel 文件——spec 拆分图中的该职责由 Activities + AppendActivity 承担）。</para>
/// </summary>
public sealed partial class NodeSetupViewModel : ObservableObject
{
    private const int ActivityCapacity = 1000;
    private readonly NodeHostService _host;
    private readonly NodeConfigLibrary _library;
    private readonly IFileDialogService _fileDialogs;

    /// <summary>节点行集合（后端无关列：名称/身份/状态/启停；Add/Remove/模板导入后经 <see cref="RefreshFromHost"/> 重建）。</summary>
    public ObservableCollection<NodeListItemViewModel> Nodes { get; } = new();

    /// <summary>活动日志环形缓冲（上限 <see cref="ActivityCapacity"/>，满时移除最旧；spec §16.3 经 UI 线程 marshal 追加）。</summary>
    public ObservableCollection<NodeActivity> Activities { get; } = new();

    /// <summary>选中节点的编辑区（消息表 + 规则表；运行中节点只读）。</summary>
    public NodeEditorViewModel Editor { get; } = new();

    /// <summary>当前选中节点行（DataGrid SelectedItem 双向绑定）。</summary>
    [ObservableProperty]
    private NodeListItemViewModel? _selectedNode;

    public NodeSetupViewModel(
        NodeHostService host,
        NodeConfigLibrary library,
        DbcService dbcService,
        IFileDialogService fileDialogs)
    {
        _host = host;
        _library = library;
        _fileDialogs = fileDialogs;
        Editor.Bind(host, dbcService, _library);
        Editor.ConfigApplied += OnConfigApplied;
        _host.Activity += OnActivity;
        // 模板播种（Step 5 裁定：放 NodeSetupViewModel ctor，避免新 hosted service）——
        // 把随应用分发的 GB/T 27930 模板复制进用户库；目录不存在时静默跳过（测试临时目录路径）。
        _library.EnsureDefaultTemplates(Path.Combine(AppContext.BaseDirectory, "Templates", "Nodes"));
        RefreshFromHost();
    }

    /// <summary>从 NodeHostService 同步节点列表（Add/Remove/模板导入后调用）。</summary>
    public void RefreshFromHost()
    {
        Nodes.Clear();
        foreach (var node in _host.Nodes)
            Nodes.Add(new NodeListItemViewModel(_host, node));
        SelectedNode ??= Nodes.FirstOrDefault();
    }

    partial void OnSelectedNodeChanged(NodeListItemViewModel? value) => Editor.Select(value?.Config, value?.IsRunning ?? false);

    [RelayCommand]
    private void NewNode()
    {
        var name = $"node-{DateTime.Now:HHmmss}";
        var result = _host.AddNode(new NodeConfig { Name = name, Identity = new J1939NodeIdentity(0x11) });
        if (!result.IsSuccess)
            return;
        RefreshFromHost();
        SelectedNode = Nodes.First(n => n.Name == name);
    }

    [RelayCommand]
    private void StartAll() => _host.StartAll();

    [RelayCommand]
    private void StopAll() => _host.StopAll();

    [RelayCommand]
    private void Save()
    {
        var config = SelectedNode?.Config;
        if (config is null)
            return;
        var path = _fileDialogs.ShowSaveDialog("节点角色档案 (*.node.json)|*.node.json", "node", null);
        if (path is null)
            return;
        _library.Save(config with { Name = Path.GetFileNameWithoutExtension(path) });
    }

    [RelayCommand]
    private void Open()
    {
        var path = _fileDialogs.ShowOpenDialog("节点角色档案 (*.node.json)|*.node.json");
        if (path is null || !File.Exists(path))
            return;
        var json = File.ReadAllText(path);
        var file = System.Text.Json.JsonSerializer.Deserialize<InternalNodeConfigFile>(json, NodeConfigLibrary.JsonOpts);
        if (file?.Config is null)
            return;
        if (_host.AddNode(file.Config).IsSuccess)
            RefreshFromHost();
    }

    /// <summary>
    /// 删除选中节点（命令）。成功 → 刷新行集并显式收敛选中（列表空时为 null，否则首行——
    /// 不依赖 DataGrid 绑定写 null 兜底，VM 自行维持 "SelectedNode 指向 Nodes 内行" 的不变量，
    /// review MEDIUM）；失败（运行中 / 不存在）→ 经 Activity Error 呈现 host 的 Result 文案
    /// （Task 18 绑定注 4 同款契约：命令丢弃 Result，活动日志是唯一可见面，不得静默——
    /// 不预判文案，既消除双处硬编码漂移，也消灭静默失败分支，review 2×LOW）。
    /// SelectedNode 为 null（列表空）时 no-op。删除的是 memo：角色档案文件
    /// （<c>.node.json</c>）不受影响，需要时经 Open 重新导入。
    /// </summary>
    [RelayCommand]
    private void DeleteSelected()
    {
        var config = SelectedNode?.Config;
        if (config is null)
            return;

        var result = _host.RemoveNode(config.Name);
        if (result.IsSuccess)
        {
            RefreshFromHost();
            SelectedNode = Nodes.FirstOrDefault();   // 显式收敛（行集重建后为空或首行）
            return;
        }

        OnActivity(new NodeActivity(config.Name, NodeActivityKind.Error, result.Error?.Message ?? "删除失败", DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// 编辑提交生效后：刷新节点列表（改名/改 SA 后行文本同步）并重选新名行——
    /// 行 VM 与 SelectedNode 都持旧 record 引用（更新语义探索的引用陷阱），
    /// 必须经 RefreshFromHost + 重选收敛，不得依赖于启动器。
    /// </summary>
    private void OnConfigApplied(NodeConfig config)
    {
        RefreshFromHost();
        SelectedNode = Nodes.FirstOrDefault(n => n.Name == config.Name);
    }

    /// <summary>
    /// 后台线程回调 → marshal 到 UI 线程（spec §16.3）。走仓库统一 Dispatcher 汇点
    /// <see cref="DispatcherExtensions.RunOnUiPost"/>（fire-and-forget，语义与计划原稿
    /// <c>BeginInvoke</c> 一致）：计划原稿的三条路径（无 Application / 已在 UI 线程 → 直连；
    /// 否则 post）逐条保留，另覆盖 leaked STA Application 的死调度器回退——那是
    /// v0.2.0-hotfix-dispatcher-marshal 已修过的已知 flake 类别（BeginInvoke 进死调度器，
    /// 追加永不执行），不重新引入。
    /// </summary>
    private void OnActivity(NodeActivity activity)
        => new Action(() => AppendActivity(activity)).RunOnUiPost();

    private void AppendActivity(NodeActivity activity)
    {
        // 运行状态点随 Started/Stopped 刷新（事件驱动；决策 3 首选路径）。
        var row = Nodes.FirstOrDefault(n => n.Name == activity.NodeName);
        if (activity.Kind is NodeActivityKind.Started or NodeActivityKind.Stopped && row is not null)
        {
            row.IsRunning = activity.Kind == NodeActivityKind.Started;
            // 修订 12 的实时半边（评审修复）：翻转的是选中行时同步编辑器只读门——
            // EditorEnabled 不只在选择变更时计算，否则选中节点经行 ▶ 启停后门态失真。
            if (ReferenceEquals(row, SelectedNode))
                Editor.SetRunning(row.IsRunning);
        }

        Activities.Add(activity);
        while (Activities.Count > ActivityCapacity)
            Activities.RemoveAt(0);
    }
}

/// <summary>与 NodeConfigLibrary.NodeConfigFile 同构（internal record 跨类型不可见，重复声明）。</summary>
internal sealed record InternalNodeConfigFile(int Version, NodeConfig Config);
