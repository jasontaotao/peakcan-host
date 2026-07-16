# v3.52.1 PATCH Implementation Plan — AI 推理 v1 cleanup

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** v3.52.0 MINOR ship 后的姐妹 pattern hardening PATCH：移除 IFrameSourceProvider 注册的显式 cast + mock 化 test fixtures + RunAnalysisCommand CanExecute refresh。

**Architecture:** 4 个 micro-task，全部本地化（不引入新边界、不改 public API、不改 UI）。变更集中在 DI 注册 + 1 行 NotifyCanExecuteChanged + 测试 fixture mock 化。

**Tech Stack:** C# .NET 10 / WPF / CommunityToolkit.Mvvm / NSubstitute / FluentAssertions / xUnit

## Global Constraints

来自 `docs/superpowers/specs/2026-07-17-v3-52-1-patch-cleanup-design.md`：

- 不引入新网络/安全/解析边界（沿用 v3.52.0 14 hard-boundary）
- DI 注册必须保持单例（concrete-first + dual forward，**不**允许重复实例化 `TraceSessionRegistry`）
- `RunAnalysisCommand.NotifyCanExecuteChanged()` 必须在 `LockAnchor` 写完 `CurrentAnchorSnapshot` 后立即调
- test mock 化必须用 NSubstitute（项目既有 sister pattern），不引入 Moq / FakeItEasy
- Minor 4 trailing-newline 明确不做
- 不改 public API surface（`ILlmProvider`、`AnchorSnapshotFlow`、`AnalysisFlow` 签名不变）
- v3.52.0 spec + plan 必须 0 改动
- 测试覆盖率 ≥ 80%（per-project rule；本 PATCH 不增加 LoC 故覆盖率不会下降）
- pkm-capture 节流：仅在 ship-completion 时 dispatch

## File Structure

修改文件（5 个）：

| 文件 | LoC delta | 改动 |
|---|---|---|
| `src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs` | +4 | DI: concrete-first dual forward |
| `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnchorSnapshotFlow.cs` | +1 | `RunAnalysisCommand.NotifyCanExecuteChanged()` after snapshot write |
| `tests/PeakCan.Host.App.Tests/ViewModels/AnalysisFlowTests.cs` | +4 (mock) +5 (new test) | 5 dependencies mock + 1 new test |
| `tests/PeakCan.Host.App.Tests/ViewModels/AnchorSnapshotFlowTests.cs` | +1 (mock) | 2 dependencies mock in helper |

总增量 ~+15 LoC（spec 估算 10；实际包括 new test 的 5 行）。

---

### Task 0: Branch + spec verify + baseline

**Files:**
- Branch from main at current HEAD `36b29c4`
- Verify spec file at `docs/superpowers/specs/2026-07-17-v3-52-1-patch-cleanup-design.md`

- [ ] **Step 1: Verify spec commit exists**

Run:
```bash
git log --oneline -3
git show --stat 36b29c4
```
Expected: commit `36b29c4` is visible with spec file +137 insertions.

- [ ] **Step 2: Create feature branch**

```bash
git checkout -b feature/v3-52-1-patch-cleanup main
```

- [ ] **Step 3: Verify baseline build is green**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```
Expected: 0 errors, 0 warnings on touched code.

- [ ] **Step 4: Verify baseline test is green**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~AnalysisFlowTests|FullyQualifiedName~AnchorSnapshotFlowTests" --logger "console;verbosity=minimal"
```
Expected: 6 tests pass (4 AnchorSnapshot + 2 Analysis).

- [ ] **Step 5: Commit plan**

```bash
git add docs/superpowers/plans/2026-07-17-v3-52-1-patch-cleanup.md
git commit -m "v3.52.1 plan: AI 推理 v1 cleanup (4 micro-tasks: DI cast removal + NotifyCanExecuteChanged + 2 test mocks; TDD)"
```

---

### Task 1: DI cast mitigation — concrete-first dual forward

