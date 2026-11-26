# DBC 字节位图二期（实时点亮）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 选中 DBC 消息后，以 33ms 节流把该消息最新一帧的载荷投影到现有字节位图与信号表：0 位变淡、信号表新增“当前值”列；其余消息零开销。

**Architecture:** 后台解码 worker 只把**当前选中消息**的最新原始载荷拷进单槽 `LiveFrameSlot`（非选中消息不拷贝）；`DbcViewModel` 在选中/取消选中时启停一个 33ms `System.Threading.Timer`，tick 经 `RunOnUiPost` 回到 UI 线程，按当前 mux 分支重解可见信号并更新格子的 `IsSet` 与信号行的 `LiveValue`。不新增定时器常驻唤醒，不预解码（mux 分支由用户随时切换）。

**Tech Stack:** .NET 10 / WPF / CommunityToolkit.Mvvm（`ObservableObject` + `[ObservableProperty]`）/ xUnit + FluentAssertions。

**Spec:** `docs/superpowers/specs/2026-11-25-dbc-byte-layout-design.md`（§3 二期、§6 二期预算）

## Global Constraints

- 只在 `peakcan-host` 内改动；**不修改 `peakcan-hil-core`**，不新增 NuGet 依赖。
- `TreatWarningsAsErrors=true`、`AnalysisMode=Recommended`；新增公共类型避免 CA1819（不要公开 `byte[]` 属性，用 `ReadOnlyMemory<byte>`）。
- 只跑定向测试，**绝不跑全量测试**。
- 用户面向/业务注释用中文；技术 API、DBC 协议字段注释用英文。
- 不许静默降级：载荷覆盖不到的位/信号必须显示为“无数据”（`null` / “—”），不能按 0 解码。
- **commit / push 只在用户明确要求时执行**；计划中的提交步骤默认不执行。
- 共享工作目录：提交前先 `git branch --show-current` + `git status --porcelain`。

## 设计决策（2026-11-26 已与用户确认）

1. **单槽缓存**：只缓存当前选中消息的最新帧；选中变化时 VM 调 `SetTarget`，worker 只拷贝匹配目标的帧。副作用：spec §6 “按消息 ID 存”的字面措辞需同步为单槽（Task 6）。
2. **点亮视觉**：归属信号的格子里，当前帧为 0 的位不透明度降到 0.28，为 1 的保持原色；信号表新增“当前值”列（物理值，`0.###`）。
3. **mux 不一致**：帧里的 mux 值与下拉分支不符时，muxed 信号的“当前值”显示“—”；位图仍按载荷位点亮。

## Review Focus

以下 5 类输入/状态最容易在实现里出错，每类都在对应任务里有测试钉住：

1. 选中消息尚未收到任何帧：位图保持静态、值列“—” —— Task 4 `Live_Without_New_Frame_Keeps_Dashes`。
2. 帧长度短于 DBC 声明的 DLC：缺失字节不得按 0 解码 —— Task 4 `Short_Frame_Leaves_Bytes_Beyond_Payload_Without_Live_State`。
3. CAN FD 64 字节载荷：第 63 字节也能点亮，不能固定按 8 字节处理 —— Task 4 `Fd_Sixty_Four_Byte_Payload_Lights_Last_Byte`。
4. 运行中切换 mux 分支：必须立即按新分支重解，不能等下一次 tick —— Task 4 `Switching_Mux_Reapplies_Live_Values_For_New_Branch`。
5. 取消选中 / DBC 重载：单槽目标清空，旧帧不得点亮新文档 —— Task 1 `Target_Change_Drops_The_Previous_Message_Payload`、Task 4 `Deselecting_...` / `Dbc_Reload_...`。

---

### Task 1: 单槽缓存 `LiveFrameSlot`

**Files:**
- Create: `src/PeakCan.Host.App/Services/LiveFrameSlot.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/LiveFrameSlotTests.cs`

**Interfaces:**
- Consumes: 无（纯 BCL）。
- Produces:
  - `public sealed class LiveFrameSlot`
  - `void SetTarget(uint? messageId)`
  - `void Publish(uint messageId, ReadOnlySpan<byte> data)`
  - `bool TryRead(uint messageId, out LiveFramePayload payload)`
  - `void Clear()`
  - `public readonly record struct LiveFramePayload(ReadOnlyMemory<byte> Data, long Sequence)`

- [ ] **Step 1: 写失败测试**

创建 `tests/PeakCan.Host.App.Tests/Services/LiveFrameSlotTests.cs`：

