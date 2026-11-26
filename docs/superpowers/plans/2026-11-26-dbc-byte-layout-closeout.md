# DBC 字节位图收口期实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把一期/二期已交付的字节位图收口：性能预算有自动化守卫、文档与实现一致、二期终审的 6 条 minor 有明确处置。

**Architecture:** 不改功能主链路。新增两个性能守卫测试（Core 快照 / App 首次投影）、补齐切页停表（`IsVisibleChanged` → VM `SetLiveVisible`）、接线 `Clear()`、加强 XAML 断言、修正注释与 spec 文字。

**Tech Stack:** .NET 10 / WPF / xUnit + FluentAssertions。

**Spec:** `docs/superpowers/specs/2026-11-26-dbc-byte-layout-closeout-design.md`

## Global Constraints

- 只在 `peakcan-host` 内改动；**不修改 `peakcan-hil-core`**，不新增 NuGet。
- `TreatWarningsAsErrors=true`、`AnalysisMode=Recommended`。
- 只跑定向测试，**绝不跑全量测试**。
- 用户面向/业务注释用中文；技术 API 注释用英文。
- 不许静默降级。
- **commit / push 只在用户明确要求时执行**。
- 与 `main` 的差异只在当前 worktree（`feat/dbc-byte-layout-closeout`），不动主工作树。

## Review Focus

1. 计时测试在 CI 负载下误报 —— Task 1（阈值用 spec 预算，不收紧；失败时记录分布后加 note 调整）。
2. 切页停表后切回：必须立即恢复，而不是等下一次选中变化 —— Task 4 `Showing_The_View_Resumes_Live_Refreshes`。
3. 停表期间不得清空最后显示（避免闪空） —— Task 4 `Hiding...` 只断言“发布的新帧不生效”，不断言清空。
4. `Clear()` 接线必须发生在 `SelectedMessage = null` 之前 —— Task 3（否则 `SyncLive` 已先 `SetTarget(null)`，接线顺序虽不影响结果，但注释要说明防御目标）。
5. 扩展帧发布约定（合并 IDE 位） —— Task 5 集成测试。

---

### Task 1: 性能预算守卫

**Files:**
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/Dbc/SignalBitLayoutPerformanceTests.cs`
- Create: `tests/PeakCan.Host.App.Tests/ViewModels/DbcLayoutPerformanceTests.cs`

**Interfaces:**
- Consumes: `SignalBitLayout.Compute(Message)`、`DbcViewModel.SelectedMessage`。
- Produces: 无（测试）。

- [ ] **Step 1: 写 Core 性能测试**

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using FluentAssertions;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Dbc;
using Xunit;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;

namespace PeakCan.Host.Core.Tests.Analysis.Dbc;

/// <summary>
/// spec §6 性能预算守卫：FD 64 字节、覆盖 512 格的快照计算中位数 ≤5ms。
/// 这是回归守卫，不是基准测试；阈值不收紧，避免 CI 噪声。
/// </summary>
public class SignalBitLayoutPerformanceTests
{
    private static Message Fd512CellMessage()
    {
        var signals = new List<Signal>(8);
        for (var i = 0; i < 8; i++)
        {
            signals.Add(new Signal(
                $"S{i}", (ushort)(i * 64), 64, ByteOrder.LittleEndian,
                DbcValueType.Unsigned, 1, 0, 0, 0, "", Array.Empty<string>(),
                false, false, null));
        }

        return new Message(0x100, "Perf", 64, "ECU", signals, false, null);
    }

    [Fact]
    public void Compute_Fd512Cells_Median_Within_Five_Milliseconds()
    {
        var message = Fd512CellMessage();

        for (var i = 0; i < 10; i++) _ = SignalBitLayout.Compute(message);  // JIT warm-up

        var samples = new double[100];
        for (var i = 0; i < samples.Length; i++)
        {
            var sw = Stopwatch.StartNew();
            _ = SignalBitLayout.Compute(message);
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        SignalBitLayout.Compute(message).Cells.Should().HaveCount(512);

        Array.Sort(samples);
        var median = samples[samples.Length / 2];
        median.Should().BeLessThanOrEqualTo(5.0,
            "spec §6 快照预算：FD 512 格 ≤5ms（中位数）");
    }
}
```