**Files:**
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs` (lines 170-172 region; verify with grep)

**Interfaces:**
- Consumes: existing `TraceSessionRegistry` (implements both `ITraceSessionRegistry` and `IFrameSourceProvider`)
- Produces: DI registration that removes explicit cast while keeping single-instance guarantee

- [ ] **Step 1: Find current registration**

Run:
```bash
grep -n "IFrameSourceProvider\|ITraceSessionRegistry" src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs
```
Expected: shows lines around 170-172.

- [ ] **Step 2: Modify the 3 registration lines**

Replace the existing pattern:
```csharp
services.AddSingleton<ITraceSessionRegistry, TraceSessionRegistry>();
services.AddSingleton<IFrameSourceProvider>(sp =>
    (TraceSessionRegistry)sp.GetRequiredService<ITraceSessionRegistry>());
```
With:
```csharp
// v3.52.1 PATCH D1: concrete-first dual forward. Removes explicit cast
// at IFrameSourceProvider registration (was Minor 1 in v3.52.0 final review).
// All 3 keys resolve to the same singleton instance — no double-allocation.
services.AddSingleton<TraceSessionRegistry>();
services.AddSingleton<ITraceSessionRegistry>(sp =>
    sp.GetRequiredService<TraceSessionRegistry>());
services.AddSingleton<IFrameSourceProvider>(sp =>
    sp.GetRequiredService<TraceSessionRegistry>());
```

- [ ] **Step 3: Build verify**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```
Expected: 0 errors, 0 warnings.

- [ ] **Step 4: Test verify (regression)**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-build --nologo -c Debug --filter "FullyQualifiedName~AnalysisFlowTests|FullyQualifiedName~AnchorSnapshotFlowTests" --logger "console;verbosity=minimal"
```
Expected: 6 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs
git commit -m "v3.52.1 T1: DI cast mitigation — concrete-first dual forward (removes IFrameSourceProvider explicit cast)"
```

---

### Task 2: NotifyCanExecuteChanged for RunAnalysisCommand

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnchorSnapshotFlow.cs` (LockAnchor body)

**Interfaces:**
- Consumes: existing `RunAnalysisCommand` (generated by `[RelayCommand(CanExecute = nameof(CanRunAnalysis))]` on `AnalysisFlow.RunAnalysisAsync`)
- Produces: explicit refresh trigger after `CurrentAnchorSnapshot` write

- [ ] **Step 1: Find LockAnchor body**

Run:
```bash
grep -n "CurrentAnchorSnapshot = new AnchorSnapshot\|OnPropertyChanged(nameof(CurrentAnchorSnapshot))" src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnchorSnapshotFlow.cs
```

- [ ] **Step 2: Add explicit NotifyCanExecuteChanged call**

Modify the `LockAnchor()` method body. After:
```csharp
CurrentAnchorSnapshot = new AnchorSnapshot(...);
OnPropertyChanged(nameof(CurrentAnchorSnapshot));
ErrorMessage = null;
```

Insert between `OnPropertyChanged` and `ErrorMessage`:
```csharp
// v3.52.1 PATCH D3: explicit CanExecute refresh for RunAnalysisCommand.
// Minor 3 from v3.52.0 review — RunAnalysisCommand's CanExecute reads
// CurrentAnchorSnapshot, but AnchorSnapshot is set here in the LockAnchor
// partial (not in a property setter). Without this trigger, the UI button
// stays disabled until something else changes (e.g., IsLoading flip).
RunAnalysisCommand.NotifyCanExecuteChanged();
```

- [ ] **Step 3: Build + test verify**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-build --nologo -c Debug --filter "FullyQualifiedName~AnchorSnapshotFlowTests|FullyQualifiedName~AnalysisFlowTests" --logger "console;verbosity=minimal"
```
Expected: 0 errors, 6 PASS.

