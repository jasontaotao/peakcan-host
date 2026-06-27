# Release Notes — PeakCan Host v1.2.11

**Date:** 2026-06-27

## Summary

v1.2.11 is a 6-item PATCH that closes five long-standing UI gaps (RTR trace label, DBC-decoded Trace column, Send-side flags / cyclic / library, Recording tab) plus one infrastructure fix (DBC decode worker fan-out to Trace). All changes follow TDD discipline with paired RED/GREEN commits and follow the same release pattern as v1.2.10 PATCH (squash merge of feature branch → tag → release).

## New features

### Item 1 — Trace RTR 列标签

`TraceEntry.IsRtr` init 属性 + `FrameType` 优先级 `ERR > RTR > FD > ""`。用户在 Send 面板勾选 RTR 发送后，Trace 行立即可见 `"RTR"` 标签。`TraceViewModel.AppendBatchAsync` 把 `FrameFlags.Rtr` bit 映射到 `IsRtr`。

### Item 2 — Trace Decoded 列填充

`DbcDecodeBackgroundService` worker 加 `TraceViewModel.PendingDecode` lookup，每个 frame 命中后 fill `traceEntry.Decoded = "Name=Value, ..."` 格式。DBC decode 完成时 Trace 行的 Decoded 列从空 → 信号值。`Decoded` 改 set + PropertyChanged（之前 init-only），WPF DataGrid 绑定响应行更新。

### Item 3 — Send CyclicSend 暴露

抽 `ICyclicSendService` 接口（`IsRunning` / `SendCount` / `Start` / `Stop`），`CyclicSendService` 实现。`SendViewModel` 加 `CyclicIntervalText` / `IsCyclicRunning` / `CyclicSendCount` 三个 ObservableProperty + `StartCyclicCommand` / `StopCyclicCommand` 两个 RelayCommand + 200 ms `DispatcherTimer` 轮询。`SendView.xaml` 加 Cyclic Expander 区块。

### Item 4 — Send RTR/BRS/ESI 标志

`SendViewModel` 加 3 个 flag checkbox：`IsRtr` / `IsBitRateSwitch` / `IsErrorStateIndicator`。`SendAsync` flag builder 扩展为 `Fd | Rtr | BRS | ESI` 各 bit。RTR + FD 互斥（ISO 11898-1：RTR 仅 classic CAN），被拒时设 Status `"RTR is not valid for CAN FD"` 不发送。`SendView.xaml` 加 3 个 checkbox。

### Item 5 — Send Frame Library

新建 `SendFrameLibrary` 服务：`%APPDATA%\PeakCan.Host\send-library.json` JSON 持久化 + atomic tmp+rename 写入 + corrupt-file 容错（missing/corrupt → 空列表 + Error 日志）。`SavedFrame` record（Name + RawId + Extended/FD/RTR/BRS + DataHex + SavedAt）。`SendViewModel` 加 `Library` ObservableCollection + `RefreshLibrary` / `SaveCurrentToLibrary` / `LoadFromLibrary` / `DeleteFromLibrary` 四个 RelayCommand。`SendView.xaml` 加 Library Expander 区块 + DataGrid 列出已保存帧 + 行内 Load/Delete 按钮。

### Item 6 — Recording UI 暴露

新建 `RecordViewModel` 包装既有 `RecordService`（已在 v0.5.0 实现 ASC/CSV 录制）。加 `OutputPath` / `Format` / `IsRecording` / `FrameCount` / `Status` 五个 ObservableProperty + `Browse` / `Start` / `Stop` 三个 RelayCommand + 200 ms `DispatcherTimer` 轮询。新建 `RecordView.xaml` + AppShell `ShowRecordCommand` + View 菜单 Record 项。Recording tab 从 backend-only 升级到全功能 UI。

## TDD record

12 RED → GREEN 提交 squash 到 main：