- [ ] **Step 2: 写 App 首次投影测试**

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core.Dbc;
using Xunit;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// spec §6 首次实现预算守卫：选中 FD 64 字节消息（64×8 格）的 VM 投影 ≤30ms。
/// 不含 WPF 实际渲染；渲染手感仍由人工验收。
/// </summary>
public class DbcLayoutPerformanceTests
{
    private static Signal Sig(string name, ushort start, byte len)
        => new(name, start, len, ByteOrder.LittleEndian, DbcValueType.Unsigned,
               1, 0, 0, 0, "", Array.Empty<string>(), false, false, null);

    private static DbcViewModel NewVm(Message message)
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = new DbcViewModel(svc, new SignalViewModel(), NullLogger<DbcViewModel>.Instance);
        var doc = new DbcDocument(
            "", new List<Node>(), new List<Message> { message },
            new Dictionary<uint, Message>(),
            new Dictionary<string, ValueTable>());
        svc.GetType().GetEvent(nameof(DbcService.DbcLoaded))!.RaiseMethod(svc, doc);
        return vm;
    }

    [Fact]
    public void Selecting_Fd512CellMessage_Projects_Within_Thirty_Milliseconds()
    {
        var signals = new List<Signal>(8);
        for (var i = 0; i < 8; i++) signals.Add(Sig($"S{i}", (ushort)(i * 64), 64));
        var message = new Message(0x100, "Perf", 64, "ECU", signals, false, null);
        var vm = NewVm(message);
        var row = vm.Messages.Single();

        // JIT / 首次绑定预热，不参与计时。
        vm.SelectedMessage = row;
        vm.SelectedMessage = null;
        vm.SelectedMessage = row;
        vm.SelectedMessage = null;

        var sw = Stopwatch.StartNew();
        vm.SelectedMessage = row;
        sw.Stop();

        vm.LayoutRows.Should().HaveCount(64);
        vm.LayoutRows.Should().OnlyContain(r => r.Cells.Count == 8);
        sw.Elapsed.TotalMilliseconds.Should().BeLessThanOrEqualTo(30.0,
            "spec §6 首次实现预算：FD 512 格 ≤30ms（不含真实渲染）");
    }
}
```

- [ ] **Step 3: 验证断言真的会红（性能守卫的 RED）**

把两个阈值临时改成 `0.0`，跑：

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter FullyQualifiedName~SignalBitLayoutPerformanceTests -v minimal
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter FullyQualifiedName~DbcLayoutPerformanceTests -v minimal
```

Expected: 两条都失败（中位数 >0ms / 投影 >0ms）。改回 `5.0` / `30.0` 再跑，Expected: 通过。

- [ ] **Step 4: 提交（仅当用户明确要求 commit 时执行）**

```bash
git add tests/PeakCan.Host.Core.Tests/Analysis/Dbc/SignalBitLayoutPerformanceTests.cs tests/PeakCan.Host.App.Tests/ViewModels/DbcLayoutPerformanceTests.cs
git commit -m "test(dbc): add performance guards for byte layout"
```

---

### Task 2: 功能 spec 同步

**Files:**
- Modify: `docs/superpowers/specs/2026-11-25-dbc-byte-layout-design.md`

**Interfaces:**
- Consumes: 无。
- Produces: 无。

- [ ] **Step 1: 同步四处文字**

1. 文首状态：

```markdown
状态：已实施（一期静态布局 + 二期实时点亮已交付；收口期见 2026-11-26 closeout spec）
```

2. §4 `ByteLayoutView` 行：