- [ ] **Step 4: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnchorSnapshotFlow.cs
git commit -m "v3.52.1 T2: AnchorSnapshotFlow — RunAnalysisCommand.NotifyCanExecuteChanged after LockAnchor (Minor 3 fix)"
```

---

### Task 3: Test fixture mock 化 (AnalysisFlowTests + AnchorSnapshotFlowTests)

**Files:**
- Modify: `tests/PeakCan.Host.App.Tests/ViewModels/AnalysisFlowTests.cs` (MakeVm helper + new test)
- Modify: `tests/PeakCan.Host.App.Tests/ViewModels/AnchorSnapshotFlowTests.cs` (MakeVm helper)

**Interfaces:**
- Consumes: existing `EvidenceExtractor`, `LocalAnalyzer`, `AnalysisSessionRegistry`, `ILlmProvider`, `IFrameSourceProvider`
- Produces: all 5 analysis dependencies mocked via `Substitute.For<>`; new test verifying T2 fix

- [ ] **Step 1: Modify AnalysisFlowTests MakeVm helper**

In `tests/PeakCan.Host.App.Tests/ViewModels/AnalysisFlowTests.cs`, find `MakeVm()`. Replace the 5 `new ...()` calls with `Substitute.For<...>()`:

```csharp
private static TraceViewerViewModel MakeVm()
{
    var registry = Substitute.For<ITraceSessionRegistry>();
    var frameSource = Substitute.For<IFrameSourceProvider>();
    var dbc = Substitute.For<DbcService>();
    var sessionLib = Substitute.For<TraceSessionLibrary>(null, null);
    var logger = NullLogger<TraceViewerViewModel>.Instance;
    frameSource.GetFrames(Arg.Any<string>()).Returns(Array.Empty<ReplayFrame>());

    return new TraceViewerViewModel(
        registry, dbc, logger, sessionLib,
        Substitute.For<EvidenceExtractor>(),
        Substitute.For<LocalAnalyzer>(),
        Substitute.For<AnalysisSessionRegistry>(),
        Substitute.For<ILlmProvider>(),
        frameSource);
}
```

- [ ] **Step 2: Modify AnchorSnapshotFlowTests MakeVm helper**

In `tests/PeakCan.Host.App.Tests/ViewModels/AnchorSnapshotFlowTests.cs`, find `MakeVm()`. The helper builds VM with only 4 args currently (no analysis params since ctor has nullable defaults with lazy stubs). Update to pass mocks explicitly:

```csharp
private static TraceViewerViewModel MakeVm(bool greenSet, bool blueSet)
{
    var registry = Substitute.For<ITraceSessionRegistry>();
    var dbc = Substitute.For<DbcService>();
    var sessionLib = Substitute.For<TraceSessionLibrary>(null, null);
    var logger = NullLogger<TraceViewerViewModel>.Instance;

    var vm = new TraceViewerViewModel(registry, dbc, logger, sessionLib,
        Substitute.For<EvidenceExtractor>(),
        Substitute.For<LocalAnalyzer>(),
        Substitute.For<AnalysisSessionRegistry>(),
        Substitute.For<ILlmProvider>(),
        Substitute.For<IFrameSourceProvider>());
    if (greenSet) vm.RefreshAtAnchor(1.0);
    if (blueSet) vm.RefreshAtAnchorBlue(1.5);
    return vm;
}
```

- [ ] **Step 3: Run existing tests verify (RED→GREEN)**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~AnchorSnapshotFlowTests|FullyQualifiedName~AnalysisFlowTests" --logger "console;verbosity=minimal"
```
Expected: 6 PASS (4 + 2; same as before — mocks preserve behavior).

- [ ] **Step 4: Add new test for T2 fix verification**

In `tests/PeakCan.Host.App.Tests/ViewModels/AnalysisFlowTests.cs`, add new test:

```csharp
[Fact]
public async Task RunAnalysisCommand_CanExecute_RefreshedAfterLockAnchor()
{
    // v3.52.1 PATCH T2: LockAnchor must explicitly notify RunAnalysisCommand
    // (Minor 3 from v3.52.0 review). Without this trigger, the UI Run button
    // stays disabled even after the user has created a valid snapshot.
    var vm = MakeVm();
    Assert.False(vm.RunAnalysisCommand.CanExecute(null),
        "before LockAnchor, no snapshot → CanExecute=false");

    vm.RefreshAtAnchor(1.0);
    vm.RefreshAtAnchorBlue(1.5);
    vm.LockAnchorCommand.Execute(null);

    Assert.True(vm.RunAnalysisCommand.CanExecute(null),
        "after LockAnchor creates snapshot, CanExecute=true");
}
```

NOTE: This test does NOT call `RunAnalysisAsync` — it only verifies `CanExecute` returns true after LockAnchor. The mock EvidenceExtractor + LocalAnalyzer are not exercised in this test path.

- [ ] **Step 5: Run all tests verify (GREEN)**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~AnchorSnapshotFlowTests|FullyQualifiedName~AnalysisFlowTests" --logger "console;verbosity=minimal"
```
Expected: 7 PASS (4 + 2 + 1 new).

- [ ] **Step 6: Commit**

```bash
git add tests/PeakCan.Host.App.Tests/ViewModels/AnalysisFlowTests.cs \
        tests/PeakCan.Host.App.Tests/ViewModels/AnchorSnapshotFlowTests.cs
