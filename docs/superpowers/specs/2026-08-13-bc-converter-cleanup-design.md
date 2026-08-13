---
topic: bc-converter-cleanup
created: 2026-08-13
status: approved
covers: B/C 决策记录 + 2 个 converter 微优化（删死代码 + 去重）
related: docs/superpowers/specs/2026-08-13-scripting-cycle-and-trace-session-state-design.md
---

# 设计文档：B/C — Converter 与 XAML 现状决策 + 微优化

## 1. 背景与问题

原设计把 B/C 定义为「converter 合并 + XAML 拆分」。探索确认该前提大部分已被历史工作完成，真正的残留很小。本文档记录 B/C 的探索结论、决策，以及 2 个低风险微优化（删除死 converter + 去重重复 converter）。**0 行为变更**。

## 2. 探索证据

### 2.1 Converter（16 个 + 2 个 code-behind）

| 项 | 现状 | 结论 |
|---|---|---|
| `Composition/Converters/` 16 个 converter | 全部在 XAML 中使用（1~28 次），无死代码 | 无需合并 |
| Null 家族（`NullToVisibility`/`InverseNullToVisibility`/`NullToBoolean`/`InverseNullToBoolean`） | 语义互异（`NullToVisibility` 还把空串当 null；bool vs visibility 目标不同） | 不是重复；合并成参数化 converter 会伤可读性 |
| 声明位置 | 主用集中在 `App.xaml`（7 个）；少数按视图局部声明（`SendView` 的 `InverseBoolean` 等，App.xaml 注释明确"刻意局部"） | 已集中，视图级声明是**既有设计选择**，不并入全局 |
| **`BooleanToVisibilityConverter`** | 是 WPF 内置 `System.Windows.Controls.BooleanToVisibilityConverter` 的重写，但**加了 null 安全**（null→Collapsed；内置版 null 会抛） | **保留**我们的 null-safe 变体；内置/自定义混用（`SignalView`/`ChatPanel` 用内置，`App.xaml`/`UdsWindow` 用自定义）记为已接受 |
| `TraceViewerViewChatPanel.xaml.cs` code-behind `InverseBoolToVisibilityConverter`（Views 命名空间） | **死代码**：类 + `ChatPanel.xaml:13` 声明，全库无使用 | **删除** |
| `TraceViewerViewChatPanel.xaml.cs` code-behind `InverseBoolConverter`（Views 命名空间） | 与 `Composition/Converters/InverseBooleanConverter` **逻辑重复**（`!bool`）；绑定 `IsTestingChatConnection`（非空 bool）下 null 语义差异不触发 | **去重**：改用 Composition 版 |

### 2.2 XAML

| 项 | 现状 | 结论 |
|---|---|---|
| 11 个视图，最大 `TraceViewerView.xaml` 346 行，总计 1786 行 | 无超大视图；chat panel 已拆为独立文件（`TraceViewerViewChatPanel.xaml` 241 行） | **无需拆分** |
| ResourceDictionary | 不存在（App.xaml 内联 7 个 converter） | 可选的组织优化，非必要 |

## 3. 决策

1. **不合并 converter。** 16 个全部使用中且语义互异；合并成参数化 converter 是可读性反模式。
2. **不拆 XAML。** 视图均偏小且已拆分。
3. **保留 null-safe `BooleanToVisibilityConverter`。** 它是内置类的安全变体（null→Collapsed 而非抛异常），8 处绑定的 null 安全性无法静态验证——放弃换内置，避免运行时回归。内置/自定义混用记为已接受。
4. **视图级 converter 声明不并入 App.xaml。** 这是既有设计选择（App.xaml 注释明示 `InverseBoolean` 仅 SendView 用、刻意局部）。
5. **2 个微优化**（删除死代码 + 去重，见 §4）。

## 4. 变更清单（2 项，0 行为变更）

### 4.1 删除死 converter `InverseBoolToVisibilityConverter`

- `TraceViewerViewChatPanel.xaml.cs:10-18`：删除类。
- `TraceViewerViewChatPanel.xaml:13`：删除 `<views:InverseBoolToVisibilityConverter x:Key="InverseBoolToVis" />`。
- code-behind 若 `using System.Globalization;` / `using System.Windows;` 因删除后无剩余引用，一并删除。

### 4.2 去重 `InverseBoolConverter` → `InverseBooleanConverter`

- `TraceViewerViewChatPanel.xaml.cs:20-28`：删除 `InverseBoolConverter` 类。
- `TraceViewerViewChatPanel.xaml:14`：`<views:InverseBoolConverter x:Key="InverseBool" />` → `<conv:InverseBooleanConverter x:Key="InverseBool" />`（`conv` 指向 `Composition.Converters`；若 ChatPanel 无该 xmlns 则新增）。
- `ChatPanel.xaml:93` 的 `{StaticResource InverseBool}` 绑定不变（key 未变）。
- 行为等价性：绑定目标 `IsTestingChatConnection` 是非空 bool，`InverseBooleanConverter`（null 透传）与 `InverseBoolConverter`（null→true）在此场景无差异。

## 5. 测试策略

- 0 行为变更，现有测试全绿即验证。
- 构建 + 全量 App 测试（含 `ConverterSmokeTests`——该测试引用 `BooleanToVisibilityConverter`（保留）与 `InverseBooleanConverter`（保留），不受影响）。

## 6. 非目标

- 不合并 converter 家族、不引入参数化 converter。
- 不拆 XAML、不新建 ResourceDictionary。
- 不动 `BooleanToVisibilityConverter`（保留 null-safe 变体）。
- 不动视图级 converter 声明（既有设计选择）。
- 不新增任何行为。

## 7. 实施顺序

1. spec 提交（本文件）。
2. 删除死 converter + 去重（§4）。
3. 构建 + 全量测试 + 提交。
