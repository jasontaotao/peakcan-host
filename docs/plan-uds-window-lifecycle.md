# Plan: UDS 窗口/单例生命周期改造

## 背景与证据

### 当前架构事实
- **panel VM 全部是 DI 单例**: `AppHostBuilder.cs:261-288` —
  `SessionPanelViewModel` / `DidPanelViewModel` / `RoutinePanelViewModel` /
  `DtcPanelViewModel` / `FlashPanelViewModel` / `UdsViewModel` / `OdxImportViewModel`
  全部 `AddSingleton`；`UdsClient` / `IsoTpLayer` / 各 Database 也是单例。
- **AppShell 持单例 VM, 窗口重建复用同一 VM**: `AppShellViewModel.cs:88,291`
  `_udsViewModel` 单例注入；`ViewSwitchFlow.cs:114` 每次 `ShowUds` 的 factory 都是
  `new UdsWindow { DataContext = _udsViewModel }` ——窗口是 per-open 实例, VM 是进程单例。
- **v3.11.3 缓存契约**: `ViewSwitcher.cs:161` `ShowWindow` 给 window 挂 `Closed` → null cache;
  所以"关闭 UdsWindow → 下次 ShowUds 走 factory 重建新窗口, 绑定同一单例 VM"。
  测试 `UdsWindowTests.cs:177-209` 锁定: 第二次 ShowUds 复用同一缓存窗口(未关闭前),
  `CurrentView == null`(UDS 是 Window 不是 tab)。

### 根本缺陷(本次要修)
`UdsWindow.xaml.cs:35-39` 在 `Unloaded` 调用 `Session.Dispose()` + `Flash.Dispose()`:

1. **Flash 永久死亡**: `FlashPanelViewModel.cs:249-255` `Dispose()` 置
   `_disposed = true`; 之后 `StartAsync` 第 78 行
   `ObjectDisposedException.ThrowIf(_disposed, this)` 永久拒绝,
   `CanStart() => !_disposed`(`FlashPanelViewModel.cs:196`)使 Start 按钮**永久灰掉**。
   → **第一次关闭 UdsWindow 后, 再次打开, Flash 面板不可用**, 且无任何复活路径。
2. **Session 状态不同步**: `SessionPanelViewModel.cs:168-179` `Dispose()` 把
   `TesterPresentActive=false` + cancel+dispose `_testerPresentCts` 并清 null。
   该 VM 也是单例, 第二次开窗时 `TesterPresentActive` 已被强制 false ——
   若用户关窗前开着 TesterPresent, 关窗即停(可能符合预期), 但窗口重开后复选框
   binding(`IsChecked={Binding TesterPresentActive, Mode=OneWay}`, `UdsWindow.xaml:25`)
   显示 false 且 loop 已停; 用户需手动点击恢复。语义可接受但需明确, 不要 Dispose。
3. `_udsWindow` cache 被 Closed null(`ViewSwitcher.cs:161`)→ 重开新窗 + 同一 VM,
   但 Unloaded 已把单例 VM 的 Flash 永久废掉: **单例生命周期被错误耦合到窗口实例**。

### v3.11.3 必须保持兼容的行为
- `ShowUdsCommand` 打开/复用 `UdsWindow`; `CurrentView` 始终 null(不是 tab)。
- 未关闭前多次点击复用同一缓存窗口(`UdsWindowTests.cs:200` `BeSameAs`)。
- OutputLog 的彩色 RichTextBox AppendLog + Clear(Reset) 镜像逻辑不变
  (`UdsWindow.xaml.cs:51-93`)。
- OdxImport / DID / Routine / DTC / Flash 各 panel 的 RelayCommands 不变。

## 目标形态(选定路线 3 — 混合)

**保留 panel VM 单例**; 把"UdsWindow 关闭"语义从 **"Dispose 单例 VM"**
降级为 **"按窗口停止在途操作 + 释放本窗独占的临时资源"**,
**移除一次性 `_disposed` 永久门**, 让窗口重开完全可用。

理由:
- 单例长期持有 `UdsClient`/`IsoTpLayer`/Database 本就是设计意图
  (跨窗口共享 ECU 会话、DID/Routine 列表、日志);
- 出问题的只有 Flash 的 `_disposed` 永久门 + Session 的"关窗即 Dispose"副作用;
- Flash 的真正资源(secondary stack = Detach→Client→IsoTp→DllKey)本就是
  **per-run** 生命周期(`StartAsync` 的 `finally` 已 `stack?.Dispose()`,
  `FlashPanelViewModel.cs:174-183`), 即 stack 不应跟着窗口走, 而应跟着单次 Start 走。
  Unloaded 只需"若 flash 在途则停掉"即可, 不碰 `_disposed`。
