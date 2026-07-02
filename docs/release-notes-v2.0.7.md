# v2.0.7 PATCH — 3 bug fixes (2026-07-02)

## Summary

Three user-reported bugs closed in a single PATCH:

| # | Severity | Symptom | Root cause | Fix |
|---|----------|---------|------------|-----|
| 1 | MEDIUM | UDS view 的 Clear 按钮无效 | `OnLogCollectionChanged` 只处理 `Action=Add`；`OutputLog.Clear()` 触发的是 `Action=Reset` → handler 提前 return → RichTextBox 旧内容残留 | 新增 `case Reset: LogParagraph.Inlines.Clear()` 分支 |
| 2 | HIGH | UDS Routine name 内容缺失 | `RequestBasedMappers.ExtractRoutines` 只读 `svc.Attribute("SHORT-NAME")`；Vector CANdelaStudio .odx-d 实际用 child element `<SHORT-NAME>`（95/95 DIAG-SERVICE 都是 child form）→ 所有 routine name 拿不到 | 先读 attribute，fall back 到 `svc.Element(ns+"SHORT-NAME")` |
| 3 | MEDIUM | Script view 第二次进入页面报错 "WebView2 runtime 未安装或损坏" | `OnUnloaded` 里 `EditorWebView.Dispose()` + 字段 null → 每次切 tab 销毁 CoreWebView2 进程；下次 OnLoaded 时 cached CoreWebView2Environment 状态坏掉 → EnsureCoreWebView2Async 抛 "runtime corrupted" | 移除 tab-switch Unload 时的 Dispose；WebView2 跟 ScriptView 同寿命（只有 1 个实例 = 1 个 CoreWebView2 进程，无泄漏） |

## Bug-2 详解：Vector CANdelaStudio 的 SHORT-NAME 形式

```xml
<!-- canonical ODX 2.x（attribute 形式）— 之前能工作 -->
<DIAG-SERVICE ID="_svc1" SHORT-NAME="CheckProgrammingPreconditions_Start">
  <REQUEST-REF ID-REF="_1"/>
</DIAG-SERVICE>

<!-- Vector CANdelaStudio .odx-d（child element 形式）— 之前全空 -->
<DIAG-SERVICE ID="_svc1">
  <SHORT-NAME>CheckProgrammingPreconditions_Start</SHORT-NAME>
  <REQUEST-REF ID-REF="_1"/>
</DIAG-SERVICE>
```

我的 v2.0.4 PATCH 只测了 attribute 形式（用户实际文件用 child form 但 v2.0.4 测试断言用 mock 数据）。v2.0.7 跑真实 ODX-D 文件分析：

```
Total DIAG-SERVICE scanned: 95
  SHORT-NAME as attr: 0
  SHORT-NAME as child: 95    ← 全是这一种
  Empty: 0
```

修复后 attribute 优先，child element fallback。两者都有时 attribute 赢（ODX 2.x canonical 形式）。

## Bug-3 详解：WebView2 Dispose-on-Unload 陷阱

```csharp
// PRE-v2.0.7 (ScriptView.xaml.cs):
private void OnUnloaded(object sender, RoutedEventArgs e)
{
    _isLoaded = false;
    EditorWebView?.Dispose();    // ← 杀掉 CoreWebView2 进程
    EditorWebView = null!;       // ← XAML 字段也清了
}

// POST-v2.0.7:
private void OnUnloaded(object sender, RoutedEventArgs e)
{
    _isLoaded = false;
    // 不再 Dispose —— WebView2 跟 ScriptView 同寿命
}
```

**为什么 tab 切换 ≠ view 销毁**：WPF UserControl 的 Loaded/Unloaded 在 tab 切换时**都会**触发，但控件实例本身不销毁。Dispose 在 Unload 上跑会把 CoreWebView2 杀掉，但 process-wide 的 `CoreWebView2Environment` 引用还在 cached 状态——下次 `EnsureCoreWebView2Async` 用这个坏掉的 env 就抛 "runtime not installed or corrupted"。

**正确做法**：WebView2 应该跟 View 的真实生命周期绑定（process exit），不是 tab 切换。整个 App 只有一个 ScriptView，所以只有 1 个 CoreWebView2 进程，process 退出时 OS 自动 reap。

**长期**：如果将来要支持多个 ScriptView 并存，得用 `CoreWebView2Environment.CreateWithOptions` 给每个实例独立 user data folder。但当前一个 instance 没这需求。

## Test counts

| Suite | v2.0.6 | v2.0.7 | Δ |
|-------|--------|--------|---|
| Core  | 384    | 388    | +4 (`RoutineNameShapeTests` 4 个 case) |
| App   | 456    | 456    | 0（Bug-1/Bug-3 是 WPF code-behind，需要 full visual tree，难 unit test；VM 行为已有 `UdsViewModelOrchestratorTests.ClearOutputCommand_Clears_OutputLog` 锁定） |
| Infra | 84     | 84     | 0 |
| **Total** | **924 + 6 SKIP** | **928 + 6 SKIP** | +4 |

Race-test flake counter preserved (24/24+).

## Files changed (5)

### 生产代码（3）
- `src/PeakCan.Host.App/Views/UdsView.xaml.cs` (M: 增 Reset handler)
- `src/PeakCan.Host.Core/Uds/Odx/RequestBasedMappers.cs` (M: SHORT-NAME attribute + child fallback)
- `src/PeakCan.Host.App/Views/ScriptView.xaml.cs` (M: 移除 Unload 时的 Dispose)

### 测试代码（1）
- `tests/PeakCan.Host.Core.Tests/Uds/Odx/RoutineNameShapeTests.cs` (+ 4 tests)

### 文档（1）
- `docs/release-notes-v2.0.7.md` (+)

## Process lessons (NEW)

1. **ObservableCollection.Clear() raises Reset, not Remove** — WPF code-behind that maintains a parallel visual structure (like RichTextBox.Inlines) must handle `NotifyCollectionChangedAction.Reset` explicitly. The CollectionChanged event has 5 actions; defaulting to "only handle Add" silently breaks Clear / bulk-replace / Reset paths. Lesson: when wrapping an ObservableCollection in a visual mirror, write a switch on `e.Action` not an `if (e.Action != X) return` guard.

2. **ODX SHORT-NAME: attribute vs child element are both legal** — ODX 2.x schema says attribute, Vector CANdelaStudio .odx-d exports say child element. Both are common in the wild. Any code that reads ODX names must try both forms. Lesson: when consuming vendor-format XML with documented schemas, NEVER trust attribute-only or element-only reads — the schema is a guideline, not a contract, for vendor exports.

3. **WebView2 lifecycle is view-lifetime, not Loaded/Unloaded** — WPF UserControl's Loaded/Unloaded events fire on tab navigation but don't destroy the control. Disposing WebView2 on Unload kills CoreWebView2 but leaves the cached CoreWebView2Environment in a bad state — the next init against that env fails with the misleading "runtime not installed" error. Lesson: WebView2.Dispose() should run on actual view destruction (parent Window.Closed, App.Exit), not on Loaded/Unloaded. For tab-based shells, the WebView2 lives for the App's lifetime.

## Pre-ship review

0C / 0H / 0M / 0L.

## Ship method

延续 Tier 3 fallback（github.com:443 仍不通）；预计 9-call pipeline。