```csharp
using FluentAssertions;
using PeakCan.Host.App.Services;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// 单槽缓存契约：只保存目标消息的最新一帧；拷贝调用方缓冲区；切换目标即丢弃旧载荷。
/// </summary>
public class LiveFrameSlotTests
{
    private static readonly byte[] TwoBytePayload = { 0x11, 0x22 };
    private static readonly byte[] OneBytePayload = { 0xAA };

    private static LiveFrameSlot NewTargetedSlot(uint id)
    {
        var slot = new LiveFrameSlot();
        slot.SetTarget(id);
        return slot;
    }

    [Fact]
    public void Publish_Without_Target_Keeps_Slot_Empty()
    {
        var slot = new LiveFrameSlot();

        slot.Publish(0x100, TwoBytePayload);

        slot.TryRead(0x100, out _).Should().BeFalse("没有选中消息时后台 worker 不应缓存任何帧");
    }

    [Fact]
    public void Publish_For_Other_Message_Is_Ignored()
    {
        var slot = NewTargetedSlot(0x100);

        slot.Publish(0x200, TwoBytePayload);

        slot.TryRead(0x100, out _).Should().BeFalse();
    }

    [Fact]
    public void Publish_Stores_Latest_Payload_For_Target()
    {
        var slot = NewTargetedSlot(0x100);

        slot.Publish(0x100, TwoBytePayload);
        slot.Publish(0x100, OneBytePayload);

        slot.TryRead(0x100, out var payload).Should().BeTrue();
        payload.Data.Span.ToArray().Should().Equal(OneBytePayload);
        payload.Sequence.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Second_Publish_Has_Higher_Sequence()
    {
        var slot = NewTargetedSlot(0x100);
        slot.Publish(0x100, TwoBytePayload);
        slot.TryRead(0x100, out var first).Should().BeTrue();

        slot.Publish(0x100, OneBytePayload);
        slot.TryRead(0x100, out var second).Should().BeTrue();

        second.Sequence.Should().BeGreaterThan(first.Sequence);
    }

    [Fact]
    public void Publish_Copies_The_Caller_Buffer()
    {
        var slot = NewTargetedSlot(0x100);
        var source = new byte[] { 0x01, 0x02 };

        slot.Publish(0x100, source);
        source[0] = 0xFF;

        slot.TryRead(0x100, out var payload).Should().BeTrue();
        payload.Data.Span[0].Should().Be(0x01, "SDK 的缓冲区可能在 OnFrame 返回后被复用");
    }

    [Fact]
    public void Target_Change_Drops_The_Previous_Message_Payload()
    {
        var slot = NewTargetedSlot(0x100);
        slot.Publish(0x100, TwoBytePayload);

        slot.SetTarget(0x200);

        slot.TryRead(0x100, out _).Should().BeFalse("切到另一条消息时不能把上一条的帧喂给新消息");
        slot.TryRead(0x200, out _).Should().BeFalse("新目标还没收到帧");
    }

    [Fact]
    public void Clear_Drops_The_Payload()
    {
        var slot = NewTargetedSlot(0x100);
        slot.Publish(0x100, TwoBytePayload);

        slot.Clear();

        slot.TryRead(0x100, out _).Should().BeFalse();
    }

    [Fact]
    public void Sixty_Four_Byte_Payload_Is_Preserved()
    {
        var slot = NewTargetedSlot(0x100);
        var buffer = new byte[64];
        buffer[63] = 0x80;

        slot.Publish(0x100, buffer);

        slot.TryRead(0x100, out var payload).Should().BeTrue();
        payload.Data.Length.Should().Be(64);
        payload.Data.Span[63].Should().Be(0x80);
    }
}
```

- [ ] **Step 2: 跑测试确认 RED**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter FullyQualifiedName~LiveFrameSlotTests -v minimal`

Expected: 编译失败 —— `LiveFrameSlot` / `LiveFramePayload` 不存在。

- [ ] **Step 3: 最小实现**

创建 `src/PeakCan.Host.App/Services/LiveFrameSlot.cs`：

```csharp
namespace PeakCan.Host.App.Services;

/// <summary>
/// 单槽缓存：只保存当前选中消息的最新一帧载荷。
/// <para>
/// 后台解码 worker 在 <see cref="Publish"/> 时比对目标消息 ID，非选中消息直接丢弃，
/// 因此总线上有多少帧都不会产生额外拷贝。UI 的 33ms 节流器通过
/// <see cref="TryRead"/> 取最新一帧；<see cref="LiveFramePayload.Sequence"/> 用于
/// 跳过没有新帧的 tick。
/// </para>
/// <para>
/// 线程安全：worker 线程写、UI 定时器线程读。锁内只做不可变快照的引用交换；
/// 载荷数组发布后不再修改。
/// </para>
/// </summary>
public sealed class LiveFrameSlot
{
    private const int NoTarget = -1;

    private readonly object _gate = new();
    private int _targetId = NoTarget;   // uint 消息 ID 转 int 存储；无目标用 -1
    private LiveFramePayload? _latest;
    private long _sequence;

    /// <summary>
    /// 设置当前选中消息；null 表示没有选中消息。切换到不同消息时清空旧载荷，
    /// 防止上一条消息的帧点亮新选中的消息。
    /// </summary>
    public void SetTarget(uint? messageId)
    {
        var next = messageId is { } id ? unchecked((int)id) : NoTarget;
        lock (_gate)
        {
            if (next != _targetId) _latest = null;
            _targetId = next;
        }
    }

    /// <summary>
    /// 后台 worker 调用：缓存匹配目标消息的最新载荷。不匹配则直接返回，不分配。
    /// </summary>
    public void Publish(uint messageId, ReadOnlySpan<byte> data)
    {
        if (unchecked((int)messageId) != Volatile.Read(ref _targetId)) return;

        var copy = data.ToArray();
        lock (_gate)
        {
            // 拷贝期间目标可能被切走，锁内再确认一次。
            if (unchecked((int)messageId) != _targetId) return;
            _latest = new LiveFramePayload(copy, ++_sequence);
        }
    }

    /// <summary>
    /// 取目标消息的最新载荷；目标不匹配或还没有帧时返回 false。
    /// 传 <paramref name="messageId"/> 而不是只读槽，是为了让迟到的 tick 无法把
    /// 上一条消息的载荷应用到新选中消息上。
    /// </summary>
    public bool TryRead(uint messageId, out LiveFramePayload payload)
    {
        lock (_gate)
        {
            if (_targetId == unchecked((int)messageId) && _latest is { } latest)
            {
                payload = latest;
                return true;
            }
        }

        payload = default;
        return false;
    }

    /// <summary>丢弃缓存；DBC 重载时调用，避免旧文档的帧点亮新文档同 ID 消息。</summary>
    public void Clear()
    {
        lock (_gate) { _latest = null; }
    }
}

/// <summary>一帧载荷快照；<see cref="Data"/> 发布后不再修改。</summary>
public readonly record struct LiveFramePayload(ReadOnlyMemory<byte> Data, long Sequence);
```

- [ ] **Step 4: 跑测试确认 GREEN**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter FullyQualifiedName~LiveFrameSlotTests -v minimal`

Expected: 8/8 通过。

- [ ] **Step 5: 提交（仅当用户明确要求 commit 时执行）**

```bash
git add src/PeakCan.Host.App/Services/LiveFrameSlot.cs tests/PeakCan.Host.App.Tests/Services/LiveFrameSlotTests.cs
git commit -m "feat(dbc): add single-slot live frame buffer for byte layout"
```

---

### Task 2: 后台 worker 写入单槽 + DI