- 改动面可控, 不动 DI 注册, 不破坏 v3.11.3 测试契约。

## 改造任务(TDD: 先红后绿)

### T1 — FlashPanelViewModel: 移除一次性 `_disposed` 永久门

**红测**(新增于 `tests/.../ViewModels/Uds/FlashPipeline/FlashPanelViewModelTests.cs`):
- `Flash_after_Dispose_can_Start_again_when_not_in_flight`: 构造 VM(NoOp 工厂),
  调 `Dispose()`, 再调 `StartCommand` 或 `StartAsync` —— 期望: 按现有拒绝
  (因 NoOp 工厂 Build 抛 `NotImplementedException`)走 `Failed` 状态,
  **而非 `ObjectDisposedException`**;
- `StartCommand_CanExecute_is_true_after_Dispose_when_idle`: `CanStart()` 应 true。
- `Stop_command_is_noop_after_Dispose`: Stop 幂等不抛。

**绿改** (`FlashPanelViewModel.cs`):
- 保留 `IDisposable` 实现(进程退出/App 关闭时仍要兜底停 flash), 但语义改为
  **"停止在途 run + 释放当前 run 的 CTS"**, **不置 `_disposed` 永久门**:
  - 移除 `_disposed` 字段与 `ObjectDisposedException.ThrowIf(_disposed, this)`(L78);
  - `CanStart() => !IsFlashing`(`FlashPanelViewModel.cs:196`, 去掉 `&& !_disposed`);
  - `CanStop() => IsFlashing`(L213, 去掉 `&& !_disposed`);
  - `Dispose()` 只做 `_runCts?.Cancel(); _runCts?.Dispose(); _runCts = null;`
    (对应"停掉在途"), 不再阻断后续 Start。
- 注意: 若 `StartAsync` 已 in-flight, `Dispose` 通过 cancel 触发 `OperationCanceledException`
  → finally 内 `stack?.Dispose()` 正常按 run 拆栈; 行为与现状一致的"中途取消", 安全。

### T2 — SessionPanelViewModel: 关窗为"停 TesterPresent", 不 Dispose

**红测**(新增于 `tests/.../ViewModels/Uds/SessionPanelViewModelTests.cs`):
- `StopTesterPresent_after_window_unload_keeps_VM_reusable`: 调用一个新的公共
  "停止/清理窗口级资源"方法后, `TesterPresentActive == false`, 且再次
  `ToggleTesterPresentCommand` 可重新启动 loop(不抛、状态翻 true)。
- `Dispose_is_idempotent_and_does_not_break_reuse`: 现有 Dispose 幂等性回归。

**绿改** (`SessionPanelViewModel.cs`):
- 新增公共实例方法 `StopForWindowClose()`(语义: 停掉 TesterPresent loop, 但保留
  VM 可重用)。实现 = 现有 `Dispose()` body 中"停 TesterPresent"部分
  (TesterPresentActive=false + cancel CTS + dispose + null)。
- `Dispose()` 改为调用 `StopForWindowClose()` + `GC.SuppressFinalize`
  (进程关闭路径仍走 Dispose, 等价语义但幂等)。
- 关键: 不清 `_log` / `_syncContext` / `_udsClient` 引用 —— 保持单例可重用。

### T3 — UdsWindow.xaml.cs: Unloaded 改"窗口级停止"语义

**红测**(新增于现有 `UdsWindowTests.cs`):
- `closing_UdsWindow_stops_in_flight_flash_and_keeps_vm_reusable`:
  开窗 → 触发关闭 → 重开新窗 → Flash.StartCommand `CanExecute` 应为 true
  (回归 T1 + 证明窗口重开后 Flash 可用)。
- 保持 `ShowUdsCommand_Opens_Cached_UdsWindow`(现有测试)仍绿 ——
  证明 v3.11.3 缓存/复用契约未被破坏。

**绿改** (`UdsWindow.xaml.cs:35-39`):
```csharp
Unloaded += (_, _) =>
{
    DetachLog();
    udsVm.Session.StopForWindowClose();  // 停 TesterPresent, 不废单例
    udsVm.Flash.StopForWindowClose();   // 见 T4
};
```
- 不再调 `Session.Dispose()` / `Flash.Dispose()`(那是进程级一次性语义)。
- `DetachLog()` 保持(解除 OutputLog 订阅, 防窗口关闭后 RichTextBox 死挂)。

### T4 — FlashPanelViewModel: 加 `StopForWindowClose()`

与 T2 对称: Flash 也需"窗口级停止"入口。
- 新增 `public void StopForWindowClose()` = 现 `Dispose()` 中"cancel 在途 run"
  部分(`_runCts?.Cancel(); _runCts?.Dispose(); _runCts = null;`),
  **不设任何永久门**。