git commit -m "v3.52.1 T3: test fixture mock + new CanExecute-refresh test (Minor 2 + T2 verification)"
```

---

### Task 4: Full CI + release notes + tier-3 ship

**Files:**
- Run full solution build + test
- Create `docs/release-notes-v3.52.1.md`
- Bump version in `src/Directory.Build.props`
- Push + PR + squash + tag + GH release

- [ ] **Step 1: Full solution build**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore
```
Expected: 0 errors, 0 warnings on touched code.

- [ ] **Step 2: Full solution test**

```bash
dotnet test PeakCan.Host.slnx --no-build --nologo -c Debug --logger "console;verbosity=minimal"
```
Expected: 1405 PASS / 0 FAIL / 5 SKIP (1404 + 1 new test).

- [ ] **Step 3: Write release notes**

Create `docs/release-notes-v3.52.1.md` (cover 3 Minor fixes + 4 NEW 1/3 lesson candidates + 6 deferred from v3.52.0 still pending).

- [ ] **Step 4: Bump version**

In `src/Directory.Build.props`:
```xml
<Version>3.52.1</Version>
<AssemblyVersion>3.52.1.0</AssemblyVersion>
<FontVersion>3.52.1.0</FontVersion>
<InformationalVersion>3.52.1</InformationalVersion>
```
(NOTE: replace `<FontVersion>` with `<FileVersion>` — typo in template above; use existing field name)

- [ ] **Step 5: Tier-3 ship**

```bash
git add docs/release-notes-v3.52.1.md src/Directory.Build.props
git commit -m "v3.52.1: version bump + release notes (AI 推理 v1 cleanup PATCH)"
git push -u origin feature/v3-52-1-patch-cleanup
gh pr create --base main --title "v3.52.1 PATCH: AI 推理 v1 cleanup (3 Minor: cast mitigation + test fixture mock + RunAnalysisCommand CanExecute refresh)" --body-file docs/release-notes-v3.52.1.md
gh pr merge --squash --delete-branch
git tag -a v3.52.1 -m "v3.52.1 PATCH — Trace Viewer AI 推理 v1 cleanup"
git push origin v3.52.1
gh release create v3.52.1 --notes-file docs/release-notes-v3.52.1.md
```

---

## Sister-lesson candidates to monitor

| Lesson | Status | What T1-T3 might observe |
|---|---|---|
| `concrete-first-dual-interface-di-registration-when-cast-was-needed` | NEW 1/3 | T1 verified (cast removal, single-instance preserved) |
| `run-analysis-command-can-execute-refresh-must-trigger-from-action-not-from-setter` | NEW 1/3 | T2 verified (action point explicit trigger) |
| `test-fixtures-should-mock-analysis-pipeline-instead-of-instantiating-real` | NEW 1/3 | T3 verified (5 mocks, tests still pass) |
| `notify-can-execute-changed-after-snapshot-write-is-cross-partial-coordination` | NEW 1/3 | T2 + T3 verified (AnchorSnapshotFlow → AnalysisFlow cross-partial) |

## Verification

- `dotnet build PeakCan.Host.slnx`: 0 errors, 0 warnings on touched code
- `dotnet test PeakCan.Host.slnx`: 1405 PASS / 0 FAIL / 5 SKIP (1404 + 1 new)
- No `ITraceSessionRegistry` direct cast in DI registration (only `GetRequiredService<TraceSessionRegistry>()`)
- `RunAnalysisCommand.NotifyCanExecuteChanged()` called exactly once per `LockAnchor` (action point)
- All 5 analysis dependencies in test helpers are `Substitute.For<>` (not `new ...()`)
- Tag v3.52.1 + GH release published

## Out of scope (YAGNI)

- Minor 4 trailing-newline (pre-existing mixed baseline) — explicitly NOT in this PATCH
- Real LLM Provider implementations — P1 PATCH
- W36 god-class refactor — separate work
- Hard-boundary #13 Evidence ID whitelist — P1 PATCH
- `.editorconfig` for trailing newline enforcement — separate config PATCH