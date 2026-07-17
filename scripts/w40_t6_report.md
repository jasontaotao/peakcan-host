# W40 T6 Report — ApiKeyManager ctor arg fixup across 15 test files

**Task:** W40 T6 (AMENDED) — Add `Substitute.For<ApiKeyManager>()` ctor arg to
all 88 callsites across 15 test files.

**Base:** `1aafd0d` (W40 T5 VM ctor break)
**Head:** `73f3f06` (T6 commit)
**Branch:** `feature/w40-p2-api-key-ui`

## Files modified (15 test files + 1 src file)

### Source change

- `src/PeakCan.Host.App/Services/AnalysisApiKey/ApiKeyManager.cs`
  - **Unsealed** the class (was `public sealed class`, now `public class`)
  - Added a `protected` parameterless ctor for `Castle.DynamicProxy` / NSubstitute
    proxy generation.

### 15 test files (1 ctor arg insertion each, 88 total)

- `tests/PeakCan.Host.App.Tests/AppLifecycleShutdownTests.cs` (1 callsite)
- `tests/PeakCan.Host.App.Tests/Services/Trace/TraceSessionAutoSaverTests.cs` (1)
- `tests/PeakCan.Host.App.Tests/ViewModels/AnalysisFlowTests.cs` (1)
- `tests/PeakCan.Host.App.Tests/ViewModels/AnchorSnapshotFlowTests.cs` (1)
- `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelMessageBoxPromptTests.cs` (1)
- `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs` (6)
- `tests/PeakCan.Host.App.Tests/ViewModels/EventSubscriptionLeakTests.cs` (1)
- `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModel/GreenLineAnchorFlowTests.cs` (1)
- `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelCanIdFilterTests.cs` (4)
- `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelChartWiringTests.cs` (3)
- `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelFixtureIntegrationTests.cs` (3)
- `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelMultiTraceTests.cs` (5)
- `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelRebuildSignalsTests.cs` (9)
- `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs` (50)
- `tests/PeakCan.Host.App.Tests/Windows/UdsWindowTests.cs` (1)

**Total: 88 callsites across 15 test files** (1 callsite discrepancy from
plan's "88" estimate was a multi-line callsite wrap — script's regex count
matched the 88 directly).

Each callsite received:
- A final `apiKeyManager: Substitute.For<PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager>()`
  argument (named arg form to avoid CS1503 / CS8323 errors with the
  4-positional + named-`fileDialog:` call patterns)
- The `using PeakCan.Host.App.Services.AnalysisApiKey;` directive was
  added at the top where missing (not strictly required since the type is
  fully qualified in the Substitute arg)

## Sister-pattern lessons reaffirmed / promoted

### NEW 1/3 candidate → 1/3 confirmed

- **`sealed-class-cannot-be-proxied-by-castle-dynamicproxy-empirical-w36-w40`**
  — sister of v3.52.1 T3 (unseal `EvidenceExtractor` / `LocalAnalyzer` /
  `AnalysisSessionRegistry`). W40 T6 1st observation. W40 1st + v3.52.1
  PATCH + (potentially) the omitted v3.52.0 baseline = 2-3 confirmations.

### LOCKED lesson (re-applied verbatim per W19 R1 0-failure discipline)

- **`add-parameterless-ctor-before-mocking-sealed-class-with-nsubstitute`** —
  1st observation: W40 needed both `unsealed` AND `protected parameterless
  ctor` for `Substitute.For<ApiKeyManager>()` to compile. The
  parameterless ctor pattern itself is unrelated to the unsealing pattern;
  both were required here (Castle needs to instantiate the proxy target
  via parameterless ctor because the public ctor needs 2 DI deps).

## Sister-pattern lesson candidates captured at T6

| Lesson | Status | Observation |
|---|---|---|
| `ctor-arg-insertion-must-use-named-form-when-callsites-have-named-args` | NEW 1/3 (W40) | First observation: 4-arg positional + `fileDialog:` named arg + new positional arg would violate C# CS8323 (named-must-precede-positional). All 88 sites use named `apiKeyManager:` form. |

## Concerns

- **Original T6 plan listed 1 file; actual scope was 88 callsites / 15 files.**
  The plan's T6 description said "TraceViewerViewModelTests.cs" but the
  T5 break actually affected every test file that constructs the VM.
  The reviewer (T5 reviewer grep) correctly identified this and the T6
  amendment followed. Plan file should be retroactively corrected to
  reflect the 15-file scope in a follow-up patch.

- **One transient flake observed in full-solution test run**: PeakCan.Host.Core.Tests
  reported 1 failure in one solution-level run, but 521/521 PASS in 3
  consecutive isolated runs. Pattern matches documented W19 R1 / W34
  transient-flake sister precedent — NOT a T6 regression. No Core.Tests
  was touched.

## Verification

```text
$ dotnet build tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --no-restore
... 0 errors, 0 warnings (W40 T6)

$ dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj \
    --no-restore --nologo -c Debug \
    --filter "FullyQualifiedName~TraceViewerViewModel|FullyQualifiedName~AppShellViewModel|FullyQualifiedName~UdsWindow|FullyQualifiedName~AnchorSnapshot|FullyQualifiedName~AnalysisFlow|FullyQualifiedName~TraceSessionAutoSaver|FullyQualifiedName~AppLifecycleShutdown|FullyQualifiedName~EventSubscriptionLeak|FullyQualifiedName~GreenLineAnchor"
... 165 PASS / 0 FAIL / 3 SKIP

$ dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug
Run 1: 849 PASS / 0 FAIL / 3 SKIP
Run 2: 849 PASS / 0 FAIL / 3 SKIP
Run 3: 849 PASS / 0 FAIL / 3 SKIP

$ dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug
... 521 PASS / 0 FAIL / 0 SKIP (3 independent runs)
```

## Deviation from plan

- **Scope: 15 files / 88 callsites** (vs plan's "1 file"). Stated in T6
  amendment prompt.
- **`ApiKeyManager` was `public sealed`** → unsealed + `protected` parameterless
  ctor added in production src. Strictly required for `Substitute.For<ApiKeyManager>()`.
  Sister of v3.52.1 T3.
- **Used named-arg form `apiKeyManager: ...` instead of plain positional** to
  avoid CS8323 (named arg misuse) on sites that already use named
  `fileDialog:` and CS1503 (positional slot collision) on 4-arg-form sites
  that fill `EvidenceExtractor?` slot 5.

STATUS: DONE
COMMITS: 1aafd0d..73f3f06
TEST_SUMMARY: 165/165 PASS in targeted filter; 849/849 PASS in full App.Tests (3 SKIP, 3 runs); 521/521 PASS in Core.Tests (3 runs)
FILES_MODIFIED: AppLifecycleShutdownTests.cs, TraceSessionAutoSaverTests.cs, AnalysisFlowTests.cs, AnchorSnapshotFlowTests.cs, AppShellViewModelMessageBoxPromptTests.cs, AppShellViewModelTests.cs, EventSubscriptionLeakTests.cs, GreenLineAnchorFlowTests.cs, TraceViewerViewModelCanIdFilterTests.cs, TraceViewerViewModelChartWiringTests.cs, TraceViewerViewModelFixtureIntegrationTests.cs, TraceViewerViewModelMultiTraceTests.cs, TraceViewerViewModelRebuildSignalsTests.cs, TraceViewerViewModelTests.cs, UdsWindowTests.cs, ApiKeyManager.cs
CONCERNS: 4 pre-existing CRLF→LF dirty doc files in v1-6-5/v1-6-6 plans stashed (not W40 T6 work); plan T6 scope originally listed 1 file (should reflect 15).