```markdown
| `Views/ByteLayoutView.xaml`（新增控件） | `ItemsControl + UniformGrid` 渲染快照 + Viewbox `Stretch="Uniform"` 等比缩放；hover / 键盘聚焦 Popup |
```

3. §5.6 信号表列清单改为：

```markdown
- 信号表数据源：`VisibleSignals`（按 `Message.Signals` 声明顺序、同一 mux 过滤规则）；列：色块 / 名称 / 位布局 / 系数 / 偏移 / 单位 / 当前值 / 注释 / 诊断；颜色由 `PaletteIndex` 决定；旧 `Signals` 字符串列表只服务搜索。
```

4. §8 末尾补一行：

```markdown
二期（实时点亮）的测试：单槽缓存、worker 发布、33ms VM 刷新（含 Timer 投递）、mux 失配、短帧、FD 64 字节、切页停表、XAML 触发器、DI 组合；见 `docs/superpowers/plans/2026-11-26-dbc-byte-layout-phase2.md` 与收口期计划。
```

- [ ] **Step 2: 核对**

Run: `grep -n "Canvas + Viewbox\|待实施" docs/superpowers/specs/2026-11-25-dbc-byte-layout-design.md`

Expected: 无输出（旧措辞已清掉）。

- [ ] **Step 3: 提交（仅当用户明确要求 commit 时执行）**

```bash
git add docs/superpowers/specs/2026-11-25-dbc-byte-layout-design.md
git commit -m "docs(dbc): sync byte-layout spec with shipped implementation"
```

---

### Task 3: `Clear()` 接线与注释修正

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/DbcViewModel/LoadingFlow.partial.cs:135-139`
- Modify: `src/PeakCan.Host.App/ViewModels/DbcViewModel/LiveFlow.partial.cs:32-38`
- Modify: `src/PeakCan.Host.App/ViewModels/DbcViewModel.cs:17-26`

**Interfaces:**
- Consumes: `LiveFrameSlot.Clear()`（二期已交付）。
- Produces: 无。

- [ ] **Step 1: `OnLoaded` 接线**

`((Action)(() => {` 之后、`SelectedMessage = null;` 之前：

```csharp
            // 重载换文档：单槽里的旧帧不能点亮新文档同 ID 消息
            //（为将来“重载后按 Id+Name 恢复选中”留的正确性保障）。
            _liveSlot?.Clear();
```

- [ ] **Step 2: `_liveGate` 并发边界注释**

`LiveFlow.partial.cs` 的 `_liveGate` 字段上方：

```csharp
    // 并发边界：_liveGate 只串行化 RefreshLiveLayout 自身，不保护 UpdateLayout 对
    // LayoutRows / VisibleSignals 的重建。生产环境两者都在 UI 线程，天然串行；
    // 无 WPF Application 的宿主里 RunOnUiPost 会就地执行，调用方必须先停表再重建集合。
    private readonly object _liveGate = new();
```

- [ ] **Step 3: 类注释与 `IDisposable` 对齐**

把 §17-26 那段“previous IDisposable implementation was a latent footgun … GC + finalizer pass handles cleanup”改为：

```markdown
/// <b>Event wiring:</b> subscribes to <see cref="DbcService.DbcLoaded"/>
/// and <see cref="DbcService.LoadFailed"/> in the constructor. The
/// shell registers the VM as a DI singleton that lives for the whole
/// app, so the event subscriptions are intentionally NEVER unsubscribed.
/// The class also implements <see cref="IDisposable"/>, but only to
/// release the phase-2 live-refresh timer — event wiring still lives
/// for the process lifetime by design.
```

- [ ] **Step 4: 验证**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter FullyQualifiedName~DbcLayoutViewModelTests -v minimal`

Expected: 全部通过（`Clear()` 是防御性接线，现有 `Dbc_Reload_Clears_Live_Target` 覆盖可观察行为；无法构造一个“不接 `Clear()` 就失败”的测试，因为当前重载路径本来就会 `SetTarget(null)`——这一点记入 ledger）。

- [ ] **Step 5: 提交（仅当用户明确要求 commit 时执行）**

```bash
git add src/PeakCan.Host.App/ViewModels/DbcViewModel/LoadingFlow.partial.cs src/PeakCan.Host.App/ViewModels/DbcViewModel/LiveFlow.partial.cs src/PeakCan.Host.App/ViewModels/DbcViewModel.cs
git commit -m "fix(dbc): clear live slot on reload and correct lifecycle comments"
```

---

### Task 4: 切标签页停表

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/DbcViewModel/LiveFlow.partial.cs`
- Modify: `src/PeakCan.Host.App/Views/DbcView.xaml.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/DbcLayoutViewModelTests.cs`

**Interfaces:**
- Consumes: 现有 `SyncLive` / `_liveTimer`。
- Produces: `internal void DbcViewModel.SetLiveVisible(bool visible)`。

- [ ] **Step 1: 写失败测试**

在 `DbcLayoutViewModelTests` 追加：

```csharp
    [Fact]
    public async Task Hiding_The_View_Stops_Live_Refreshes()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, SimpleMessage());
        vm.SelectedMessage = vm.Messages.Single();
        vm.SetLiveVisible(false);

        slot.Publish(0x100, new byte[] { 0x05 });
        await Task.Delay(200);   // 6 个 33ms 周期，足够暴露“没停表”

        vm.VisibleSignals.Single().LiveValue.Should()
            .Be("—", "切走标签页后 33ms 刷新必须停止");
    }

    [Fact]
    public void Showing_The_View_Resumes_Live_Refreshes()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, SimpleMessage());
        vm.SelectedMessage = vm.Messages.Single();
        vm.SetLiveVisible(false);

        slot.Publish(0x100, new byte[] { 0x05 });
        vm.SetLiveVisible(true);

        vm.VisibleSignals.Single().LiveValue.Should()
            .Be("5", "切回标签页必须立即按最新帧恢复，而不是等下一次 tick");
    }

    [Fact]
    public void Showing_Without_Selection_Is_NoOp()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);

        vm.SetLiveVisible(false);
        vm.SetLiveVisible(true);

        vm.LayoutSnapshot.Should().BeNull();
    }
