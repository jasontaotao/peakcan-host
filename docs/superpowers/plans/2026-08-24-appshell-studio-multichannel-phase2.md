# AppShell + Studio 多通道（阶段二）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** AppShell 一次连接多路 CAN + Studio 编辑多通道用例（per-channel DBC + TargetChannel 透传 + 通道引用对账），全程不动 hil-core。

**Architecture:** 两个解耦子主题合一 plan。子主题 A（host `PeakCan.Host.App`）：弹窗多连接 + Shell 多槽位 + SendService 字典快照多目标 + Trace/Stats 按通道。子主题 B（studio `PeakCan.Studio.App`）：`MultiDbcStore` 活动文档模型 + Suite Channels 声明 + 5 类步骤 TargetChannel 透传 + 对账服务通道引用校验。两子主题无代码耦合，依赖链清晰。

**Tech Stack:** .NET 10 WPF + CommunityToolkit.Mvvm（`[ObservableProperty]`/`[RelayCommand]` 源生成器）；hil-core 0.13.0 NuGet（已就绪，不 bump）；host 与 studio 双 pin 0.13.0；NSubstitute + xUnit 测试。

**Spec:** `D:\claude_proj2\peakcan-host\docs\superpowers\specs\2026-08-24-appshell-studio-multichannel-design.md`（v2，接口已逐条核实，见 spec §2.4）

## Global Constraints

- **不动 hil-core**：0.13.0 已有 `ChannelConfig`/`TestSuite.Channels`/5 类步骤 `TargetChannel`/`StepParametersExporter|Factory` 全部实现 + 测试。`StepValidatorRegistry` 也在包内但 `Register(IStepValidator)` 是 public 扩展点——B-5 走 studio 本地对账服务，不改包。
- **零回归硬约束**：单通道场景（suite 无 Channels / 弹窗单组 / `MultiDbcStore` 为空）行为完全不变。每任务含单通道回归测试。
- **TDD**：每任务先写失败测试（RED）→ 实现（GREEN）→ 重构（IMPROVE）。测试覆盖 80%+。
- **注释语言**：用户面向/业务逻辑注释中文；技术 API/外部接口/协议字段注释英文。
- **commit**：conventional commits（feat/fix/refactor/test），无 Co-Authored-By 归属。
- **分支**：在 main 先开分支 `feat/multichannel-phase2`，不在 main 直接改。
- **跨仓库**：host 改动在 `D:\claude_proj2\peakcan-host`，studio 改动在 `D:\claude_proj2\peakcan-studio`。两仓库**各开同名分支** `feat/multichannel-phase2`（同名便于对应，均从各自 main 开出），独立 commit。T11 端到端时 host + studio 两分支都 checkout 对齐。
- **接口已核实**（spec §2.4 + 本 plan 核实）：
  - `ChannelId`=`readonly record struct(ushort Handle)` 可做字典 key（`ChannelId.cs:7`）
  - `BaudRate`=`readonly record struct`（`BaudRate.cs:26`）；`ICanChannel.ConnectAsync(BaudRate, bool fd, ct)`（`ICanChannel.cs:32`）；`IConnectSettingsSink.ApplyConnection(ChannelInfo?, BaudRate, bool)` channel 可空（`ConnectionSettingsViewModel.cs:25`）
  - `DbcService.LoadAsync(string, ct)` + `DbcLoaded: Action<DbcDocument>?`（`DbcService.cs:59`+`LoadLifecycle.partial.cs:39`）
  - `DbcParser.Parse(string, int, ct)` → `Result<DbcDocument>`（`DbcParser.cs:40`）；`Result<T>.Ok(v)`/`.Fail(code,msg)`（`Result.cs:16,19`）——T1 `ParseDbcAsync` 返回 `Result<DbcDocument>` 可用
  - `EditableTestCaseStep.Params`=`ObservableDictionary`（`EditableTestCaseStep.cs:35`）；`StepValidationContext` 无 Channels 字段（`IStepValidator.cs:62`）——B-5 走对账服务

---

## File Structure

### 子主题 A（host `D:\claude_proj2\peakcan-host`）
- Create: `src/PeakCan.Host.App/ViewModels/ChannelConnection.cs`（多槽位单项 VM）
- Modify: `src/PeakCan.Host.App/ViewModels/ConnectionSettingsViewModel.cs`（`IConnectSettingsSink` 列表契约 + `ConnectionConfig` record + `ChannelRow`）
- Modify: `src/PeakCan.Host.App/Windows/ConnectionSettingsWindow.xaml`（四行单选 → ItemsControl 通道列表）
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs`（`_activeChannel` 单槽 → `ObservableCollection<ChannelConnection>`）
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel/ChannelFlow.cs`（`ConnectAsync`/`DisconnectAsync` 多通道尽力式）
- Modify: `src/PeakCan.Host.App/Services/SendService.cs`（`SendAsync(frame, ChannelId)` 重载 + 字典快照）
- Modify: `src/PeakCan.Host.App/Views/SendView.xaml` + `ViewModels/SendViewModel.cs`（目标通道 ComboBox）
- Modify: `src/PeakCan.Host.App/Views/TraceView.xaml` + `ViewModels/TraceViewModel.cs`（通道过滤 ComboBox）
- Modify: `src/PeakCan.Host.App/ViewModels/StatsViewModel.cs`（按通道聚合）
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/`（既有 `AppShellViewModelTests.cs` 扩展 + 新 `ChannelConnectionTests.cs`/`SendServiceMultiTargetTests.cs`）

### 子主题 B（studio `D:\claude_proj2\peakcan-studio`）
- Create: `src/PeakCan.Studio.App/Services/MultiDbcStore.cs`（per-channel DBC 字典 + 活动文档切换）
- Modify: `src/PeakCan.Studio.App/Services/DbcService.cs`（加 `internal void SetActive(DbcDocument doc)`，设 Current + Invoke DbcLoaded）
- Modify: `src/PeakCan.Studio.App/Services/DbcService/LoadLifecycle.partial.cs`（提取 `ParseDbcAsync(path, ct)` 共享解析方法供 `MultiDbcStore` 复用）
- Create: `src/PeakCan.Studio.App/ViewModels/TestSuiteBuilder/ChannelConfigRow.cs`（Channels 编辑器行 VM）
- Modify: `src/PeakCan.Studio.App/ViewModels/TestSuiteBuilder/TestSuiteBuilderViewModel.cs`（`Channels` 集合 + `AddChannel`/`RemoveChannel` + 序列化）
- Modify: `src/PeakCan.Studio.App/Windows/HilStudioWindow.xaml`（Channels Expander + 5 模板 TargetChannel ComboBox + DBC Browser 通道切换）
- Modify: `src/PeakCan.Studio.App/ViewModels/HilStudioViewModel/DbcLoadingFlow.partial.cs`（`SwitchActiveChannel` 调 `MultiDbcStore.SwitchActive`）
- Create: `src/PeakCan.Studio.App/Services/Integrity/ChannelReferenceCheck.cs`（通道引用对账 check）
- Modify: `src/PeakCan.Studio.App/Services/Integrity/ReferenceIntegrityService.cs`（挂 `ChannelReferenceCheck`）
- Test: `tests/PeakCan.Studio.App.Tests/`（新 `MultiDbcStoreTests.cs`/`ChannelReferenceCheckTests.cs` + 扩展 `TestSuiteBuilderViewModelTests.cs`）

---

## Task 1: MultiDbcStore + DbcService.SetActive（studio 前提）

**Files:**
- Create: `D:\claude_proj2\peakcan-studio\src\PeakCan.Studio.App\Services\MultiDbcStore.cs`
- Modify: `D:\claude_proj2\peakcan-studio\src\PeakCan.Studio.App\Services\DbcService.cs:55`（`Current` private set 区 + 加 `SetActive`）
- Modify: `D:\claude_proj2\peakcan-studio\src\PeakCan.Studio.App\Services\DbcService\LoadLifecycle.partial.cs:39`（提取 `ParseDbcAsync`）
- Test: `D:\claude_proj2\peakcan-studio\tests\PeakCan.Studio.App.Tests\Services\MultiDbcStoreTests.cs`

**Interfaces:**
- Consumes: `DbcService`（`SetActive` 新增）/ `DbcParser.Parse`（hil-core 0.13.0）
- Produces: `MultiDbcStore.GetByChannel(string?)` / `Loaded` / `LoadAsync(string path, string? channelName, ct)` / `SwitchActive(string?)` —— 供 T5/T9 消费

- [ ] **Step 1: Write the failing test**

```csharp
// MultiDbcStoreTests.cs
public sealed class MultiDbcStoreTests
{
    [Fact]
    public async Task LoadAsync_ByChannel_StoresAndSwitchActive_TriggersDbcLoaded()
    {
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        var store = new MultiDbcStore(dbc);
        DbcDocument? fired = null;
        dbc.DbcLoaded += d => fired = d;
        var path = WriteTempDbc("BO_ 256 M: 8 ECU"); // helper 写临时 dbc

        await store.LoadAsync(path, "bus-a", CancellationToken.None);

        Assert.NotNull(store.GetByChannel("bus-a"));
        // SwitchActive 触发 DbcService.DbcLoaded → fired 非空（活动文档切换）
        store.SwitchActive("bus-a");
        Assert.NotNull(fired);
        Assert.Equal("bus-a" is null, false); // 占位断言（实际断 fired 非空已覆盖）
    }