**Files:**
- Modify: `src/PeakCan.Host.App/Services/DbcDecodeBackgroundService.cs`
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs:155-167`
- Test: `tests/PeakCan.Host.App.Tests/Services/DbcDecodeBackgroundServiceTests.cs`

**Interfaces:**
- Consumes: `LiveFrameSlot.Publish(uint, ReadOnlySpan<byte>)`（Task 1）。
- Produces: `DbcDecodeBackgroundService.DbcDecodeBackgroundService(DbcService, SignalViewModel, TraceViewModel, LiveFrameSlot, ILogger<...>)`；DI 中 `LiveFrameSlot` 单例。

- [ ] **Step 1: 写失败测试（同时更新既有构造点）**

在 `DbcDecodeBackgroundServiceTests.cs` 末尾追加：

```csharp
    [Fact]
    public async Task Matching_Frame_Is_Cached_In_LiveSlot()
    {
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(DocWithOneSignal());
        var slot = new LiveFrameSlot();
        slot.SetTarget(0x100);
        var svc = new DbcDecodeBackgroundService(
            dbc, new SignalViewModel(), new TraceViewModel(), slot,
            NullLogger<DbcDecodeBackgroundService>.Instance);

        using var startCts = new System.Threading.CancellationTokenSource();
        await svc.StartAsync(startCts.Token);
        try
        {
            var frame = new CanFrame(
                new CanId(0x100, FrameFormat.Standard),
                new byte[] { 0x42 },
                FrameFlags.None,
                new ChannelId(0x51),
                Timestamp.FromMicroseconds(0));
            svc.OnFrame(frame);

            LiveFramePayload payload = default;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!slot.TryRead(0x100, out payload) && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            slot.TryRead(0x100, out payload).Should().BeTrue("匹配帧必须进入单槽缓存");
            payload.Data.Span.ToArray().Should().Equal(new byte[] { 0x42 });
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }
```

把该文件里其余 8 处 `new DbcDecodeBackgroundService(dbc, sigVm, traceVm, ...)` 构造补上 `new LiveFrameSlot(),`（放在 logger 之前）。

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter FullyQualifiedName~DbcDecodeBackgroundServiceTests -v minimal`

Expected: 编译失败 —— 构造函数没有 5 个参数。

- [ ] **Step 2: 实现 worker 写入**

`DbcDecodeBackgroundService.cs`：

字段区加：

```csharp
    private readonly LiveFrameSlot _liveSlot;
```

构造函数加参数并赋值：

```csharp
    public DbcDecodeBackgroundService(
        DbcService dbc,
        SignalViewModel signalVm,
        TraceViewModel traceVm,
        LiveFrameSlot liveSlot,
        ILogger<DbcDecodeBackgroundService> logger)
    {
        _dbc = dbc ?? throw new ArgumentNullException(nameof(dbc));
        _signalVm = signalVm ?? throw new ArgumentNullException(nameof(signalVm));
        _traceVm = traceVm ?? throw new ArgumentNullException(nameof(traceVm));
        _liveSlot = liveSlot ?? throw new ArgumentNullException(nameof(liveSlot));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
```

在 `_signalVm.ApplyFrame(frame, msg);` 之后加：

```csharp
                    // 二期：只缓存当前选中消息的原始载荷（单槽，非选中消息零拷贝）。
                    _liveSlot.Publish(msg.Id, frame.Data.Span);
```

- [ ] **Step 3: DI 接线**

`AppHostBuilder.cs` 在 `AddSingleton<DbcDecodeBackgroundService>` 之前加：

```csharp
        // 二期：单槽缓存当前选中消息的最新载荷（解码 worker 写、DBC 页 33ms 节流读）。
        builder.Services.AddSingleton<LiveFrameSlot>();
```

工厂调用补参数：

```csharp
        builder.Services.AddSingleton<DbcDecodeBackgroundService>(sp =>
            new DbcDecodeBackgroundService(
                sp.GetRequiredService<DbcService>(),
                sp.GetRequiredService<SignalViewModel>(),
                sp.GetRequiredService<TraceViewModel>(),
                sp.GetRequiredService<LiveFrameSlot>(),
                sp.GetRequiredService<ILogger<DbcDecodeBackgroundService>>()));
```

- [ ] **Step 4: 跑测试确认 GREEN**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter FullyQualifiedName~DbcDecodeBackgroundServiceTests -v minimal`

Expected: 全部通过（含原有用例与新用例）。

- [ ] **Step 5: 提交（仅当用户明确要求 commit 时执行）**

```bash
git add src/PeakCan.Host.App/Services/DbcDecodeBackgroundService.cs src/PeakCan.Host.App/Composition/AppHostBuilder.cs tests/PeakCan.Host.App.Tests/Services/DbcDecodeBackgroundServiceTests.cs
git commit -m "feat(dbc): publish selected-message payload to live slot"
```

---

### Task 3: 布局行模型支持实时状态

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/DbcViewModel/LayoutFlow.partial.cs:197-223`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/DbcLayoutViewModelTests.cs`

**Interfaces:**
- Consumes: 无。
- Produces:
  - `ByteLayoutCell.IsSet`（`bool?`，`null` = 无实时数据）
  - `DbcSignalRow.LiveValue`（`string`，默认 `"—"`）

- [ ] **Step 1: 写失败测试**

在 `DbcLayoutViewModelTests` 追加（同时给该测试类补一个非 mux 的消息 helper）：

```csharp
    private static Message SimpleMessage()
        => new(0x100, "Simple", 8, "ECU", new List<Signal>
        {
            Sig("Bits", 0, 8),
        }, false, null);

    [Fact]
    public void New_Layout_Cells_Start_Without_Live_State()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, SimpleMessage());
        vm.SelectedMessage = vm.Messages.Single();

        vm.LayoutRows.SelectMany(r => r.Cells).Should().OnlyContain(c => c.IsSet == null);
        vm.VisibleSignals.Should().OnlyContain(s => s.LiveValue == "—");
    }
```

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter FullyQualifiedName~DbcLayoutViewModelTests -v minimal`

