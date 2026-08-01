# TDD Plan: HIL View Enhancements

> Spec: `docs/superpowers/specs/2026-08-01-hil-view-enhancements-spec.md` (Rev 5)

## Phase 1: G1 Browse 占位符 ✅

| Step | Type | File | Status |
|------|------|------|--------|
| 1.1 | RED | `tests/.../Converters/EmptyStringToVisibilityConverterTests.cs` | ✅ 6 tests |
| 1.2 | GREEN | `src/.../Converters/EmptyStringToVisibilityConverter.cs` | ✅ |
| 1.3 | WIRE | `App.xaml` 注册转换器 | ✅ |
| 1.4 | WIRE | `HilView.xaml` 5 个 Browse 字段 Grid overlay | ✅ |

## Phase 2: G2 Mode 图标 ✅

| Step | Type | File | Status |
|------|------|------|--------|
| 2.1 | RED | `tests/.../Converters/HilModeToIconConverterTests.cs` | ✅ 7 tests |
| 2.2 | GREEN | `src/.../Converters/HilModeToIconConverter.cs` | ✅ |
| 2.3 | RED | `tests/.../Converters/HilModeToDescriptionConverterTests.cs` | ✅ 5 tests |
| 2.4 | GREEN | `src/.../Converters/HilModeToDescriptionConverter.cs` | ✅ |
| 2.5 | WIRE | `HilView.xaml` ComboBox ItemTemplate + 图标 TextBlock | ✅ |

## Phase 3: G3 独立 ECU 编辑器

### 3A-3F: 全部完成 ✅

| Step | File | Status |
|------|------|--------|
| 3.1 RED | `EcuScriptEditorViewModelTests.cs` | ✅ 26 tests |
| 3.2 GREEN | `EcuScriptEditorViewModel.cs` | ✅ |
| 3.3-3.4 | `EcuScriptEditorWindow.xaml` + `.cs` | ✅ |
| 3.5-3.8 | `HilViewModel.cs` 瘦身+事件+命令 | ✅ |
| 3.9-3.13 | `AppShellViewModel.cs` + `ViewSwitchFlow.cs` 接线 | ✅ |
| 3.14-3.16 | `AppHostBuilder.cs` + `AppShell.xaml` + `HilView.xaml` | ✅ |
| 3.17 FIX | 8 处构造点 + 2 处 HilViewModelTests | ✅ |

> **Spec 偏差**: `MessageBoxResult.OK` -> `MessageBoxResult.Yes`
> (`IMessageBoxPrompt.ShowAsync` 是 Yes/No modal, CodeGraph 确认 `SessionAutoSaver.cs:177`)

## Phase 4: 验证

| Step | Action | Status |
|------|--------|--------|
| 4.1 | `dotnet build src/PeakCan.Host.App` 0 警告 0 错误 | ✅ |
| 4.2 | 45 新增测试全通过 (4 pre-existing TraceViewer 失败无关) | ✅ |
| 4.3 | ConverterSmokeTests 扩展 | ⬜ 后续 |
| 4.4 | G1/G2/G3 手动验证 | ⬜ 用户 |

## 测试统计: 45 ✅ / 0 ❌