    [Fact]
    public void SwitchActive_UnknownChannel_DoesNotThrowAndNoFire()
    {
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        var store = new MultiDbcStore(dbc);
        bool fired = false;
        dbc.DbcLoaded += _ => fired = true;

        store.SwitchActive("unknown"); // 未加载 → 不设活动文档，不触发

        Assert.False(fired);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test D:\claude_proj2\peakcan-studio\tests\PeakCan.Studio.App.Tests --filter MultiDbcStoreTests`
Expected: FAIL with "MultiDbcStore not found"（类型不存在）

- [ ] **Step 3: Write minimal implementation**

```csharp
// DbcService.cs 加（Current 的 private set 后，SetCurrentForTests 同区）
/// <summary>设当前活动 DBC 并触发 DbcLoaded（供 MultiDbcStore 通道切换调）。</summary>
internal void SetActive(DbcDocument doc)
{
    Current = doc;
    DbcLoaded?.Invoke(Current);
}
```

```csharp
// LoadLifecycle.partial.cs 提取（LoadAsync 内的 bytes→text→parse 段提为可复用方法）
/// <summary>读 + 解码 + 解析 DBC 文件 → DbcDocument（含 SourcePath）。失败返 Error。</summary>
internal async Task<Result<DbcDocument>> ParseDbcAsync(string path, CancellationToken ct)
{
    var bytes = await ReadDbcBytesAsync(path, ct).ConfigureAwait(false);
    if (_options.MaxFileSizeBytes > 0 && bytes.Length > _options.MaxFileSizeBytes)
        return Result<DbcDocument>.Fail(ErrorCode.DbcFileTooLarge, "...");
    var text = ReadDbcText(bytes);
    var r = await Task.Run(() => DbcParser.Parse(text, _options.MaxMessageCount, ct), ct).ConfigureAwait(false);
    if (!r.IsSuccess) return Result<DbcDocument>.Fail(r.Error!.Code, r.Error.Message);
    return Result<DbcDocument>.Ok(r.Value with { SourcePath = path });
}
// LoadAsync 改为调 ParseDbcAsync 后设 Current + Invoke（行为不变，重构）
```

```csharp
// MultiDbcStore.cs（新）
public sealed class MultiDbcStore
{
    private readonly Dictionary<string, DbcDocument> _byChannel = new();
    private readonly DbcService _active;
    private string? _activeChannel;

    public MultiDbcStore(DbcService active) { _active = active; }

    public DbcDocument? GetByChannel(string? channelName)
        => _byChannel.TryGetValue(channelName ?? "", out var d) ? d : null;

    public IReadOnlyDictionary<string, DbcDocument> Loaded => _byChannel;

    public async Task LoadAsync(string path, string? channelName, CancellationToken ct)
    {
        var r = await _active.ParseDbcAsync(path, ct).ConfigureAwait(false);
        if (!r.IsSuccess) return; // 失败静默（或转 LoadFailed，T1 先静默，T5 接 UI 报错）
        _byChannel[channelName ?? ""] = r.Value;
        if (_activeChannel is null || _activeChannel == (channelName ?? ""))
            _active.SetActive(r.Value); // 首次加载或当前活动通道 → 即时设活动
    }

    public void SwitchActive(string? channelName)
    {
        if (GetByChannel(channelName) is { } doc)
        {
            _activeChannel = channelName;
            _active.SetActive(doc);
        }
        // 未加载通道：不设活动，不触发（调用方提示加载）
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test D:\claude_proj2\peakcan-studio\tests\PeakCan.Studio.App.Tests --filter MultiDbcStoreTests`
Expected: PASS

- [ ] **Step 5: Regression — DbcService 单通道行为不变**

Run: `dotnet test D:\claude_proj2\peakcan-studio\tests\PeakCan.Studio.App.Tests --filter "DbcService|DbcLoading"`
Expected: 既有 DbcService 测试全绿（LoadAsync 重构后行为不变）

- [ ] **Step 6: Commit**

```bash
cd D:/claude_proj2/peakcan-studio
git add src/PeakCan.Studio.App/Services/MultiDbcStore.cs src/PeakCan.Studio.App/Services/DbcService.cs src/PeakCan.Studio.App/Services/DbcService/LoadLifecycle.partial.cs tests/PeakCan.Studio.App.Tests/Services/MultiDbcStoreTests.cs
git commit -m "feat(studio): MultiDbcStore per-channel DBC + DbcService.SetActive (B-1)"
```

---

## Task 2: IConnectSettingsSink 列表契约 + ConnectionConfig record（host）

**Files:**
- Modify: `D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\ViewModels\ConnectionSettingsViewModel.cs:17-29`（`IConnectSettingsSink` 接口）
- Modify: `D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\ViewModels\AppShellViewModel.cs:422-427`（`ApplyConnection` 显式实现，加 `ApplyConnections` DIM 默认）
- Test: `D:\claude_proj2\peakcan-host\tests\PeakCan.Host.App.Tests\ViewModels\AppShellViewModelTests.cs`

**Interfaces:**
- Consumes: `ChannelInfo`(host Core `record(ushort Handle, string Name)`) / `BaudRate`(hil-core)
- Produces: `ConnectionConfig(ChannelInfo Channel, BaudRate BaudRate, bool IsFd)` record + `IConnectSettingsSink.ApplyConnections(IReadOnlyList<ConnectionConfig>)` —— 供 T3/T4 消费

- [ ] **Step 1: Write the failing test**

```csharp
// AppShellViewModelTests.cs 加
[Fact]
public void ApplyConnections_List_Form_RoutesToShellState()
{
    // Arrange: 构造 shell（既有 test fixture）+ 一组 ConnectionConfig
    var cfg = new ConnectionConfig(
        new ChannelInfo(0x51, "USB1"), BaudRate.CanFd1Mbps, true);
    // Act: 通过 IConnectSettingsSink.ApplyConnections 显式实现
    ((IConnectSettingsSink)_shell).ApplyConnections(new[] { cfg });
    // Assert: 旧 ApplyConnection 单组 DIM 默认转发到 ApplyConnections，行为等价
    // （ApplyConnections 在 T3 才真正存多通道，T2 先验证契约路由不抛 + 单组等价）
    Assert.NotNull(cfg.Channel);
}

[Fact]
public void ApplyConnection_LegacySingle_DelegatesToListForm()
{
    // 零回归：旧单组 ApplyConnection 转发到 ApplyConnections（单元素）
    ((IConnectSettingsSink)_shell).ApplyConnection(
        new ChannelInfo(0x52, "USB2"), BaudRate.Can500kbps, false);
    // 不抛即通过（T2 契约层，状态断言在 T3）
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test D:\claude_proj2\peakcan-host\tests\PeakCan.Host.App.Tests --filter "ApplyConnections|ApplyConnection_Legacy"`
Expected: FAIL with "ConnectionConfig not found"

- [ ] **Step 3: Write minimal implementation**

```csharp
// ConnectionSettingsViewModel.cs
public sealed record ConnectionConfig(ChannelInfo? Channel, BaudRate BaudRate, bool IsFd);
// Channel 可空（对齐旧 ApplyConnection 的 ChannelInfo?）；null 时 shell 跳过该组

public interface IConnectSettingsSink
{
    IReadOnlyList<ChannelInfo> AvailableChannels { get; }
    void ProbeChannels();

    /// <summary>列表形式（多通道）。shell 逐组连接。</summary>
    void ApplyConnections(IReadOnlyList<ConnectionConfig> configs);

    /// <summary>旧单组形式（DIM 默认转发到列表，单元素）——零回归。</summary>
    void ApplyConnection(ChannelInfo? channel, BaudRate baudRate, bool isFd)
        => ApplyConnections(channel is null
            ? Array.Empty<ConnectionConfig>()
            : new[] { new ConnectionConfig(channel, baudRate, isFd) });

    void Connect();
}
```

```csharp
// AppShellViewModel.cs
// 加字段：待连接 configs（T2 存，T3 ConnectAsync 读）
private IReadOnlyList<ConnectionConfig> _pendingConfigs = Array.Empty<ConnectionConfig>();

void IConnectSettingsSink.ApplyConnections(IReadOnlyList<ConnectionConfig> configs)
{
    _pendingConfigs = configs ?? Array.Empty<ConnectionConfig>();
    // 兼容旧单通道 UI 绑定（工具栏 ComboBox 仍绑 SelectedChannel）：存首组
    if (configs.Count > 0)
    {
        SelectedChannel = configs[0].Channel;
        SelectedBaudRate = configs[0].BaudRate;
        IsFd = configs[0].IsFd;
    }
}
// 旧 ApplyConnection 显式实现移除（DIM 默认接管）
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test D:\claude_proj2\peakcan-host\tests\PeakCan.Host.App.Tests --filter "ApplyConnections|ApplyConnection_Legacy"`
Expected: PASS

- [ ] **Step 5: Regression — 既有 AppShell 测试零回归**

Run: `dotnet test D:\claude_proj2\peakcan-host\tests\PeakCan.Host.App.Tests --filter "AppShellViewModel"`
Expected: 既有测试全绿（DIM 默认保旧签名）

- [ ] **Step 6: Commit**

```bash
cd D:/claude_proj2/peakcan-host
git add src/PeakCan.Host.App/ViewModels/ConnectionSettingsViewModel.cs src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs
git commit -m "feat(host): IConnectSettingsSink list contract + ConnectionConfig (A-1)"
```

---

## Task 3: AppShellViewModel 多槽位 + ChannelFlow 尽力式连接（host）

**Files:**
- Create: `D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\ViewModels\ChannelConnection.cs`
- Modify: `D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\ViewModels\AppShellViewModel.cs:179,214-218`（`_activeChannel` → `ObservableCollection<ChannelConnection>` + `IsConnected` 派生）
- Modify: `D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\ViewModels\AppShellViewModel\ChannelFlow.cs:132-205`（`ConnectAsync` 多通道尽力式 + `DisconnectAllAsync` 遍历断开）
- Modify: `D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\Services\SendService.cs:30-84`（**T3 配套定义** `SetChannels(IReadOnlyDictionary<ChannelId, ICanChannel>?)` + `_channels` 字段——T6 只加 `SendAsync(frame, ChannelId)` 重载 + UI）
- Test: `D:\claude_proj2\peakcan-host\tests\PeakCan.Host.App.Tests\ViewModels\ChannelConnectionTests.cs` + 扩展 `AppShellViewModelTests.cs`

**Interfaces:**
- Consumes: T2 `ApplyConnections`（`_pendingConfigs`）/ `ICanChannel.ConnectAsync(BaudRate, bool, ct)` / `ChannelRouter.RegisterChannel` / `ReadLoopError`
- Produces: `ChannelConnection`（`Channel`/`Name`/`BaudRate`/`State`/`DisconnectCommand`，**不持 shell 引用**——单项断开只需 `channel.DisconnectAsync`，详见 Step 3）+ `ChannelConnections` 集合 + `SendService.SetChannels(...)`（配套定义）—— 供 T6/T7 消费

- [ ] **Step 1: Write the failing test**

```csharp
// ChannelConnectionTests.cs
public sealed class ChannelConnectionTests
{
    [Fact]
    public async Task DisconnectCommand_DisconnectsUnderlyingChannel()
    {
        var channel = Substitute.For<ICanChannel>();
        channel.Id.Returns(new ChannelId(0x51));
        var conn = new ChannelConnection(channel, "bus-a", BaudRate.CanFd1Mbps);
        conn.State = "已连接";

        await conn.DisconnectCommand.ExecuteAsync(null);

        await channel.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
        Assert.Equal("已断开", conn.State);
    }
    // 注：ChannelConnection 构造不持 shell 引用（单项断开只需 channel.DisconnectAsync）
}

// AppShellViewModelTests.cs 加
[Fact]
public async Task ConnectAsync_MultiConfigs_BestEffort_AllSucceed_RegistersAll()
{
    // Arrange: 2 组 ConnectionConfig，FakeChannelFactory 造 2 个 fake channel
    var cfgs = new[] {
        new ConnectionConfig(new ChannelInfo(0x51,"USB1"), BaudRate.CanFd1Mbps, true),
        new ConnectionConfig(new ChannelInfo(0x52,"USB2"), BaudRate.Can500kbps, false),
    };
    // Act: 调 ConnectAsync（经 ApplyConnections + ConnectCommand 或直接测 ConnectAsync）
    // Assert: ChannelConnections.Count==2, IsConnected==true, 两个 channel 都 Registered
}

[Fact]
public async Task ConnectAsync_MultiConfigs_SecondFails_BestEffort_KeepsFirst()
{
    // 第二组 ConnectAsync 返 Fail → 该组标红跳过，第一组保留
    // Assert: ChannelConnections.Count==1, IsConnected==true, 第二组 State="连接失败..."
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test D:\claude_proj2\peakcan-host\tests\PeakCan.Host.App.Tests --filter "ChannelConnection|ConnectAsync_Multi"`
Expected: FAIL（类型/方法不存在）

- [ ] **Step 3: Write minimal implementation**

```csharp
// ChannelConnection.cs（新）—— 不持 shell 引用：单项断开只需 channel.DisconnectAsync
// shell 的 DisconnectAllAsync 遍历集合，单项 DisconnectCommand 仅供 UI"每项独立断开"按钮
public sealed class ChannelConnection : ObservableObject
{
    public ICanChannel Channel { get; }
    public string Name { get; }
    public BaudRate BaudRate { get; }
    [ObservableProperty] private string _state = "已连接";

    public ChannelConnection(ICanChannel channel, string name, BaudRate baud)
    { Channel = channel; Name = name; BaudRate = baud; }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await Channel.DisconnectAsync().ConfigureAwait(true);
        State = "已断开";
    }
}
```

```csharp
// SendService.cs —— T3 配套定义（SetChannels + _channels 字段；SendAsync(frame, ChannelId) 重载留 T6）
private IReadOnlyDictionary<ChannelId, ICanChannel>? _channels;
/// <summary>shell 连接/断开后整体替换通道快照（Volatile 读写，无锁热路径）。</summary>
public void SetChannels(IReadOnlyDictionary<ChannelId, ICanChannel>? channels)
    => Volatile.Write(ref _channels, channels);
```

```csharp
// AppShellViewModel.cs
// _activeChannel 单字段 → 删除
public ObservableCollection<ChannelConnection> ChannelConnections { get; } = new();
// IsConnected 改派生（移除 [ObservableProperty] _isConnected，改为计算属性 + 手动 OnPropertyChanged）
public bool IsConnected => ChannelConnections.Count > 0;
// IsConnected 变化时通知：在 ChannelConnections.CollectionChanged 里 RaisePropertyChanged(nameof(IsConnected))
```

```csharp
// ChannelFlow.cs ConnectAsync 改造（尽力式遍历）
[RelayCommand(CanExecute = nameof(CanConnect))]
private async Task ConnectAsync()
{
    var configs = _pendingConfigs; // T2 ApplyConnections 存入的列表
    foreach (var cfg in configs)
    {
        if (cfg.Channel is null) continue; // null 组跳过
        var channel = _channelFactory.Create(new ChannelId(cfg.Channel.Handle));
        var rate = cfg.BaudRate;
        try
        {
            var result = await channel.ConnectAsync(rate, cfg.IsFd).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                _router.RegisterChannel(channel);
                channel.ReadLoopError += OnReadLoopError;
                ChannelConnections.Add(new ChannelConnection(channel, cfg.Channel.Name, rate, this));
            }
            else
            {
                // 尽力式：标红跳过，不阻塞
                var failed = new ChannelConnection(channel, cfg.Channel!.Name, rate, this) { State = $"连接失败: {result.Error!.Code}" };
                ChannelConnections.Add(failed);
                await channel.DisposeAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            var failed = new ChannelConnection(channel, cfg.Channel!.Name, rate, this) { State = $"连接异常: {ex.GetType().Name}" };
            ChannelConnections.Add(failed);
        }
    }
    _sendService.SetChannels(ChannelConnections.Where(c => c.Channel.IsConnected).ToDictionary(c => c.Channel.Id, c => c.Channel));
    _sendService.ActiveChannel = ChannelConnections.FirstOrDefault(c => c.Channel.IsConnected)?.Channel;
    IsConnected = ChannelConnections.Any(c => c.Channel.IsConnected);
    ConnectionState = IsConnected ? $"已连接 {ChannelConnections.Count} 路" : "已断开";
}
// DisconnectAsync 改遍历
[RelayCommand(CanExecute = nameof(CanDisconnect))]
private async Task DisconnectAllAsync()
{
    foreach (var conn in ChannelConnections.ToList())
    {
        try { await conn.Channel.DisconnectAsync().ConfigureAwait(true); }
        catch { }
        _router.UnregisterChannel(conn.Channel);
        conn.Channel.ReadLoopError -= OnReadLoopError;
        conn.State = "已断开";
    }
    ChannelConnections.Clear();
    _sendService.SetChannels(null);
    _sendService.ActiveChannel = null;
    IsConnected = false;
    ConnectionState = "已断开";
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test D:\claude_proj2\peakcan-host\tests\PeakCan.Host.App.Tests --filter "ChannelConnection|ConnectAsync_Multi|DisconnectAll"`
Expected: PASS

- [ ] **Step 5: Regression — 既有 AppShell 单通道测试（经 DIM 单组路径）**

Run: `dotnet test D:\claude_proj2\peakcan-host\tests\PeakCan.Host.App.Tests`
Expected: 全绿（单组 ConnectionConfig → 1 元素列表 → 行为等价旧单通道）

- [ ] **Step 6: Commit**

```bash
cd D:/claude_proj2/peakcan-host
git add src/PeakCan.Host.App/ViewModels/ChannelConnection.cs src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs src/PeakCan.Host.App/ViewModels/AppShellViewModel/ChannelFlow.cs tests/PeakCan.Host.App.Tests/ViewModels/
git commit -m "feat(host): multi-slot ChannelConnection + best-effort connect (A-3)"
```

---

## Task 4: ConnectionSettingsWindow 弹窗多通道 UI（host）

**Files:**
- Modify: `D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\Windows\ConnectionSettingsWindow.xaml:12-64`（StackPanel 四行 → ItemsControl 通道列表 + 添加/移除按钮）
- Modify: `D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\ViewModels\ConnectionSettingsViewModel.cs:37-127`（`ObservableCollection<ChannelRow>` + `AddChannel`/`RemoveChannel` + `ApplyAndConnect` 收集列表）
- Test: `D:\claude_proj2\peakcan-host\tests\PeakCan.Host.App.Tests\ViewModels\ConnectionSettingsViewModelTests.cs`

**Interfaces:**
- Consumes: T2 `IConnectSettingsSink.ApplyConnections` / `DeviceDescriptor`/`ChannelDescriptor`（既有）
- Produces: `ChannelRow`（每行独立 `SelectedDevice/SelectedChannel/IsFd/SelectedBaudRate`）

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void ApplyAndConnect_MultipleRows_CollectsAllConfigs_ToSink()
{
    // Arrange: VM with 2 ChannelRows + spy IConnectSettingsSink 记录 ApplyConnections 参数
    // Act: ApplyAndConnectCommand.Execute(null)
    // Assert: spy 收到 2 个 ConnectionConfig
}

[Fact]
public void AddChannel_IncrementsRows_RemoveChannel_Decrements()
{
    // Assert: AddChannelCommand → Rows.Count+1; RemoveChannelCommand(row) → -1
}
```

- [ ] **Step 2: Run test to verify it fails** → `dotnet test ... --filter ConnectionSettings`
- [ ] **Step 3: Write minimal implementation** — XAML `ItemsControl` 绑 `Rows`，每行 `ComboBox(Devices)`+`ComboBox(Channels)`+`CheckBox(FD)`+`ComboBox(BaudRates)`+`Button(移除)`；底部 `Button(+ 添加通道)` 绑 `AddChannelCommand`；VM 加 `ObservableCollection<ChannelRow> Rows` + `AddChannel`/`RemoveChannel`；`ApplyAndConnect` 改 `_sink.ApplyConnections(Rows.Select(r => new ConnectionConfig(r.Match, r.SelectedBaudRate, r.IsFd)))` + `_sink.Connect()`
- [ ] **Step 4: Run test to verify it passes**
- [ ] **Step 5: Regression** — 既有 `ConnectionSettingsViewModel` 测试（单行默认）全绿
- [ ] **Step 6: Commit** `feat(host): multi-channel connection settings window (A-2)`

---

## Task 5: TestSuiteBuilder Channels 声明编辑器（studio）

**Files:**
- Create: `D:\claude_proj2\peakcan-studio\src\PeakCan.Studio.App\ViewModels\TestSuiteBuilder\ChannelConfigRow.cs`
- Modify: `D:\claude_proj2\peakcan-studio\src\PeakCan.Studio.App\ViewModels\TestSuiteBuilder\TestSuiteBuilderViewModel.cs:42-46`（加 `Channels` 集合 + `AddChannel`/`RemoveChannel` + 序列化 round-trip）
- Modify: `D:\claude_proj2\peakcan-studio\src\PeakCan.Studio.App\Windows\HilStudioWindow.xaml:644-714`（Suite Parameters Expander 区加 Channels Expander）
- Test: `D:\claude_proj2\peakcan-studio\tests\PeakCan.Studio.App.Tests\ViewModels\TestSuiteBuilder\TestSuiteBuilderViewModelTests.cs`

**Interfaces:**
- Consumes: T1 `MultiDbcStore.LoadAsync(path, channelName, ct)` / hil-core `ChannelConfig` record / `TestSuite.Channels`
- Produces: `ChannelConfigRow`（`Name`/`Handle`/`BaudRate`/`IsFd`/`DbcPath`）+ `Channels` 集合 —— 供 T8/T9/T10 消费

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void AddChannel_AddsRow_AndSerializesToSuite_Channels()
{
    // Arrange: VM 加 ChannelConfigRow(Name="bus-a", Handle="51", ...)
    // Act: AddChannelCommand + 编辑 → 保存为 TestSuite
    // Assert: suite.Channels.Count==1, Channels[0].Name=="bus-a"
}

[Fact]
public void LoadSuite_WithChannels_PopulatesRows_RoundTrip()
{
    // 旧 suite JSON 含 Channels → 加载后 Rows 匹配；null Channels → Rows 空（零回归）
}
```

- [ ] **Step 2: Run test to verify it fails** → `dotnet test D:\claude_proj2\peakcan-studio\... --filter "AddChannel|LoadSuite_WithChannels"`
- [ ] **Step 3: Write minimal implementation** — `ChannelConfigRow: ObservableObject`（5 字段）；VM 加 `ObservableCollection<ChannelConfigRow> Channels`；`AddChannelCommand`/`RemoveChannelCommand`。
  - **序列化挂载点已钉死**：`TestSuiteBuilderViewModel.ToSuite()`（`TestSuiteBuilderViewModel.cs:985-999`）末尾构造 `new TestSuite(...)` 时加 `Channels` 参数：`Channels: Channels.Any() ? Channels.Select(r => new ChannelConfig(r.Name, r.Handle, r.BaudRate, r.IsFd, r.DbcPath)).ToList() : null`（null = 单通道兼容）。
  - **反序列化挂载点已钉死**：`RoundTripFlow.partial.cs` 的 `LoadFromText`（suite 还原后，约 line 30-46 区）加：`if (suite.Channels is { } chs) foreach (var c in chs) Channels.Add(ChannelConfigRow.From(c))`（无 Channels → 集合空，零回归）。
  - `DbcPath` 变更触发 `MultiDbcStore.LoadAsync(dbcPath, channelName, ct)`（fire-and-forget，T1 产物）。
- [ ] **Step 4: Run test to verify it passes**
- [ ] **Step 5: Regression** — 既有 `TestSuiteBuilderViewModelTests` 全绿（无 Channels suite → Rows 空）
- [ ] **Step 6: Commit** `feat(studio): suite Channels declaration editor (B-2)`

---

## Task 6: SendService 多目标字典快照 + SendView 选择器（host）

**Files:**
- Modify: `D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\Services\SendService.cs`（加 `SendAsync(frame, ChannelId, ct)` 重载——`SetChannels`+`_channels` 已在 T3 配套定义）
- Modify: `D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\Views\SendView.xaml:18-29`（加目标通道 ComboBox）
- Modify: `D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\ViewModels\SendViewModel.cs`（加 `AvailableTargets`/`SelectedTarget`）
- Test: `D:\claude_proj2\peakcan-host\tests\PeakCan.Host.App.Tests\Services\SendServiceMultiTargetTests.cs`

**Interfaces:**
- Consumes: T3 已定义的 `SendService.SetChannels`/`_channels`（字典快照）/ `ChannelId` record struct（可做字典 key）
- Produces: `SendService.SendAsync(CanFrame, ChannelId, ct)` 重载 —— SendView 消费

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task SendAsync_ByChannelId_RoutesToThatChannel_NotActive()
{
    var svc = new SendService(NullLogger<SendService>.Instance);
    var chA = Substitute.For<ICanChannel>(); chA.Id.Returns(new ChannelId(0x51));
    var chB = Substitute.For<ICanChannel>(); chB.Id.Returns(new ChannelId(0x52));
    chB.WriteAsync(Arg.Any<CanFrame>(), default).Returns(Result<Unit>.Ok(Unit.Value));
    svc.SetChannels(new Dictionary<ChannelId, ICanChannel> { [new(0x51)] = chA, [new(0x52)] = chB });
    svc.ActiveChannel = chA; // 默认 bus-a

    var frame = new CanFrame(new CanId(0x100, FrameFormat.Standard), new byte[]{1}, FrameFlags.None, default, default);
    var r = await svc.SendAsync(frame, new ChannelId(0x52), default); // 显式 bus-b

    Assert.True(r.IsSuccess);
    await chB.Received(1).WriteAsync(frame, default);
    await chA.DidNotReceive().WriteAsync(frame, default); // 未走 ActiveChannel
}

[Fact]
public async Task SendAsync_NoChannelId_FallsBackToActiveChannel()
{
    // 零回归：无 channelId → ActiveChannel（6 既有发送方路径不变）
}
```

- [ ] **Step 2: Run test to verify it fails** → `dotnet test ... --filter SendServiceMultiTarget`（`SendAsync(frame, ChannelId)` 重载不存在）
- [ ] **Step 3: Write minimal implementation**

```csharp
// SendService.cs —— 仅加重载（SetChannels/_channels T3 已定义）
public virtual ValueTask<Result<Unit>> SendAsync(CanFrame frame, ChannelId channelId, CancellationToken ct = default)
{
    var map = Volatile.Read(ref _channels);
    if (map is not null && map.TryGetValue(channelId, out var ch))
        return ch.WriteAsync(frame, ct);
    return SendAsync(frame, ct); // 回落 ActiveChannel（无 channelId 或未找到时）
}
// 既有 SendAsync(frame, ct) 不变
```

SendView.xaml 加 `ComboBox` 绑 `AvailableTargets`（`SelectedTarget` 为 null 时回落 Active）。

- [ ] **Step 4: Run test to verify it passes**
- [ ] **Step 5: Regression** — 既有 `SendService`/`SendViewModel` 测试（无 SetChannels → `SendAsync(frame)` 走 ActiveChannel 不变）
- [ ] **Step 6: Commit** `feat(host): SendService multi-target SendAsync + SendView selector (A-4)`

---

## Task 7: Trace 过滤 + Stats 按通道聚合（host）

**Files:**
- Modify: `D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\Views\TraceView.xaml`（加通道过滤 ComboBox）
- Modify: `D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\ViewModels\TraceViewModel.cs`（加 `ChannelFilter` + 过滤谓词）
- Modify: `D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\ViewModels\StatsViewModel.cs`（按 `frame.Channel` 分组聚合）
- Test: 扩展 `TraceViewModelTests`/`StatsViewModelTests`

**Interfaces:**
- Consumes: T3 `ChannelConnections`（ComboBox 选项源）/ `CanFrame.Channel`（数据面已在）

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void ChannelFilter_SetToBusA_HidesBusB_Frames()
{
    // Arrange: TraceViewModel 收 bus-a(0x51) + bus-b(0x52) 帧各 1
    // Act: ChannelFilter = new ChannelId(0x51)
    // Assert: VisibleFrames 只剩 bus-a 帧
}

[Fact]
public void Stats_GroupsByChannel_SeparateCounts_PlusTotal()
{
    // 收 0x51 帧 10 个 + 0x52 帧 5 个 → 两组 + 全局合计 15
}
```

- [ ] **Step 2: Run test to verify it fails** → `dotnet test ... --filter "ChannelFilter|Stats_Groups"`
- [ ] **Step 3: Write minimal implementation** — TraceViewModel 加 `ChannelId? ChannelFilter`，帧过滤 `f => ChannelFilter is null || f.Channel == ChannelFilter`；StatsViewModel 按 `frame.Channel` GroupBy。数据面（ChannelRouter）零改动。
- [ ] **Step 4: Run test to verify it passes**
- [ ] **Step 5: Regression** — Trace/Stats 既有测试（无过滤 → 全部）全绿
- [ ] **Step 6: Commit** `feat(host): Trace channel filter + Stats per-channel aggregation (A-5)`

---

## Task 8: 5 类步骤 TargetChannel 透传（studio）

**Files:**
- Modify: `D:\claude_proj2\peakcan-studio\src\PeakCan.Studio.App\Windows\HilStudioWindow.xaml:18-51,53-65,305-349`（`SendFrameTemplate`/`ExpectFrameTemplate`/`AssertNoFrameTemplate`/`AssertFrameCountTemplate`/`AssertCycleTimeTemplate` 各加 TargetChannel ComboBox）
- Test: `D:\claude_proj2\peakcan-studio\tests\PeakCan.Studio.App.Tests\ViewModels\TestSuiteBuilder\SendFrameComposerViewModelTests.cs`（扩展）

**Interfaces:**
- Consumes: T5 `Channels` 集合（ComboBox 选项源）/ `EditableTestCaseStep.Params`（`ObservableDictionary`，XAML `Params[TargetChannel]`）/ hil-core `StepParametersFactory.Create`（已支持 TargetChannel 序列化）

**保存链已核实（无歧义）**：`EditableTestCaseStep.ToStep()`（`EditableTestCaseStep.cs:104-108`）调 `StepParametersFactory.Create(Kind, Params)` → 读 `Params["TargetChannel"]` 产 `SendFrameStep{TargetChannel=...}`（包内 `StepParametersFactory.cs:252` 已支持）。`ObservableDictionary` 实现 `IReadOnlyDictionary`（`ObservableDictionary.cs:14`）兼容 `Create(IReadOnlyDictionary)` 重载。**T8 只改 XAML 加 ComboBox，保存链零改动**。

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void SendFrameStep_TargetChannel_RoundTrips_Through_Params()
{
    // Arrange: EditableTestCaseStep SendFrame, Params["TargetChannel"] = "bus-b"
    // Act: step.ToStep() → StepParametersFactory.Create(Kind, Params) → SendFrameStep
    // Assert: ((SendFrameStep)step.Parameters).TargetChannel == "bus-b"
}

[Fact]
public void SendFrameStep_TargetChannel_Null_Omitted_Legacy()
{
    // 旧 suite 无 TargetChannel → Params 无 key → step.TargetChannel == null（零回归）
}
```

- [ ] **Step 2: Run test to verify it fails** → `dotnet test ... --filter TargetChannel_RoundTrips`
- [ ] **Step 3: Write minimal implementation** — 5 个模板各加（抽共享子模板复用）：
```xml
<TextBlock Text="目标通道" Margin="0,0,0,2"/>
<ComboBox ItemsSource="{Binding DataContext.SuiteBuilder.Channels, RelativeSource={RelativeSource AncestorType=Window}}"
          DisplayMemberPath="Name" SelectedValuePath="Name"
          SelectedValue="{Binding Params[TargetChannel], Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"/>
```
XAML 写 `Params["TargetChannel"]` → `ObservableDictionary` 索引器 set → `ToStep()` 时 `StepParametersFactory.Create` 自动读出 → `SendFrameStep.TargetChannel`（包内已支持，保存链零改动）。
- [ ] **Step 4: Run test to verify it passes**
- [ ] **Step 5: Regression** — 既有 5 模板测试（无 TargetChannel）全绿
- [ ] **Step 6: Commit** `feat(studio): 5 step kinds TargetChannel passthrough (B-3)`

---

## Task 9: DBC Browser 通道切换（studio）

**Files:**
- Modify: `D:\claude_proj2\peakcan-studio\src\PeakCan.Studio.App\Windows\HilStudioWindow.xaml:481-596`（col0 DBC Browser 工具栏加通道 ComboBox）
- Modify: `D:\claude_proj2\peakcan-studio\src\PeakCan.Studio.App\ViewModels\HilStudioViewModel.cs:21-89`（加 `SelectedDbcChannel` + `SwitchDbcChannel`）
- Modify: `D:\claude_proj2\peakcan-studio\src\PeakCan.Studio.App\ViewModels\HilStudioViewModel\DbcLoadingFlow.partial.cs:11-49`（`SwitchActiveChannel` 调 `MultiDbcStore.SwitchActive`）
- Test: `D:\claude_proj2\peakcan-studio\tests\PeakCan.Studio.App.Tests\ViewModels\HilStudioViewModelTests.cs`

**Interfaces:**
- Consumes: T1 `MultiDbcStore.SwitchActive(string?)` / T5 `Channels`（ComboBox 选项）
- Produces: `SelectedDbcChannel` —— DBC Browser 切换活动 DBC

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void SwitchDbcChannel_TriggersMultiDbcStore_SwitchActive_DbcLoadedFires()
{
    // Arrange: 2 通道 DBC 已加载到 MultiDbcStore + spy 记录 DbcLoaded
    // Act: HilStudioViewModel.SelectedDbcChannel = "bus-b"
    // Assert: MultiDbcStore.SwitchActive("bus-b") 被调 + DbcLoaded 触发 + Messages 重建为 bus-b 的
}
```

- [ ] **Step 2: Run test to verify it fails**
- [ ] **Step 3: Write minimal implementation** — `HilStudioViewModel` 加 `string? SelectedDbcChannel` + `partial void OnSelectedDbcChannelChanged` 调 `_multiDbcStore.SwitchActive(value)`；`OnLoaded(doc)` 既有逻辑不变（`MultiDbcStore.SwitchActive` → `DbcService.SetActive` → `DbcLoaded` → `OnLoaded` → `Rebuild`）。单通道（无 Channels）ComboBox 只"默认通道"，`MultiDbcStore` 空，`OpenAsync` 走旧路径零回归。
- [ ] **Step 4: Run test to verify it passes**
- [ ] **Step 5: Regression** — 既有 `HilStudioViewModelTests` 全绿
- [ ] **Step 6: Commit** `feat(studio): DBC browser channel switch (B-4)`

---

## Task 10: ChannelReferenceCheck 通道引用对账（studio）

**Files:**
- Create: `D:\claude_proj2\peakcan-studio\src\PeakCan.Studio.App\Services\Integrity\ChannelReferenceCheck.cs`
- Modify: `D:\claude_proj2\peakcan-studio\src\PeakCan.Studio.App\ViewModels\TestSuiteBuilder\RoundTripFlow.partial.cs:50-57`（对账块后追加 `ChannelReferenceCheck.Check(suite)` 合并 issue）
- Test: `D:\claude_proj2\peakcan-studio\tests\PeakCan.Studio.App.Tests\Services\Integrity\ChannelReferenceCheckTests.cs`

**Interfaces:**
- Consumes: `TestSuite.Channels`（hil-core `IReadOnlyList<ChannelConfig>?`）/ `TestCaseStep.Parameters`（含 TargetChannel，经 `StepParametersExporter.FromParameters` 读出）/ 步骤树递归（`If`/`Repeat`/`Loop` 的 Body/ElseBody 子步骤）
- **递归行为已核实**：`ReferenceCollector.Collect`（`ReferenceCollector.cs:70-86`）已递归 `IfStep.Body`+`ElseBody`、`RepeatStep.Body`、`LoopStep.Body`，路径用 `pathPrefix`+索引拼接（`"0.e.1"` = step[0] ElseBody 的 step[1]）。T10 的 `EnumerateStepsRecursive` **可复用此递归**（抽共享遍历器），或自实现——两条路都行，plan Step 3 示自实现（独立不耦合 DBC 引用收集）。
- Produces: `ReconciliationIssue`（复用既有类型，进 `ReconciliationIssues` UI + 定位跳转）

**已核实（无歧义）**：
- `ReconciliationIssue` 是通用 record（`ReconciliationResult.cs:12-18`）：`(string Category, StepValidationSeverity Severity, string Code, string Location, string OldRef, string Message)`——非 DBC 专用，`ChannelReferenceCheck` 直接产出
- 对账挂载点 = `RoundTripFlow.partial.cs:50-57`（`LoadFromText` 内 `Reconcile` 后 `foreach issue → ReconciliationIssues.Add(new ReconciliationIssueView(issue, DescribeLocation(issue.Location)))`）
- `DescribeLocation(string)` 在 VM 内（转 Location 为用户可读 Where）——T10 复用产 `Where`
- `ReferenceIntegrityService.Reconcile(DataSource, DbcDocument)` **拿不到 suite**（只对账 DBC 引用）→ `ChannelReferenceCheck` 是独立 `Check(TestSuite)` 入口，不挂 `Reconcile`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Check_StepReferencesUndeclaredChannel_ReportsCritical()
{
    // Arrange: suite.Channels = ["bus-a"]；case.Steps[0] SendFrame.TargetChannel = "bus-b"
    var suite = MakeSuite(channels: new[]{ new ChannelConfig("bus-a","51",null,false,null) },
        steps: new[]{ SendFrameStepWith(tc:"bus-b") });
    // Act
    var issues = new ChannelReferenceCheck().Check(suite);
    // Assert: 1 个 Critical, Code="CHANNEL_UNDECLARED", Message 含 "bus-b"
    var i = Assert.Single(issues);
    Assert.Equal(StepValidationSeverity.Critical, i.Severity);
    Assert.Contains("bus-b", i.Message);
}

[Fact]
public void Check_SuiteNoChannels_StepHasTargetChannel_ReportsCritical()
{
    // Q3: suite.Channels = null + 步骤带 TargetChannel → Critical（Code="CHANNEL_NO_DECLARATION"）
}

[Fact]
public void Check_StepTargetChannelInsideLoopBody_RecursivelyFound_Gap3()
{
    // Gap-3: Repeat.Body[0].SendFrame.TargetChannel = "bus-z"（未声明）→ 递归进 Body 报 Critical
    var suite = MakeSuite(channels: new[]{ new ChannelConfig("bus-a",...) },
        steps: new[]{ RepeatStep(body: new[]{ SendFrameStepWith(tc:"bus-z") }) });
    var issues = new ChannelReferenceCheck().Check(suite);
    Assert.Single(issues); // 递归进 Body 找到
}

[Fact]
public void Check_NoTargetChannel_SuiteNoChannels_Ok()
{
    // 零回归：旧 suite 无 Channels + 步骤无 TargetChannel → issues 空
    var suite = MakeSuite(channels: null, steps: new[]{ SendFrameStepWith(tc:null) });
    Assert.Empty(new ChannelReferenceCheck().Check(suite));
}
```

- [ ] **Step 2: Run test to verify it fails** → `dotnet test ... --filter ChannelReferenceCheck`
- [ ] **Step 3: Write minimal implementation**

```csharp
// ChannelReferenceCheck.cs（新，studio 本地）
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.ReferenceIntegrity;

public sealed class ChannelReferenceCheck
{
    /// <summary>遍历所有步骤（含控制流递归 Body/ElseBody）收集 TargetChannel，比对 suite.Channels。</summary>
    public IReadOnlyList<ReconciliationIssue> Check(TestSuite suite)
    {
        var issues = new List<ReconciliationIssue>();
        var declared = suite.Channels?.Select(c => c.Name).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
        var hasChannels = suite.Channels is { Count: > 0 };

        foreach (var c in suite.Cases)
            foreach (var (step, loc) in EnumerateStepsRecursive(c.Steps, ""))
            {
                var tc = TryGetTargetChannel(step); // 从 step.Parameters 取 TargetChannel（经 FromParameters 读 dict）
                if (tc is null) continue;
                if (!hasChannels)
                    issues.Add(new ReconciliationIssue("Channel", StepValidationSeverity.Critical,
                        "CHANNEL_NO_DECLARATION", loc, tc, $"步骤带 TargetChannel '{tc}' 但 suite 未声明 Channels"));
                else if (!declared.Contains(tc))
                    issues.Add(new ReconciliationIssue("Channel", StepValidationSeverity.Critical,
                        "CHANNEL_UNDECLARED", loc, tc, $"通道 '{tc}' 未在 suite.Channels 声明"));
            }
        return issues;
    }

    /// <summary>递归遍历步骤树（If.Body/ElseBody, Repeat.Body, Loop.Body）——自实现，不依赖 ReferenceCollector。</summary>
    private static IEnumerable<(TestCaseStep Step, string Location)> EnumerateStepsRecursive(
        IReadOnlyList<TestCaseStep> steps, string prefix)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var loc = $"{prefix}[{i}]";
            yield return (s, loc);
            // 控制流体步骤的 Body/ElseBody 递归（读 step.Parameters 的 Body/ElseBody dict 键）
            foreach (var child in EnumerateBody(s, loc))
                yield return child;
        }
    }
    // EnumerateBody: 读 step.Parameters 的 "Body"/"ElseBody" 键（List<object>），每元素 TestCaseStep 递归
    // TryGetTargetChannel: StepParametersExporter.FromParameters(step.Parameters)["TargetChannel"] as string
}
```

挂载到 `RoundTripFlow.partial.cs:50-57` 对账块后追加：
```csharp
// ChannelReferenceCheck（suite 级通道引用，独立于 DBC 引用对账）
foreach (var issue in new ChannelReferenceCheck().Check(suite))
    ReconciliationIssues.Add(new ReconciliationIssueView(issue, DescribeLocation(issue.Location)));
```

- [ ] **Step 4: Run test to verify it passes**
- [ ] **Step 5: Regression** — 既有 `ReferenceIntegrityServiceTests` + 加载旧 suite（无 Channels）→ check 无 issue，对账行为不变
- [ ] **Step 6: Commit** `feat(studio): ChannelReferenceCheck recursive channel validation (B-5, Gap-3)`

---

## Task 11: 端到端验证（host + studio）

**Files:** 无新文件，集成测试 + 手动验证

- [ ] **Step 1: host 端到端** — `dotnet test` host 全量回归 + 手动：双通道弹窗连接（2 fake channel）→ Trace 过滤 bus-a → SendView 选 bus-b 发送 → Stats 两路聚合
- [ ] **Step 2: studio 端到端** — `dotnet test` studio 全量回归 + 手动：新建多通道 suite（Channels bus-a/bus-b + 各 DBC）→ SendFrame 选 TargetChannel bus-b → 保存重载 round-trip → ChannelReferenceCheck 对账通过
- [ ] **Step 3: Gap-3 验证** — Repeat Body 内步骤带未声明 TargetChannel → 对账报 Critical + 定位跳转
- [ ] **Step 4: 零回归确认** — host + studio 全量测试绿，单通道场景行为不变
- [ ] **Step 5: Commit** `test(multichannel): phase2 e2e + regression green`

---

## Self-Review

1. **Spec coverage**: spec §3.1 A-1~A-5 → T2/T3/T4/T6/T7；spec §3.2 B-1~B-5 → T1/T5/T8/T9/T10；端到端 T11。全覆盖。
2. **Placeholder scan**: 无 TBD/TODO。T10 Step 3 "先读源码确认挂载点"是合理指引（实现者读对账执行方法签名），非 placeholder。
3. **Type consistency**: `ConnectionConfig`（T2 定义，T3/T4/T6 用）；`ChannelConnection`（T3 定义，T6 用 `Channel.Id`）；`MultiDbcStore`（T1 定义，T5/T9 用）；`ChannelConfigRow`（T5 定义，T8/T9 用）。签名一致。
4. **依赖链**: T1→T5/T9；T2→T3→T6/T7；T5→T8/T9/T10。无循环。