Expected: 编译失败 —— `IsSet` / `LiveValue` 不存在。

- [ ] **Step 2: 实现**

`LayoutFlow.partial.cs` 把 `DbcSignalRow` 改为：

```csharp
/// <summary>信号表一行：原始 Signal、调色板索引、布局诊断文本与实时值。</summary>
public sealed partial class DbcSignalRow : ObservableObject
{
    public DbcSignalRow(Signal signal, int paletteIndex, string diagnosticText)
    {
        Signal = signal;
        PaletteIndex = paletteIndex;
        DiagnosticText = diagnosticText;
    }

    public Signal Signal { get; }

    public int PaletteIndex { get; }

    public string DiagnosticText { get; }

    public string Name => Signal.Name;

    public string BitLayout => string.Create(
        CultureInfo.InvariantCulture,
        $"{Signal.StartBit}|{Signal.Length}@{(Signal.Order == ByteOrder.LittleEndian ? '1' : '0')}{(Signal.ValueType == DbcValueType.Signed ? '-' : '+')}");

    public string FactorOffset => string.Create(
        CultureInfo.InvariantCulture, $"{Signal.Factor} / {Signal.Offset}");

    public string Unit => Signal.Unit;

    public string Comment => Signal.Comment ?? "";

    /// <summary>当前帧解码后的物理值；无数据 / mux 分支不匹配时显示“—”。</summary>
    [ObservableProperty]
    private string _liveValue = "—";
}
```

把 `ByteLayoutCell` 改为：

```csharp
/// <summary>位图的一格：位号、归属信号、调色板索引与 hover 文本。</summary>
public sealed partial class ByteLayoutCell : ObservableObject
{
    public ByteLayoutCell(int bitNumber, string? ownerSignal, int paletteIndex, string tooltip)
    {
        BitNumber = bitNumber;
        OwnerSignal = ownerSignal;
        PaletteIndex = paletteIndex;
        Tooltip = tooltip;
    }

    public int BitNumber { get; }

    public string? OwnerSignal { get; }

    public int PaletteIndex { get; }

    public string Tooltip { get; }

    /// <summary>
    /// 当前帧里该位的值：true/false = 有数据；null = 无实时数据（未选中、
    /// 该字节超出帧载荷，或该格不属于任何信号）。只有归属信号的格子在实时刷新时赋值。
    /// </summary>
    [ObservableProperty]
    private bool? _isSet;
}
```

- [ ] **Step 3: 跑测试确认 GREEN**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter FullyQualifiedName~DbcLayoutViewModelTests -v minimal`

Expected: 全部通过（既有布局用例 + 新用例）。

- [ ] **Step 4: 提交（仅当用户明确要求 commit 时执行）**

```bash
git add src/PeakCan.Host.App/ViewModels/DbcViewModel/LayoutFlow.partial.cs tests/PeakCan.Host.App.Tests/ViewModels/DbcLayoutViewModelTests.cs
git commit -m "feat(dbc): make layout rows observable for live bit state"
```

---

### Task 4: VM 实时刷新流程

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/DbcViewModel/LiveFlow.partial.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/DbcViewModel.cs:45,118-135`
- Modify: `src/PeakCan.Host.App/ViewModels/DbcViewModel/LayoutFlow.partial.cs`（`UpdateLayout` 两个出口调用 `SyncLive()`）
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder/ViewModelsBatch2Flow.cs:204`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/DbcLayoutViewModelTests.cs`

**Interfaces:**
- Consumes: `LiveFrameSlot.TryRead(uint, out LiveFramePayload)`（Task 1）、`ByteLayoutCell.IsSet` / `DbcSignalRow.LiveValue`（Task 3）。
- Produces:
  - `DbcViewModel(DbcService, SignalViewModel, ILogger<DbcViewModel>, IFileDialogService?, IShellLauncher?, Func<string>?, LiveFrameSlot?)`
  - `internal void RefreshLiveLayoutForTest()`
  - `internal void StopLiveTimerForTest()`
  - `internal LiveFrameSlot? LiveSlot`

- [ ] **Step 1: 写失败测试**

在 `DbcLayoutViewModelTests` 中把 `NewVm` 改为：

```csharp
    private static DbcViewModel NewVm(out DbcService svc, LiveFrameSlot? liveSlot = null)
    {
        svc = new DbcService(NullLogger<DbcService>.Instance);
        return new DbcViewModel(
            svc, new SignalViewModel(), NullLogger<DbcViewModel>.Instance,
            liveSlot: liveSlot);
    }
```

追加用例：