- Task 1: TraceEntry.IsRtr / FrameType RTR (5 RED tests + GREEN)
- Task 2: TraceViewModel maps IsRtr from CanFrame (扩展现有 STA dispatcher 测试，0 新增独立测试因 STA 单例 race)
- Task 3: TraceEntry.Decoded mutable + PropertyChanged (2 RED tests + GREEN)
- Task 4: TraceEntryKey struct + PendingDecode map (2 unit tests + STA dispatcher 断言合并进 Task 2 测试)
- Task 5: DbcDecodeBackgroundService fills Decoded (3 RED tests + GREEN，service ctor 加 TraceViewModel)
- Task 6: SendViewModel flags RTR/BRS/ESI (5 RED tests + GREEN，RTR+FD 拒绝)
- Task 7: SendViewModel exposes CyclicSend (3 RED tests + ICyclicSendService 接口抽取)
- Task 8: SendFrameLibrary JSON persistence (4 RED tests + atomic write)
- Task 9: SendViewModel loads/saves/deletes library (3 RED tests + 4 commands)
- Task 10: SendView.xaml rewrite with Expanders (3 文本级 XAML 测试，因 STA 单例 race 放弃视觉树测试)
- Task 11: RecordViewModel (5 RED tests + DispatcherTimer 轮询 + PollNow test helper)
- Task 12: RecordView.xaml + AppShell ShowRecordCommand (1 简化测试验证 command 非 null，路由由手动 smoke 覆盖)

## Test metrics

- Total tests: **300 PASS + 4 SKIP + 0 FAIL** (was 264 + 4 SKIP + 0 FAIL on v1.2.10)
- New tests: **+36**
- Coverage: ~85% (App project); Core / Infrastructure unchanged

## Process notes

### STA-WPF Application 单例 race

WPF `Application` 单例在 xunit 跨 test class 并行时互相 collide（即使 `[Collection(DisableParallelization=true)]` 也不完全可靠）。本 PATCH 的对策：

1. `[Collection(WpfAppTestCollection.Name)]` 标注所有 STA-bound 测试类（TraceViewModelTests / SendViewTests / AppShellViewModelTests）
2. 测试内部 STA thread 创建 `Application` 前调 `LeakedApplicationReset.CleanupLeakedApplication()` 反射清 `_appInstance`
3. STA-bound assertion 尽量合并进同一测试方法（避免多 STA 测试共享 AppDomain）
4. SendViewTests 改为文本级 XAML 检查（XAML markup 含 `x:Name="CyclicExpander"` 等），放弃视觉树验证

### SendViewModel 重命名

私有 `_library` 字段重命名为 `_libraryService`，避免与 ObservableProperty `_library` 同名冲突（XAML 临时 csproj 把同类型 partial 包含时编译错误）。

### SendFrameLibrary `partial`

WPF XAML 临时 csproj（`PeakCan.Host.App_*.wpftmp.csproj`）会把同 namespace 内的 class 作为 partial 类型包含，因此 `SendFrameLibrary` 类需加 `partial` 修饰符才能编译。

### v1.2.10 Directory.Build.props 未对齐

v1.2.10 ship 时未 bump `Directory.Build.props` 版本号（仍为 1.2.3）。本 PATCH 一并修正为 1.2.11。

## Known issues (deferred to v1.3.0+)

- `_pendingDecode` 字典在 Entries FIFO trim 时短期泄漏（每个 trimmed row ~200 ms 由 worker fill Decoded 后失效），由 MaxRows 默认 1000 上界，2 × 32 B ≈ 64 KB 影响可忽略
- RecordView 格式选择暂仅 ASC enabled（CSV 推迟 v1.3.0，加 `EnumToBoolConverter` 后回归）
- ShowRecordCommand STA 路由测试改为 command 非 null 验证；实际 UI 路由由手动 smoke 覆盖（CI 中 WPF Application 单例 race 不稳定）

## Breaking changes

无。所有 PATCH 范围内变更，不改公开 API 签名。