- `Dispose()`(进程级一次性兜底) 调 `StopForWindowClose()` ——
  保留 `IDisposable` 供 AppShutdown 调用, 但语义与窗口解耦。
- T1 的改动其实已让 Dispose 变幂等可重入, `StopForWindowClose` 可直接复用
  `Dispose()` 的可逆 body —— 决策点见下"待定"。

### T5 — App 关闭路径: 进程级 Dispose 安全网(已查证 — 无需新增钩子)

**查证结论**: `App.xaml.cs:175-194` `OnExit` 的 finally 里 `_host.Dispose()`
由 `Microsoft.Extensions.Hosting` 负责 Dispose 所有注册为 singleton 的
`IDisposable` 实例。`SessionPanelViewModel`(`SessionPanelViewModel.cs:14 implements IDisposable`)
+ `FlashPanelViewModel`(`FlashPanelViewModel.cs:26 implements IDisposable`)均已
`AddSingleton`(`AppHostBuilder.cs:261,284`)→ **进程退出时 DI 容器已自动级联 Dispose
这两个单例**, native 句柄(NativeLibrary.Load 的 OEM DLL key)随 Flash 在途 stack
被 cancel→finally 拆栈(Detach→Client→IsoTp→DllKey)释放。

**因此 T5 无需新增钩子**: 把 `Dispose()` 改成幂等可重入(不设永久门)后,
- 进程级 Dispose 仍能正确停掉在途 flash 与 TesterPresent(T1+T2 让 Dispose 只做"停",
  行为与现状一致但去掉永久门);
- 窗口级 Unloaded 改调 `StopForWindowClose`(window-scoped 停止, 重开可用)。

**红测**(回归保险, 加在 `FlashPanelViewModelTests`):
- `Dispose_is_idempotent_and_only_stops_in_flight_run`: 调 `Dispose()` 两次不抛,
  仍是 `StopForWindowClose` 语义(no 永久门)。
- `Dispose_after_in_flight_cancel_tears_down_stack_via_finally`: 在途 run + Dispose
  → stack 收到 DetachFromRouter(用 spy `ISecondaryFlashStack` 断言)。

注: 现状 `App.OnExit` 已是唯一容忍 teardown 异常的调用点(`App.xaml.cs:169`),
所以即使某个 panel Dispose 抛, 进程也能安全退出 —— 与旧设计等价。

## 待定(执行时即时决策, 不阻塞 plan)
- D1: `FlashPanelViewModel.StopForWindowClose` 是否就等于放宽后的 `Dispose()`
  (即 T1+T4 合并为"让 Dispose 本身幂等可重入, Unloaded 直接调 Dispose ")?
  倾向**是** —— 减少 API 表面, 单一停止入口; 但需在文档注释里说清
  "Dispose 现在只停当前 run, 不永久废 VM, 进程退出可多次安全调用"。
  执行时按此实现, 若红测发现语义歧义再拆双方法。
- D2: Session 的 `StopForWindowClose` 同理是否就等于 `Dispose` body?
  Session 的 Dispose 本就是幂等且不设永久门, **可让 Unloaded 直接调
  `Session.Dispose()`** —— 但术语误导(Dispose 暗示一次性)。倾向命名
  `StopForWindowClose`(语义清晰)  + `Dispose()` 委托之。与 Flash 对称。

## 不改范围(明确)
- DI 注册全部不动(保持单例)。
- `ViewSwitcher.ShowWindow` 的 closed→null cache 机制不动(v3.11.3 兼容)。
- `UdsViewModel` orchestrator 不动(它不持有 dispose 职责)。
- OutputLog 镜像 + Clear(Reset) 逻辑不动。
- 各 panel 的 RelayCommands 行为不动。

## 验证闭环
1. 红测先行: 每个 T 的测试先写且失败。
2. 绿改使其通过。
3. 全量 `dotnet test` —— 现有 `UdsWindowTests` + `FlashPanelViewModelTests` +
   `SessionPanelViewModelTests` + `AppShellViewModelTests` 全绿
   (证明 v3.11.3 + 现有 flash/session 契约未回归)。
4. `dotnet build src/PeakCan.Host.Infrastructure/` 或对应 App 项目 0 error 0 warning。
5. 代码审 `code-reviewer` agent。

## 风险
- **App 关闭 native 句柄释放**(T5): 这是移除 Unload-Dispose 的**唯一回退风险**。
  必须先查证 App 现有关闭钩子; 若无, 补进程级 dispose。
- 复用既有 STA 测试夹具(`WpfAppTestCollection`)避免 dispatcher 死锁。
- Flash 的 `_runCts?.Dispose()` 在 in-flight cancel 后再次 Start 会先 `_runCts?.Dispose()`
  再 new(`FlashPanelViewModel.cs:131-132`), 安全。
