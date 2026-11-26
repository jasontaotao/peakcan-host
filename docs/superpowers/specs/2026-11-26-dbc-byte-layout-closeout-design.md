# DBC 字节位图收口期设计（性能、文档与遗留 minor）

日期：2026-11-26
状态：待评审
上游：`docs/superpowers/specs/2026-11-25-dbc-byte-layout-design.md`（一期静态布局 + 二期实时点亮均已交付，提交 `60decfc9`）

## 1. 背景与目标

一期（静态位图）与二期（33ms 实时点亮）已实现并提交，但功能 spec 还有几项没有收口：

- §6 的性能预算（快照 ≤5ms、首次实现 ≤30ms）从未实测；
- §4 / §5.6 / 文首状态与实现不一致；
- 二期终审留下 6 条 deferred minor，其中两条会影响真实使用（`Clear()` 未接线、切标签页后定时器不停）。

本期的目标不是加功能，而是让已交付的东西**可验证、文档可信、遗留项有明确处置**。

## 2. 范围

**做：**

1. 性能预算落成自动化守卫 + 人工测量清单。
2. 同步功能 spec 中与实现不符的三处文字。
3. 处置二期终审的 6 条 minor（见 §3.3）。
4. 输出人工验收清单（性能手感 + 交互），由用户执行。

**不做：**

- “当前值”接 DBC `VAL_` 表（值表可读化）——新功能，另立 spec。
- FD 512 格的渲染优化、全消息逐帧染色——现 spec §3 明确不做，维持。
- 不修改 `peakcan-hil-core`，不新增 NuGet。

## 3. 设计

### 3.1 性能预算落地

spec §6 的两个数字都是“中端机、手动测量”。本期拆成两层：

| 层 | 方式 | 预算 | 说明 |
|---|---|---|---|
| `SignalBitLayout.Compute` | Core 测试内 Stopwatch | 中位数 ≤5ms | 纯函数；FD 64 字节消息，8 个 `64|64@1` 信号（StartBit 0/64/…/448），正好覆盖 512 格；先预热 10 次，再测 100 次取中位数 |
| VM 首次投影 | App 测试内 Stopwatch | ≤30ms | 选中一条 64 字节消息（`SelectedMessage` setter 触发 Compute + 64×8 行/格构建 + 预置帧的 SyncLive 实时投影）；20 次取中位数；**不含 WPF 实际渲染** |
| 实际渲染手感 | 人工 | 无明显卡顿 | 打开 FD 消息，RowDetails 展开无冻结；这是 §6 “首次实现 ≤30ms” 的真实口径 |

约束：

- 两个自动化测试是**回归守卫**，不是基准测试；阈值就用 spec 的 5ms / 30ms，不做更紧的断言，避免 CI 噪声。
- 真正钉住“测的是什么工作量”的是结构断言：512 格、8 段 × 64 位在界内、64 行 × 8 格、实时值已应用。把阈值临时改成 0.0 只能证明断言接了线，不能单独作为守卫质量的证据。
- 若 CI 上偶发超时，处理方式是记录实测分布后**用 note 说明再调整**，不得无声放宽（与仓库“台账放松必须配 note”同口径）。
- 计时测试不与其他计时测试共享静态停表；每次自建 `Stopwatch`。

### 3.2 功能 spec 同步

`2026-11-25-dbc-byte-layout-design.md` 改三处：

1. 文首状态：`已修订（评审意见已合并；待实施）` → `已实施（一期静态布局 + 二期实时点亮已交付；收口期见 2026-11-26 closeout spec）`。
2. §4 `ByteLayoutView` 行：`Canvas + Viewbox 渲染快照` → 实际实现 `ItemsControl + UniformGrid 渲染快照 + Viewbox Stretch="Uniform" 等比缩放；hover / 键盘聚焦 Popup`。
3. §5.6 信号表列：补上实现里已有的“诊断”“当前值”两列（顺序：色块 / 名称 / 位布局 / 系数偏移 / 单位 / 当前值 / 注释 / 诊断），与二期 XAML 的 8 列表头一致。

§8 末尾补一行二期测试摘要（单槽、VM 实时刷新、XAML 触发器、DI 组合；指向二期计划文件），不重写一期测试清单。

### 3.3 遗留 minor 处置