```csharp
    [Fact]
    public void Live_Payload_Lights_Cells_And_Decodes_Signal_Value()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, SimpleMessage());
        vm.SelectedMessage = vm.Messages.Single();
        vm.StopLiveTimerForTest();

        slot.Publish(0x100, new byte[] { 0x05 });
        vm.RefreshLiveLayoutForTest();

        var cells = vm.LayoutRows[0].Cells;
        cells.Single(c => c.BitNumber == 0).IsSet.Should().BeTrue();
        cells.Single(c => c.BitNumber == 2).IsSet.Should().BeTrue();
        cells.Single(c => c.BitNumber == 1).IsSet.Should().BeFalse();
        vm.VisibleSignals.Single().LiveValue.Should().Be("5");
    }

    [Fact]
    public void Live_Without_New_Frame_Keeps_Dashes()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, SimpleMessage());
        vm.SelectedMessage = vm.Messages.Single();
        vm.StopLiveTimerForTest();

        vm.RefreshLiveLayoutForTest();

        vm.VisibleSignals.Single().LiveValue.Should().Be("—");
        vm.LayoutRows[0].Cells.Should().OnlyContain(c => c.IsSet == null);
    }

    [Fact]
    public void Short_Frame_Leaves_Bytes_Beyond_Payload_Without_Live_State()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        var msg = new Message(0x100, "Two", 8, "ECU", new List<Signal>
        {
            Sig("Low", 0, 8),
            Sig("High", 8, 8),
        }, false, null);
        RaiseLoaded(svc, msg);
        vm.SelectedMessage = vm.Messages.Single();
        vm.StopLiveTimerForTest();

        slot.Publish(0x100, new byte[] { 0xFF });
        vm.RefreshLiveLayoutForTest();

        vm.VisibleSignals.Single(s => s.Name == "Low").LiveValue.Should().Be("255");
        vm.VisibleSignals.Single(s => s.Name == "High").LiveValue.Should()
            .Be("—", "载荷只有 1 字节，不能把缺失字节当 0 解码");
        vm.LayoutRows[1].Cells.Should().OnlyContain(c => c.IsSet == null);
    }

    [Fact]
    public void Mux_Mismatch_Shows_Dash_For_Muxed_Signals()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, MuxMessage());
        vm.SelectedMessage = vm.Messages.Single();   // 选中 mux=0 分支
        vm.StopLiveTimerForTest();

        // 帧里 mux=1（Mode 低 4 位 = 1），mux=0 的 A 在本帧不生效
        slot.Publish(0x100, new byte[] { 0x01, 0x2A, 0x63 });
        vm.RefreshLiveLayoutForTest();

        vm.VisibleSignals.Single(s => s.Name == "Mode").LiveValue.Should().Be("1");
        vm.VisibleSignals.Single(s => s.Name == "A").LiveValue.Should().Be("—");
        vm.LayoutRows[1].Cells.Single(c => c.BitNumber == 1).IsSet.Should()
            .BeTrue("位图仍按载荷位点亮，与 mux 分支无关");
    }

    [Fact]
    public void Switching_Mux_Reapplies_Live_Values_For_New_Branch()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, MuxMessage());
        vm.SelectedMessage = vm.Messages.Single();   // mux=0
        vm.StopLiveTimerForTest();
        slot.Publish(0x100, new byte[] { 0x01, 0x2A, 0x63 });
        vm.RefreshLiveLayoutForTest();

        vm.SelectedMuxValue = 1;   // 切到 B 分支
        vm.StopLiveTimerForTest();

        vm.VisibleSignals.Select(s => s.Name).Should().BeEquivalentTo("Mode", "B");
        vm.VisibleSignals.Single(s => s.Name == "B").LiveValue.Should()
            .Be("99", "切换 mux 后必须立即按新分支重解，不能等下一次 tick");
    }

    [Fact]
    public void Fd_Sixty_Four_Byte_Payload_Lights_Last_Byte()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        var msg = new Message(0x100, "Fd", 64, "ECU", new List<Signal>
        {
            Sig("Top", 504, 8),
        }, false, null);
        RaiseLoaded(svc, msg);
        vm.SelectedMessage = vm.Messages.Single();
        vm.StopLiveTimerForTest();

        var payload = new byte[64];
        payload[63] = 0x80;
        slot.Publish(0x100, payload);
        vm.RefreshLiveLayoutForTest();

        vm.LayoutRows.Should().HaveCount(64);
        vm.LayoutRows[63].Cells.Single(c => c.BitNumber == 7).IsSet.Should().BeTrue();
        vm.VisibleSignals.Single().LiveValue.Should().Be("128");
    }

    [Fact]
    public void Deselecting_Message_Clears_Live_Target()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, SimpleMessage());
        vm.SelectedMessage = vm.Messages.Single();
        vm.StopLiveTimerForTest();
        slot.Publish(0x100, new byte[] { 0xFF });

        vm.SelectedMessage = null;

        slot.TryRead(0x100, out _).Should().BeFalse("取消选中后后台不再为该消息保留载荷");
    }

    [Fact]
    public void Dbc_Reload_Clears_Live_Target()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, SimpleMessage());
        vm.SelectedMessage = vm.Messages.Single();
        vm.StopLiveTimerForTest();
        slot.Publish(0x100, new byte[] { 0xFF });

        RaiseLoaded(svc, SimpleMessage());

        vm.SelectedMessage.Should().BeNull();
        slot.TryRead(0x100, out _).Should().BeFalse();
    }
```

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter FullyQualifiedName~DbcLayoutViewModelTests -v minimal`

Expected: 编译失败 —— 构造函数没有 `liveSlot`，`StopLiveTimerForTest` / `RefreshLiveLayoutForTest` 不存在。

- [ ] **Step 2: 构造函数与生命周期**

`DbcViewModel.cs` 顶部加 `using Microsoft.Extensions.Hosting;`，类声明改为：

```csharp
public sealed partial class DbcViewModel : ObservableObject, IHostedService, IDisposable
```

构造函数签名与末尾：

```csharp
    public DbcViewModel(DbcService svc, SignalViewModel signals, ILogger<DbcViewModel> logger,
                        IFileDialogService? fileDialog = null,
                        IShellLauncher? shellLauncher = null,
                        Func<string>? logDirectoryProvider = null,
                        LiveFrameSlot? liveSlot = null)
    {
        // ...原有赋值不动...
        _svc.DbcLoaded += OnLoaded;
        _svc.LoadFailed += OnLoadFailed;

        // 二期：33ms 实时刷新 Timer，初始不启动；选中消息时由 SyncLive 启动。
        _liveSlot = liveSlot;
        if (_liveSlot is not null)
        {
            _liveTimer = new System.Threading.Timer(
                _ => OnLiveTick(), state: null, Timeout.Infinite, Timeout.Infinite);
        }
    }