```

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter FullyQualifiedName~DbcLayoutViewModelTests -v minimal`

Expected: 编译失败 —— `SetLiveVisible` 不存在。

- [ ] **Step 2: 实现 VM 联动**

`LiveFlow.partial.cs` 字段区：

```csharp
    private bool _liveVisible = true;
```

`SyncLive` 开头：

```csharp
        if (_liveSlot is null || _liveDisposed) return;
        if (!_liveVisible)
        {
            _liveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }
```

新增方法：

```csharp
    /// <summary>
    /// 标签页可见性联动：切走停表（保留最后显示，不闪空），切回立即恢复刷新。
    /// 与选中变化正交；无选中时切回是 no-op。
    /// </summary>
    internal void SetLiveVisible(bool visible)
    {
        if (_liveDisposed) return;
        _liveVisible = visible;

        if (!visible)
        {
            _liveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        if (SelectedMessage?.RawMessage is not null) SyncLive();
    }
```

- [ ] **Step 3: View 接线**

`DbcView.xaml.cs` 改为：

```csharp
using System.Windows;
using System.Windows.Controls;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

/// <summary>
/// Code-behind for the DBC tab view. Visibility changes drive the
/// phase-2 live-refresh timer; everything else lives in the VM.
/// </summary>
public partial class DbcView : UserControl
{
    public DbcView()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>切到其他标签页时停掉 33ms 实时刷新，切回再恢复。</summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => (DataContext as DbcViewModel)?.SetLiveVisible(IsVisible);
}
```

