# Release Notes v3.11.2 — ViewSwitcher Extraction (PATCH)

**Released:** 2026-07-07
**Parent:** v3.10.0 MINOR (`8c26af7`)
**Tag:** v3.11.2
**Branch:** `feature/v3-11-1-patch`

## Highlights

This PATCH completes the v3.11.x refactor cleanups by shipping **M3** (ViewSwitcher extraction) that was originally intended for the v3.11.1 PATCH but the parallel-implementer dispatch produced a half-finished build state. After re-integrating the working tree, M3 is now shippable.

| Commit | Finding | Refactor | Tests |
|--------|---------|----------|-------|
| `0c2e5e7` | **M3** | `ViewSwitcher` extracted — 9 Show-* commands + `ShowTraceViewer` refactored to use the static helper | +6 |

**Test delta:** 1260 + 5 SKIP / 0 fail → **1266 + 5 SKIP / 0 fail** (+6 active tests)
**Code stats:** 1 commit / +464 / -112 (net +352 LoC, mostly the new `ViewSwitcher` helper + tests)

## Refactor

### M3 — `ViewSwitcher` extraction

`AppShellViewModel`'s 9 Show-* lazy-view-create commands (`ShowTrace` / `ShowDbc` / `ShowSend` / `ShowSignals` / `ShowStats` / `ShowScript` / `ShowUds` / `ShowRecord` / `ShowReplay`) plus the `ShowTraceViewer` secondary-window command had near-duplicate bodies:
1. Check the cached field; if null, construct the view/window
2. Assign the cached instance to the shell's `CurrentView` (in-place views) or call `Show()` (windows)
3. On window close, null the cached field so the next click opens a fresh window

**Fix:** Extract the pattern into `ViewSwitcher` static helper class with two methods:

```csharp
public static class ViewSwitcher
{
    public static void Show<TView>(
        Func<TView> factory,
        ref TView? cache,
        Action<TView> setCurrent,
        string menuName) where TView : FrameworkElement;

    public static void ShowWindow<TWindow>(
        Func<TWindow> factory,
        ref TWindow? cache,
        Action<string> logOpen = null) where TWindow : Window;

    public static void HideWindow<TWindow>(ref TWindow? cache) where TWindow : Window;
}
```

The `ShowWindow` path uses a `CacheHolder<TWindow>` struct to work around C#'s "lambda cannot capture `ref`" limitation — the Closed subscription holds a regular reference to the holder struct whose `Value` is nulled when the window closes, then written back into the caller's `cache` field. Defensive read-back also handles the rare case where the window already closed synchronously between `factory()` and the `+=` subscription.

**Refactored commands** (in `AppShellViewModel.cs`):
- `ShowTrace` / `ShowSend` / `ShowSignals` / `ShowStats` / `ShowScript` / `ShowUds` / `ShowRecord` / `ShowReplay` → `ViewSwitcher.Show`
- `ShowTraceViewer` → `ViewSwitcher.ShowWindow` (preserves Owner + Show/Activate logic in caller; helper wires Closed-reset)
- `ShowDbc` → kept inline (uses `GetOrCreateDbcView` which is already a one-liner with `??=`)
- `OpenMultiFrame` → kept inline (original code opens a FRESH window on every click — no cache semantics; comment documents future refactor path)

**Public RelayCommand surface preserved** — all 11 commands (`ShowTraceCommand` / `ShowDbcCommand` / `ShowSendCommand` / `ShowSignalsCommand` / `ShowStatsCommand` / `ShowScriptCommand` / `ShowUdsCommand` / `ShowRecordCommand` / `ShowReplayCommand` / `ShowTraceViewerCommand` / `OpenMultiFrameCommand`) still bind the same XAML commands. The `CurrentView` ObservableProperty still drives the `MainArea` ContentControl.

**Tests:** +6 in `ViewSwitcherTests.cs`:
- `Show_FirstCall_CreatesViewFromFactory`
- `Show_SecondCall_ReturnsCachedView`
- `Show_NullFactory_ThrowsArgumentNullException`
- `ShowWindow_FirstCall_CreatesAndOwns`
- `ShowWindow_CloseReset_ClearsCache`
- `HideWindow_NullCache_NoOp`

(STA-bound tests use `[Collection(WpfAppTestCollection.Name)]` + inline `RunSta` mirroring `AppShellViewModelTests.RunSta`.)

## Deferred

| Item | Reason |
|------|--------|
| **C2** ReplayViewModel god class split | Deferred to v3.12.0 MINOR (1153 LoC split = 1+ day) |
| **UDS** UserControl → Window refactor | Reverted during v3.11.1 PATCH ship prep; re-test in next user-driven cycle |
| **Record** Window → tab UserControl | NO-OP — `RecordView` is already a `UserControl` (converted in v1.2.11 PATCH Item 6) |
| **OpenMultiFrame** cache refactor | Original code opens a fresh window on every click (no cache field); future refactor to add `_multiFrameSendWindow` cache + swap to `ViewSwitcher.ShowWindow` |

## Upgrade notes

No breaking changes. All public API surfaces preserved:
- `AppShellViewModel` 11 RelayCommand surface unchanged (XAML bindings work without modification)
- `CurrentView` ObservableProperty semantics preserved
- `ViewSwitcher` is a new `internal static` helper class — only visible to assembly

## NEXT

- v3.11.3 PATCH — retry UDS UserControl → Window refactor (user-requested; cleanly committed before attempting)
- v3.12.0 MINOR — C2 ReplayViewModel god class split (1153 LoC → 3 VMs)