```

- [ ] **Step 3: 新建实时流程**

创建 `src/PeakCan.Host.App/ViewModels/DbcViewModel/LiveFlow.partial.cs`：

```csharp
using System.Globalization;
using Microsoft.Extensions.Hosting;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.App.Services;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class DbcViewModel
{
    // Flow: live byte-layout lighting (phase 2).
    //
    // Data path: DbcDecodeBackgroundService publishes the latest raw payload of the
    // selected message into LiveFrameSlot (single slot, copy-on-publish). This VM
    // re-decodes on a 33 ms System.Threading.Timer tick, honoring the current mux
    // branch, and applies per-cell bit state + per-signal values on the UI thread
    // via RunOnUiPost. No selection → the timer is stopped: the bus idle path does
    // no per-frame work.
    //
    // Cross-flow callers (plain calls via partial-class visibility):
    //   - UpdateLayout (LayoutFlow) -> SyncLive
    //   - SyncLive -> RefreshLiveLayout / _liveSlot.SetTarget
    //   - OnLiveTick -> RefreshLiveLayout
    //   - Dispose (host shutdown) -> _liveTimer.Dispose

    private const int LiveRefreshIntervalMs = 33;   // ~30 Hz, same cadence as SignalViewModel

    private readonly LiveFrameSlot? _liveSlot;
    private readonly System.Threading.Timer? _liveTimer;
    private readonly object _liveGate = new();
    private long _lastLiveSequence = -1;
    private bool _liveDisposed;

    /// <summary>测试接缝：同步跑一次实时刷新（xunit 没有 WPF 调度器，不能等 Timer）。</summary>
    internal void RefreshLiveLayoutForTest() => RefreshLiveLayout(force: true);

    /// <summary>测试接缝：停掉自动 tick，避免测试线程与线程池回调并发改行集合。</summary>
    internal void StopLiveTimerForTest() => _liveTimer?.Change(Timeout.Infinite, Timeout.Infinite);

    /// <summary>组合根测试用：确认 DI 把同一个单槽注入了 VM。</summary>
    internal LiveFrameSlot? LiveSlot => _liveSlot;

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        _liveDisposed = true;
        _liveTimer?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 选中消息变化后同步实时通道：选中有帧消息 → 记目标 ID、立刷一次、启动 33ms tick；
    /// 无选中 → 停 tick、清目标。切换消息不得沿用上一条消息的载荷。
    /// </summary>
    private void SyncLive()
    {
        if (_liveSlot is null || _liveDisposed) return;

        if (SelectedMessage?.RawMessage is not { } message)
        {
            _liveSlot.SetTarget(null);
            _liveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            lock (_liveGate) { _lastLiveSequence = -1; }
            return;
        }

        _liveSlot.SetTarget(message.Id);
        lock (_liveGate) { _lastLiveSequence = -1; }
        RefreshLiveLayout(force: true);
        _liveTimer?.Change(LiveRefreshIntervalMs, LiveRefreshIntervalMs);
    }

    private void OnLiveTick() => ((Action)(() => RefreshLiveLayout(force: false))).RunOnUiPost();

    /// <summary>
    /// 把单槽里的最新载荷投影到当前布局：格子位状态 + 信号表当前值。
    /// <paramref name="force"/> = true 时忽略序号（选中 / mux 切换后必须立刻重画）。
    /// 在 UI 线程执行（选中路径天然在 UI 线程，tick 路径经 RunOnUiPost）。
    /// </summary>
    private void RefreshLiveLayout(bool force)
    {
        // 测试线程可能同时从线程池 tick；锁保证「最后应用的一定是最新载荷」。
        lock (_liveGate)
        {
            if (SelectedMessage?.RawMessage is not { } message) return;
            if (_liveSlot is null || !_liveSlot.TryRead(message.Id, out var frame)) return;
            if (!force && frame.Sequence == _lastLiveSequence) return;
            _lastLiveSequence = frame.Sequence;

            var payload = frame.Data.Span;
            var frameMux = DecodeFrameMux(message, payload);

            foreach (var row in LayoutRows)
            {
                foreach (var cell in row.Cells)
                {
                    if (cell.OwnerSignal is null)
                    {
                        cell.IsSet = null;
                        continue;
                    }

                    cell.IsSet = row.ByteIndex < payload.Length
                        ? ((payload[row.ByteIndex] >> cell.BitNumber) & 1) == 1
                        : null;
                }
            }

            foreach (var row in VisibleSignals)
                row.LiveValue = FormatLiveValue(row.Signal, payload, frameMux);
        }
    }

    /// <summary>帧里实际的 mux 值；消息无复用器 / 当前载荷解不出时为 null。</summary>
    private static ushort? DecodeFrameMux(Message message, ReadOnlySpan<byte> payload)
    {
        if (message.MultiplexorSignalIndex is not { } index
            || index < 0 || index >= message.Signals.Count)
        {
            return null;
        }

        var muxSignal = message.Signals[index];
        return CanDecode(muxSignal, payload)
            ? (ushort)SignalDecoder.DecodeRaw(payload, muxSignal)
            : null;
    }

    /// <summary>
    /// 单信号当前值。载荷不够覆盖信号全部位时返回“—”（不能像 Decoder 那样把缺字节
    /// 当 0 解码——那会把“没收到数据”显示成 0）；muxed 信号且帧 mux 与选中分支不符
    /// 时也返回“—”。
    /// </summary>
    private static string FormatLiveValue(Signal signal, ReadOnlySpan<byte> payload, ushort? frameMux)
    {
        if (!CanDecode(signal, payload)) return "—";

        if (signal.IsMultiplexed && signal.MultiplexValue is { } expected
            && (frameMux is null || frameMux.Value != expected))
        {
            return "—";
        }

        var physical = SignalDecoder.Decode(payload, signal);
        return physical.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>信号的全部位是否落在当前帧载荷内（Decoder 对缺失字节静默补 0，这里必须先挡）。</summary>
    private static bool CanDecode(Signal signal, ReadOnlySpan<byte> payload)
    {
        if (signal.Length == 0 || signal.Length > 64) return false;
        var lastBit = signal.StartBit + signal.Length - 1;
        return lastBit >> 3 < payload.Length;
    }
}
```

- [ ] **Step 4: 接入布局重算**

`LayoutFlow.partial.cs` 的 `UpdateLayout()`：

空选中分支：

```csharp
        if (SelectedMessage?.RawMessage is not { } message)
        {
            LayoutSnapshot = null;
            SyncLive();
            return;
        }
```

末尾 `LayoutRows.Add(...)` 循环之后：

```csharp
        SyncLive();
```

- [ ] **Step 5: 注册为 hosted service（Timer 随宿主释放）**

`ViewModelsBatch2Flow.cs` 在 `services.AddSingleton<DbcViewModel>();` 之后加：

```csharp
        // 二期：33ms 实时刷新 Timer 持有在 VM 内，注册为 hosted service 让宿主退出时 Dispose。
        services.AddHostedService(sp => sp.GetRequiredService<DbcViewModel>());
```

- [ ] **Step 6: 跑测试确认 GREEN**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter FullyQualifiedName~DbcLayoutViewModelTests -v minimal`

Expected: 全部通过（既有 14 个 + 新增 9 个）。

- [ ] **Step 7: 提交（仅当用户明确要求 commit 时执行）**

```bash
git add src/PeakCan.Host.App/ViewModels/DbcViewModel/LiveFlow.partial.cs src/PeakCan.Host.App/ViewModels/DbcViewModel.cs src/PeakCan.Host.App/ViewModels/DbcViewModel/LayoutFlow.partial.cs src/PeakCan.Host.App/Composition/AppHostBuilder/ViewModelsBatch2Flow.cs tests/PeakCan.Host.App.Tests/ViewModels/DbcLayoutViewModelTests.cs
git commit -m "feat(dbc): add 33ms live refresh for selected message layout"
```

---

### Task 5: XAML 实时渲染

**Files:**
- Modify: `src/PeakCan.Host.App/Views/ByteLayoutView.xaml:45-53`
- Modify: `src/PeakCan.Host.App/Views/DbcView.xaml:87-117`
- Test: `tests/PeakCan.Host.App.Tests/Views/ByteLayoutViewLayoutTests.cs`
- Test: `tests/PeakCan.Host.App.Tests/Views/DbcViewLayoutTests.cs`

**Interfaces:**
- Consumes: `ByteLayoutCell.IsSet`（Task 3）、`DbcSignalRow.LiveValue`（Task 3）。
- Produces: 无（纯视图）。

- [ ] **Step 1: 写失败测试**

`ByteLayoutViewLayoutTests` 追加：

```csharp
    [Fact]
    public void Zero_Bits_Are_Dimmed_Through_An_IsSet_Trigger()
    {
        var doc = Load();

        var border = doc.Descendants(P + "Border").Single(
            b => ((string?)b.Attribute("Focusable")) == "True");

        var trigger = border.Descendants(P + "DataTrigger")
            .SingleOrDefault(t => ((string?)t.Attribute("Binding"))?.Contains(
                "IsSet", StringComparison.Ordinal) == true);

        trigger.Should().NotBeNull("0 位必须通过 IsSet 触发变淡");
        ((string?)trigger!.Attribute("Value")).Should().Be("False");
        trigger.Descendants(P + "Setter").Should()
            .Contain(s => ((string?)s.Attribute("Property")) == "Opacity");
    }
```

`DbcViewLayoutTests` 追加：

```csharp
    [Fact]
    public void Signal_Table_Shows_Live_Value_Column()
    {
        var rowDetails = DbcView()
            .Descendants(P + "DataGrid.RowDetailsTemplate").Single();

        rowDetails.Descendants(P + "TextBlock").Should()
            .Contain(t => (((string?)t.Attribute("Text")) ?? "").Contains(
                "LiveValue", StringComparison.Ordinal),
                "信号表必须显示实时值");
    }
```

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~ByteLayoutViewLayoutTests|FullyQualifiedName~DbcViewLayoutTests" -v minimal`

Expected: 两个新用例失败（XAML 还没有 IsSet 触发器 / LiveValue 绑定）。

- [ ] **Step 2: 位图格子变淡**

`ByteLayoutView.xaml` 的格子 `Border` 内加 Style（在 `TextBlock` 之前）：

```xml
                                                <Border.Style>
                                                    <Style TargetType="Border">
                                                        <Setter Property="Opacity" Value="1" />
                                                        <Style.Triggers>
                                                            <DataTrigger Binding="{Binding IsSet}"
                                                                         Value="False">
                                                                <Setter Property="Opacity" Value="0.28" />
                                                            </DataTrigger>
                                                        </Style.Triggers>
                                                    </Style>
                                                </Border.Style>
```

- [ ] **Step 3: 信号表加表头与“当前值”列**

`DbcView.xaml` 的 `ItemsControl ItemsSource="{Binding VisibleSignals}"` 之前加表头：

```xml
                                <Grid Margin="16,4,8,2">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="18" />
                                        <ColumnDefinition Width="160" />
                                        <ColumnDefinition Width="120" />
                                        <ColumnDefinition Width="110" />
                                        <ColumnDefinition Width="50" />
                                        <ColumnDefinition Width="90" />
                                        <ColumnDefinition Width="160" />
                                        <ColumnDefinition Width="*" />
                                    </Grid.ColumnDefinitions>
                                    <TextBlock Grid.Column="1" Text="名称" FontSize="10"
                                               Foreground="{StaticResource TextSecondary}" />
                                    <TextBlock Grid.Column="2" Text="位布局" FontSize="10"
                                               Foreground="{StaticResource TextSecondary}" />
                                    <TextBlock Grid.Column="3" Text="系数 / 偏移" FontSize="10"
                                               Foreground="{StaticResource TextSecondary}" />
                                    <TextBlock Grid.Column="4" Text="单位" FontSize="10"
                                               Foreground="{StaticResource TextSecondary}" />
                                    <TextBlock Grid.Column="5" Text="当前值" FontSize="10"
                                               Foreground="{StaticResource TextSecondary}" />
                                    <TextBlock Grid.Column="6" Text="注释" FontSize="10"
                                               Foreground="{StaticResource TextSecondary}" />
                                    <TextBlock Grid.Column="7" Text="诊断" FontSize="10"
                                               Foreground="{StaticResource TextSecondary}" />
                                </Grid>
```

数据行的列定义改为 8 列（在“单位”后插入 90 宽）：

```xml
                                                <ColumnDefinition Width="18" />
                                                <ColumnDefinition Width="160" />
                                                <ColumnDefinition Width="120" />
                                                <ColumnDefinition Width="110" />
                                                <ColumnDefinition Width="50" />
                                                <ColumnDefinition Width="90" />
                                                <ColumnDefinition Width="160" />
                                                <ColumnDefinition Width="*" />
```

“单位”之后的列整体右移一列，并插入实时值：

```xml
                                            <TextBlock Grid.Column="5" Text="{Binding LiveValue}"
                                                       FontFamily="Consolas" FontSize="11" />
                                            <TextBlock Grid.Column="6" Text="{Binding Comment}"
                                                       FontSize="11" Foreground="{StaticResource TextSecondary}"
                                                       TextTrimming="CharacterEllipsis" />
                                            <TextBlock Grid.Column="7" Text="{Binding DiagnosticText}"
                                                       FontSize="11" Foreground="{StaticResource WarnText}"
                                                       TextWrapping="Wrap" />
```

- [ ] **Step 4: 跑测试确认 GREEN**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~ByteLayoutViewLayoutTests|FullyQualifiedName~DbcViewLayoutTests" -v minimal`

Expected: 全部通过。

- [ ] **Step 5: 提交（仅当用户明确要求 commit 时执行）**

```bash
git add src/PeakCan.Host.App/Views/ByteLayoutView.xaml src/PeakCan.Host.App/Views/DbcView.xaml tests/PeakCan.Host.App.Tests/Views/ByteLayoutViewLayoutTests.cs tests/PeakCan.Host.App.Tests/Views/DbcViewLayoutTests.cs
git commit -m "feat(dbc): render live bit state and signal values"
```

---

### Task 6: DI 组合断言、spec 同步与整体验证

**Files:**
- Modify: `tests/PeakCan.Host.App.Tests/Composition/AppHostBuilderTests.cs`
- Modify: `docs/superpowers/specs/2026-11-25-dbc-byte-layout-design.md`（§3、§6）

**Interfaces:**
- Consumes: `DbcViewModel.LiveSlot`（Task 4）。
- Produces: 无。

- [ ] **Step 1: 写组合根断言**

`AppHostBuilderTests` 追加：

```csharp
    [Fact]
    public void Build_Wires_DbcViewModel_To_The_Decode_Live_Slot()
    {
        using var host = new AppHostBuilder().Build();

        var slot = host.Services.GetRequiredService<LiveFrameSlot>();
        host.Services.GetRequiredService<DbcViewModel>().LiveSlot.Should().BeSameAs(
            slot, "二期实时刷新必须读到后台 worker 写入的同一个单槽");
    }
```

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter FullyQualifiedName~AppHostBuilderTests -v minimal`

Expected: 通过（Task 2/4 已接线）。若失败，说明 DI 漏了 `LiveFrameSlot` 注入——修复而不是跳过。

- [ ] **Step 2: 同步 spec 措辞**

`docs/superpowers/specs/2026-11-25-dbc-byte-layout-design.md` §3 的二期条目改为：

```markdown
**二期（2026-11-26 已确认，实施计划见 `docs/superpowers/plans/2026-11-26-dbc-byte-layout-phase2.md`）**：实时点亮。仅当前选中消息走 33ms 节流更新背景/数值，其余消息不动；无选中消息时停止节流。位图中当前帧为 0 的信号位变淡，信号表新增“当前值”列；帧内 mux 值与选中分支不符时 muxed 信号显示“—”。
```

§6 最后一条改为：

```markdown
- 二期预算上限：30Hz × 当前选中消息 ≤512 格的属性赋值，复用 `RunOnUiPost` 模式。原始帧由 `DbcDecodeBackgroundService` 写入**单槽缓存** `LiveFrameSlot`：VM 在选中变化时设置目标消息 ID，worker 只拷贝这条消息的最新一帧完整载荷（经典 8 字节 / FD 最多 64 字节，不是固定 8 字节）；无选中时不拷贝。单槽避免高帧率总线上对无关消息的逐帧分配。
```

- [ ] **Step 3: 定向回归 + 构建**

Run:

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~LiveFrameSlotTests|FullyQualifiedName~DbcDecodeBackgroundServiceTests|FullyQualifiedName~DbcLayoutViewModelTests|FullyQualifiedName~ByteLayoutViewLayoutTests|FullyQualifiedName~DbcViewLayoutTests|FullyQualifiedName~AppHostBuilderTests" -v minimal
dotnet build PeakCan.Host.slnx -c Debug
```

Expected: 定向测试全绿；构建 0 warning / 0 error。**不要跑全量测试。**

- [ ] **Step 4: 人工验收（需用户执行）**

- 连上总线 / 回放数据，选中一条有帧的消息：位图 0 位变淡、1 位保持原色；“当前值”列每帧更新。
- 切到 mux 另一分支：值列立即更新；帧 mux 不符时显示“—”。
- 取消选中 / 切到无帧消息：位图回到静态配色，值列“—”。
- FD 消息 64 字节：第 63 字节同样能点亮。
- 总线空闲时不选消息：CPU 不应有持续 30Hz 唤醒（Timer 已停）。

- [ ] **Step 5: 提交（仅当用户明确要求 commit 时执行）**

```bash
git add tests/PeakCan.Host.App.Tests/Composition/AppHostBuilderTests.cs docs/superpowers/specs/2026-11-25-dbc-byte-layout-design.md
git commit -m "test(dbc): pin live slot DI wiring; sync phase-2 spec"
```

---

## Self-Review

- **Spec 覆盖**：§3 二期（仅选中消息 33ms、其余不动、无选中停止）、§6 二期预算（≤512 格属性赋值、RunOnUiPost、原始载荷上限 64 字节）均有对应任务；单槽是对 §6 字面的偏离，已在 Task 6 同步 spec。
- **占位符扫描**：无 TBD / TODO / “类似 Task N”。
- **类型一致性**：`LiveFrameSlot.TryRead(uint, out LiveFramePayload)` 在 Task 1/2/4 一致；`IsSet`/`LiveValue` 在 Task 3/4/5 一致；`SyncLive`/`RefreshLiveLayout` 在 Task 4 内闭合。
- **Review Focus**：5 类输入均有具名测试（见上）。