| # | 问题 | 处置 |
|---|---|---|
| 1 | `LiveFrameSlot.Clear()` 无调用点，注释却称“DBC 重载时调用” | 在 `OnLoaded` 的 UI 委托开头显式调用 `_liveSlot?.Clear()`（放在 `SelectedMessage = null` 之前）；同时 `SyncLive` 在“无选中”分支无条件 `SetTarget(null)`（无论标签页是否可见），避免隐藏期间 worker 继续为失效选中逐帧拷贝。`Clear()` 是纵深防御；隐藏状态下重载的行为由 `Reload_While_Hidden_Drops_The_Live_Payload` 钉住。 |
| 2 | 切到其他标签页后 33ms Timer 仍运行 | 见 §3.4。 |
| 3 | `_liveGate` 只保护刷新，不保护 `UpdateLayout` 的集合重建 | 只改注释：写明“安全性依赖 UI 线程不变式；锁不保护 `LayoutRows`/`VisibleSignals` 的重建，测试宿主必须停表后重建”。不改锁结构。 |
| 4 | 扩展帧（IDE bit）发布链路无集成测试 | 在 `DbcDecodeBackgroundServiceTests` 增加一条：DBC 消息 Id = `0x80001000`（29 位原始 ID `0x1000` 带 IDE 位），帧用 `CanId(0x1000, FrameFormat.Extended)`；worker 合并成查找键 `0x80001000` 后必须把载荷写进单槽。锁住 `Publish(msg.Id, …)` 与合并 IDE 位的约定。 |
| 5 | XAML 断言只查存在性 | 断言具体值：Opacity setter 的值必须是 `0.28`；信号表头与数据行都必须有 8 个 `ColumnDefinition`；表头“当前值”在 `Grid.Column="5"`，数据行 `LiveValue` 也在第 5 列。 |
| 6 | `DbcViewModel` 类注释与重新实现的 `IDisposable` 矛盾 | 更新类注释：本次实现 `IDisposable` 只释放实时 Timer；`DbcLoaded` / `LoadFailed` 订阅仍与 VM 生命周期一致，有意不取消。 |

### 3.4 切标签页停表

- `DbcView.xaml.cs` 处理 `IsVisibleChanged`：
  ```csharp
  private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
      => (DataContext as DbcViewModel)?.SetLiveVisible(IsVisible);
  ```
  在构造函数里订阅。`IsVisible` 由 TabControl 切换驱动；不额外处理 `Loaded`/`Unloaded`，避免重复事件。
- `DbcViewModel` 新增 `_liveVisible = true` 与 `internal void SetLiveVisible(bool visible)`：
  - 置 `false`：停 Timer（`Change(Infinite, Infinite)`），不清当前显示（保留最后值，符合“切走再回来不闪空”）。
  - 置 `true` 且当前有选中消息：走 `SyncLive()`（重启 Timer + 立即刷新一次）。
  - `SyncLive()` 在 `!_liveVisible` 时只保证 Timer 停止，不重启。
- 自动测试（VM 级，不依赖 WPF 可见性）：
  1. 先发布一帧并应用（值为“5”），再 `SetLiveVisible(false)`，然后发布第二帧，等 200ms，`LiveValue` 仍为“5”（Timer 确实停了，且没有清空最后显示；200ms 是 6 个周期，足够暴露“没停”）。
  2. `SetLiveVisible(true)` 后轮询 ≤2s，`LiveValue` 变为期望值。
  3. 无选中时 `SetLiveVisible(true)` 不抛异常、不启动空转更新（`IsLiveTicking == false`）。
  4. 隐藏状态下重载 DBC：单槽里旧文档的载荷必须消失（`Reload_While_Hidden_Drops_The_Live_Payload`）。

## 4. 验收

**自动化：**

- Core：`SignalBitLayoutPerformanceTests` 通过（≤5ms 中位数）。
- App：`DbcLayoutPerformanceTests`（≤30ms 中位数）、切页停表 3 条 + 隐藏重载 1 条、扩展帧缓存 1 条、加强后的 XAML 断言全部通过。
- 既有六类定向测试仍全绿；`dotnet build PeakCan.Host.slnx -c Debug` 0 warning / 0 error。
- 只跑定向测试，不跑全量。

**人工：**

- 打开 FD 64 字节消息：RowDetails 展开无明显卡顿（首次实现手感）。
- 选中消息后切到 Trace/Signal 页再切回：切走期间值列冻结，切回立即恢复更新。
- 启动后不选消息：CPU 无持续 30Hz 唤醒。
- 原有 5 项交互验收（0 位变淡 / 值列更新 / mux 切换 / 取消选中回静态 / FD 第 63 字节）复跑一遍。

**文档：**

- 功能 spec 三处文字与实现一致；本 spec 的验收项全部有结果记录（自动化输出或人工确认）。

## 5. 风险与取舍

- **计时测试的 CI 噪声**：5ms / 30ms 相对预期值（微秒到毫秒级）有百倍余量，但仍可能在极端负载下抖动。处置见 §3.1：记录分布 + note，不静默放宽。
- **`IsVisibleChanged` 的触发时机**：TabControl 切换时 UserControl 的 `IsVisible` 会变化；若未来 DBC 页改为非 Tab 宿主，需要复核该事件是否仍覆盖。接收这个风险，因为当前宿主就是 TabControl。
- **停表期间不刷新**：切走时保留最后值而不是清空，回归时可能短暂显示旧值，直到下一秒刷新。这是刻意的（避免闪空），在秒级内自愈。
- **`Clear()` 接线**：当前重载路径本来就会 `SetTarget(null)`，新接线是防御性的；若将来有人改掉 `SelectedMessage = null`，它仍然兜住旧帧。
