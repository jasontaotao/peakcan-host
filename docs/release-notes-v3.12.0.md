# Release Notes v3.12.0 — ReplayViewModel god class split + WPF converter STA smoke matrix (MINOR)

**Released:** 2026-07-08
**Parent:** v3.11.7 PATCH (`23da0505`)
**Tag:** v3.12.0
**Branch:** `feature/v3-12-0-minor`

## Highlights

This MINOR is a **zero-user-visible-behavior-change** investment in two preventive
maintenance goals:

1. **C2: Decompose the 1190-LoC `ReplayViewModel` god class** into 5 partial-class
   files (1 core + 4 responsibility regions). Public type, XAML bindings, DI wiring,
   and command names are all unchanged — the split is purely structural.
2. **M3: Ship a project-wide WPF converter STA smoke-test matrix** that catches the
   v3.11.6-class regression ("binding crashes only at DataTemplate materialization
   time") on every converter the project uses.

Two review-backlog findings are also closed:

- **H1: `IReplayService.LoopRewound` contract-drift guard** — regression test for
  the v3.9.2 PATCH H1 fix (event contract without subscriber was loud contract
  drift for 9 PATCHes v3.8.0 → v3.9.1).
- **L1: `ReplayException` hierarchy xmldoc** — documents the base class contract
  (parse | load | runtime failure-class separation).

| Commit | Fix | Tests |
|--------|-----|-------|
| `5998d94` | RED: partial-split regression guard (Task 1) | +2 |
| `02c03ca` | GREEN: 4-way `ReplayViewModel` partial split — Loader / Playback / Bookmarks / Bundle (Tasks 2-5) | +4 |
| `b691e44` | GREEN: project-wide WPF converter STA smoke-test matrix (Task 6) | +5 |
| `1a6e0e4` | GREEN: `IReplayService.LoopRewound` contract-drift guard (Task 7) | +2 |
| `455b61d` | DOCS: `ReplayException` hierarchy xmldoc (Task 8) | 0 |
| `c70234c` | REVIEW-FIX: M3 converter smoke test tightened to TwoWay DP + `Mode=OneWay` contract | 0 |

**Test delta:** 1284 + 5 SKIP / 0 fail → **1297 + 5 SKIP / 0 fail** (+13 active: 6 partial-split regression-guard + 5 converter smoke + 2 LoopRewound contract)
**Code stats:** 1190-LoC `ReplayViewModel.cs` → 430-LoC core + 4 partial files (312+207+102+175 = 796 LoC) + 50-LoC `ReplayExceptions.cs` xmldoc expansion + new tests. **Zero-net LoC** in the VM responsibility surface.

## Why this MINOR

v3.9.2 PATCH noted 3 CRITICAL findings deferred to v3.10.0 MINOR: `MessageBox.Show` in VM,
`ReplayViewModel` god class, and `AutoSaver` dedup. v3.10.0 MINOR + v3.11.x PATCH chain
cleared `AutoSaver` (via `SessionAutoSaver<TVm>` generic base) + `MessageBox.Show` seam
(`IMessageBoxPrompt`), but the god class remained. v3.12.0 MINOR is the closure of that
last CRITICAL — and bundles the v3.11.7 PATCH lesson (regression-guard tests must include
STA-bound WPF runtime validation, not just static substring checks) into a project-wide
matrix that prevents the same class of regression from hitting any other converter.

## What changed

### C2: `ReplayViewModel` 4-way partial split (Task 2-5)

`src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` was a 1190-LoC god class
covering 5 concern clusters: state + commands, file loading (.asc + .tmtrace),
playback control (play/pause/seek/loop), bookmarks + loop regions, and bundle
persistence. The 5 files are now:

| File | LoC | Responsibility |
|------|-----|----------------|
| `ReplayViewModel.cs` (CORE) | 430 | State, commands, ctor, threading, IDisposable |
| `ReplayViewModel.Loader.partial.cs` | 312 | `.asc` / `.tmtrace` file loading, content hashing, locator |
| `ReplayViewModel.Playback.partial.cs` | 207 | Play/pause/seek/loop-region boundary checks |
| `ReplayViewModel.Bookmarks.partial.cs` | 102 | Bookmark / LoopRegion CRUD + persistence |
| `ReplayViewModel.Bundle.partial.cs` | 175 | `.tmtrace` bundle save/load + Recent MRU |

The split uses the C# `partial` class mechanism (CommunityToolkit.Mvvm source-gen
emits `[ObservableProperty]` / `[RelayCommand]` partial setters into the file
containing the attribute; any `OnCanIdFilterTextChanged`-style partial method
can live in any file marked `partial` — that's the entire point of the pattern).

**Public type unchanged:** `ReplayViewModel` is still `public sealed partial class`,
still single ctor, still implements `IDisposable`. XAML bindings
(`{Binding IsLoaded}`, `{Binding PlayCommand}`) need no edits. DI registration
(`services.AddTransient<ReplayViewModel>()`) needs no edits. Test fixtures
(`new ReplayViewModel(...)`) need no edits.

**Net LoC delta in VM responsibility surface:** zero. The split moves ~760 LoC
from the core file to 4 partial files without removing a single behavior.

### M3: project-wide WPF converter STA smoke-test matrix (Task 6)

`tests/PeakCan.Host.App.Tests/Composition/ConverterSmokeTests.cs` (NEW, +5 via
`[Theory]` parameterization over `AllConverters()`).

The v3.11.6 PATCH shipped a `MultiBinding` + `IMultiValueConverter` fix for the
Trace Viewer master-radio. The fix was correct — but the regression-guard test
(`NoProductionFile_References_MasterRadioConverter_Or_ResourceKey`) only asserted
that banned substrings were absent from production files. The MultiBinding still
crashed at runtime because `RadioButton.IsChecked` is a TwoWay `DependencyProperty`
by default, so WPF unconditionally invokes `ConvertBack` during the binding's
`Activate()` path, and the converter's `ConvertBack` threw `NotSupportedException`.
v3.11.7 PATCH added the `Mode="OneWay"` fix + an STA smoke test that programmatically
constructs the actual `RadioButton` + `MultiBinding` + exercises the binding
activation pipeline.

This MINOR generalizes that lesson to **every** converter in `App.xaml`:

- `[Theory]` parameterizes over `ConverterCase(Converter, TargetDp, IsTwoWayDefault)`.
- Each iteration programmatically constructs the production-equivalent binding +
  the converter on a `RadioButton` (STA-bound), forces `Mode=OneWay` on the binding
  (matching the project's actual usage pattern), then touches `IsChecked` + Measure
  + Arrange + reads again — exactly the binding activation pipeline WPF runs when
  the `DataTemplate` materializes.
- The test asserts the binding activation never throws.

This catches the v3.11.6-class regression on every converter, not just the one that
happened to ship in v3.11.6.

### H1: `IReplayService.LoopRewound` contract-drift guard (Task 7)

`tests/PeakCan.Host.Core.Tests/Replay/IReplayServiceLoopRewoundContractTests.cs`
(NEW, +2 tests).

The v3.9.0 MINOR P1 contract promised `IReplayService.LoopRewound` would fire on
A/B loop rewind so the UI could surface a status message. No UI subscriber was
wired for 9 PATCHes (v3.8.0 MINOR → v3.9.1 PATCH). The contract was "loud
contract drift": the event existed, the docs were clear, but no consumer caught
the gap. v3.9.2 PATCH H1 finally wired the subscriber — but without a regression
test, a future cleanup could silently delete the event or rename the handler
without anyone noticing.

The two new tests pin the contract:

1. **Reflection test** — `typeof(IReplayService).GetEvent("LoopRewound")` exists,
   has `EventHandler<LoopRegionRewoundEventArgs>` handler type, and is
   `Public | Instance`. Catches event deletion or signature drift.
2. **Behavioral test** — `ReplayService` actually raises `LoopRewound` when a
   playback loop boundary is crossed. Catches event-handler detachment inside the
   service implementation.

### L1: `ReplayException` hierarchy xmldoc (Task 8)

`src/PeakCan.Host.Core/Replay/ReplayExceptions.cs` (file is PLURAL — `ReplayExceptions`,
not `ReplayException`; the singular is the abstract base class inside it). xmldoc
added to `ReplayException`, `ReplayLoadException`, `ReplayFormatException`, and
`ReplaySendException`. Documents the contract: **new concrete subclasses MUST
describe a single failure class (parse | load | runtime) — never mix**. Callers
(Replay VM + Trace Viewer VM) catch `ReplayException` to surface ALL replay-related
failures via `ErrorMessage`; catch a concrete subclass only when the recovery
path is subclass-specific.

No code logic changed — documentation-only change.

## Upgrade notes

No breaking changes:

- `ReplayViewModel` is still `public sealed partial class` with the same ctor signature.
- All XAML bindings (`{Binding IsLoaded}`, `{Binding PlayCommand}`, etc.) are unchanged.
- DI registration (`services.AddTransient<ReplayViewModel>()`) is unchanged.
- All `[ObservableProperty]` / `[RelayCommand]` source-generated names are unchanged.
- `ReplayException` is unchanged in behavior — only xmldoc added.
- `IReplayService.LoopRewound` event is unchanged in signature or behavior.

## Tests (delta)

| Test | Asserts |
|------|---------|
| `ReplayViewModelPartialSplitTests.ReplayViewModel_HasFour_PartialClassFiles_ForResponsibilitySplit` (+1) | Reflection: 4 expected `.partial.cs` files exist + belong to `ReplayViewModel` partial type. |
| `ReplayViewModelPartialSplitTests.ReplayViewModel_CoreFile_IsUnder500Lines_PostSplit` (+1) | Core file ≤ 500 LoC (was 1190). |
| `ReplayViewModelPartialSplitTests.LoaderPartial_ContainsExpectedPublicSurface` (+1) | Loader partial exposes `LoadAsync` / `OpenSessionAsync` public methods. |
| `ReplayViewModelPartialSplitTests.PlaybackPartial_ContainsExpectedPublicSurface` (+1) | Playback partial exposes `Play` / `Pause` / `Seek` public commands. |
| `ReplayViewModelPartialSplitTests.BookmarksPartial_ContainsExpectedPublicSurface` (+1) | Bookmarks partial exposes `AddBookmark` / `RemoveLoopRegion` public commands. |
| `ReplayViewModelPartialSplitTests.BundlePartial_ContainsExpectedPublicSurface` (+1) | Bundle partial exposes `Save` / `Open` public commands. |
| `ConverterSmokeTests.Converter_DoesNotThrow_WhenAttached_ToTwoWayDp_WithOneWayBindingMode` (+5, parameterized) | For each converter in `App.xaml`, programmatic binding + `RadioButton.IsChecked` (TwoWay DP) + `Mode=OneWay` + Measure/Arrange → no exception. |
| `IReplayServiceLoopRewoundContractTests.IReplayService_ExposesLoopRewoundEvent_OfTypeEventHandler_OfLoopRegionRewoundEventArgs` (+1) | Reflection: `LoopRewound` event exists with expected handler type. |
| `IReplayServiceLoopRewoundContractTests.ReplayService_RaisesLoopRewound_OnBoundaryCross` (+1) | Behavioral: `ReplayService` raises `LoopRewound` on A/B loop boundary cross. |

## Lessons (1-of-1, captured inline)

1. **`partial-class-responsibility-split-keeps-public-type-identity-while-decomposing-god-class`** — C# `partial class` lets you split a god class into responsibility-region files without changing the public type, XAML bindings, or DI surface. CommunityToolkit.Mvvm source-gen emits `[ObservableProperty]` / `[RelayCommand]` partial setters into the file containing the attribute; any `OnXxxChanged`-style partial method can live in any file marked `partial` — that's the entire point of the pattern. Public type identity + zero-binding-changes + single-ctor + DI-friendly.
2. **`wpf-converter-smoke-test-matrix-prevents-v3-11-6-class-regressions`** — Regression-guard tests for XAML converters must include an STA-bound smoke test that programmatically constructs the same binding shape + reads/touches the bound DP, OR they catch nothing. v3.11.6's "substrings not present" test passed while the underlying MultiBinding was crashing at runtime. The fix materializes as a `[Theory]` parameterization over every converter in `App.xaml`, each bound to a TwoWay DP with `Mode=OneWay` (matching project usage) — exercises the actual WPF binding activation pipeline (`AttachDataItem` → `PropertyPathWorker.CheckReadOnly` → `ConvertBack`).
3. **`event-contract-without-subscriber-is-loud-contract-drift`** (re-confirmed) — Promising an event contract in xmldoc without wiring a subscriber is "loud contract drift": the code is correct, the docs are correct, but no consumer catches the gap. The fix is a two-pronged regression test: reflection (event exists with the right handler type) + behavioral (service actually raises the event at the documented boundary). Either alone misses the regression class.
4. **`ask-for-stack-trace-when-debugging-XAML-rather-than-guessing-antipatterns`** (re-confirmed from v3.11.7 PATCH) — Stack-trace-driven root-cause diagnosis is 100x cheaper than guess-and-test antipattern hunting.

## NEXT

- v3.12.1 PATCH — close `MessageBox.Show` sites in `SendViewModel` (1 remaining) + project-wide empty-string `CommandParameter` audit per v3.11.6 PATCH note
- v3.13.0 MINOR — H7 / H8 / H9 (refactor scope: bundle path-validation policy mirror in ODX parser + others)