- [ ] **Step 4: 跑测试确认 GREEN**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter FullyQualifiedName~DbcLayoutViewModelTests -v minimal`

Expected: 全部通过（新增 3 条）。

- [ ] **Step 5: 提交（仅当用户明确要求 commit 时执行）**

```bash
git add src/PeakCan.Host.App/ViewModels/DbcViewModel/LiveFlow.partial.cs src/PeakCan.Host.App/Views/DbcView.xaml.cs tests/PeakCan.Host.App.Tests/ViewModels/DbcLayoutViewModelTests.cs
git commit -m "feat(dbc): pause live layout refresh when the DBC tab is hidden"
```

---

### Task 5: 扩展帧测试与 XAML 断言加强

**Files:**
- Modify: `tests/PeakCan.Host.App.Tests/Services/DbcDecodeBackgroundServiceTests.cs`
- Modify: `tests/PeakCan.Host.App.Tests/Views/ByteLayoutViewLayoutTests.cs`
- Modify: `tests/PeakCan.Host.App.Tests/Views/DbcViewLayoutTests.cs`

**Interfaces:**
- Consumes: 二期已交付的 `LiveFrameSlot` 与 XAML。
- Produces: 无。

- [ ] **Step 1: 扩展帧缓存测试**

在 `DbcDecodeBackgroundServiceTests` 追加 helper 与用例：

```csharp
    private static DbcDocument DocWithOneSignalAtExtendedId()
    {
        var sig = new Signal(
            Name: "S1", StartBit: 0, Length: 8,
            Order: ByteOrder.LittleEndian,
            ValueType: DbcValueType.Unsigned,
            Factor: 1.0, Offset: 0.0,
            Min: 0, Max: 255, Unit: "u", Receivers: Array.Empty<string>());
        var msg = new Message(
            Id: 0x80001000, Name: "M1", Dlc: 8, Sender: "n1",
            Signals: new[] { sig },
            IsMultiplexed: false, MultiplexorSignalIndex: null);
        var dict = new Dictionary<uint, Message> { [0x80001000] = msg };
        return new DbcDocument(
            Version: "",
            Nodes: Array.Empty<Node>(),
            Messages: new[] { msg },
            MessagesById: dict,
            ValueTables: new Dictionary<string, ValueTable>());
    }

    [Fact]
    public async Task Extended_Frame_Is_Cached_Under_Merged_Ide_Id()
    {
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(DocWithOneSignalAtExtendedId());
        var slot = new LiveFrameSlot();
        slot.SetTarget(0x80001000);
        var svc = new DbcDecodeBackgroundService(
            dbc, new SignalViewModel(), new TraceViewModel(), slot,
            NullLogger<DbcDecodeBackgroundService>.Instance);

        using var startCts = new System.Threading.CancellationTokenSource();
        await svc.StartAsync(startCts.Token);
        try
        {
            var frame = new CanFrame(
                new CanId(0x1000, FrameFormat.Extended),
                new byte[] { 0x7E },
                FrameFlags.None,
                new ChannelId(0x51),
                Timestamp.FromMicroseconds(0));
            svc.OnFrame(frame);

            LiveFramePayload payload = default;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!slot.TryRead(0x80001000, out payload) && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            slot.TryRead(0x80001000, out payload).Should()
                .BeTrue("扩展帧必须按合并 IDE 位后的消息 ID 进单槽");
            payload.Data.Span.ToArray().Should().Equal(new byte[] { 0x7E });
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }
```

- [ ] **Step 2: 加强 XAML 断言**

`ByteLayoutViewLayoutTests` 的 `Zero_Bits_Are_Dimmed_Through_An_IsSet_Trigger` 里，把 Opacity setter 的断言从“存在”改为钉值：

```csharp
        trigger.Descendants(P + "Setter")
            .Single(s => ((string?)s.Attribute("Property")) == "Opacity")
            .Attribute("Value")!.Value.Should().Be("0.28");
```

`DbcViewLayoutTests` 追加：

```csharp
    [Fact]
    public void Signal_Table_Header_And_Rows_Have_Eight_Aligned_Columns()
    {
        var rowDetails = DbcView()
            .Descendants(P + "DataGrid.RowDetailsTemplate").Single();

        var liveHeader = rowDetails.Descendants(P + "TextBlock")
            .Single(t => (string?)t.Attribute("Text") == "当前值");
        ((string?)liveHeader.Attribute("Grid.Column")).Should().Be("5");

        var headerGrid = liveHeader.Ancestors(P + "Grid").First();
        headerGrid.Element(P + "Grid.ColumnDefinitions")!
            .Elements(P + "ColumnDefinition").Should().HaveCount(8);

        var liveValue = rowDetails.Descendants(P + "TextBlock")
            .Single(t => (((string?)t.Attribute("Text")) ?? "").Contains(
                "LiveValue", StringComparison.Ordinal));
        ((string?)liveValue.Attribute("Grid.Column")).Should().Be("5");

        var dataGrid = liveValue.Ancestors(P + "Grid").First();
        dataGrid.Element(P + "Grid.ColumnDefinitions")!
            .Elements(P + "ColumnDefinition").Should().HaveCount(8);
    }
```

- [ ] **Step 3: 跑测试确认 GREEN**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter FullyQualifiedName~DbcDecodeBackgroundServiceTests -v minimal
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~ByteLayoutViewLayoutTests|FullyQualifiedName~DbcViewLayoutTests" -v minimal
```

Expected: 全部通过。若扩展帧用例失败，说明 worker 的 IDE 合并或发布键有回归——修实现，不改测试。

- [ ] **Step 4: 提交（仅当用户明确要求 commit 时执行）**

```bash
git add tests/PeakCan.Host.App.Tests/Services/DbcDecodeBackgroundServiceTests.cs tests/PeakCan.Host.App.Tests/Views/ByteLayoutViewLayoutTests.cs tests/PeakCan.Host.App.Tests/Views/DbcViewLayoutTests.cs
git commit -m "test(dbc): pin extended-frame live caching and XAML column alignment"
```

---

### Task 6: 定向回归与构建

- [ ] **Step 1: 跑全部定向测试**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~SignalBitLayout" -v minimal
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~LiveFrameSlotTests|FullyQualifiedName~DbcDecodeBackgroundServiceTests|FullyQualifiedName~DbcLayoutViewModelTests|FullyQualifiedName~DbcLayoutPerformanceTests|FullyQualifiedName~ByteLayoutViewLayoutTests|FullyQualifiedName~DbcViewLayoutTests|FullyQualifiedName~AppHostBuilderTests" -v minimal
dotnet build PeakCan.Host.slnx -c Debug
```

Expected: Core 21/21（20 + 1 性能守卫）、App 全绿、构建 0 error / 0 warning。**不要跑全量测试。**

- [ ] **Step 2: 人工验收（需用户执行）**

- 打开 FD 64 字节消息：RowDetails 展开无明显卡顿。
- 选中消息 → 切到 Trace/Signal 页 2 秒 → 切回：切走期间值冻结，切回立即恢复。
- 不选消息时 CPU 无持续 30Hz 唤醒。
- 原有 5 项交互验收复跑。

- [ ] **Step 3: 提交（仅当用户明确要求 commit 时执行）**

收口期不留独立提交；本轮所有 commit 由用户决定是否合并回 `main`。

---

## Self-Review

- **Spec 覆盖**：性能实测（Task 1）、文档同步（Task 2）、6 条 minor（Task 3/4/5 分别覆盖 Clear、切页停表、`_liveGate` 注释、扩展帧、XAML 断言、IDisposable 注释）。
- **占位符扫描**：无 TBD / TODO / “类似 Task N”。
- **类型一致性**：`SetLiveVisible(bool)` 在 Task 4 的 VM 与 View 中一致；`LiveFrameSlot.Clear()` 已在二期存在；`Grid.Column`/`ColumnDefinitions` 名称与 XAML 一致。
- **Review Focus**：5 类风险均有具名测试或明确的人工项。
