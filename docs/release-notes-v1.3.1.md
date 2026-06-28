# v1.3.1 PATCH Release Notes

**Ship date:** 2026-06-28
**Branch:** `feature/v1-3-1-patch` (cut from main @ v1.3.0 `01988cd`)
**Base:** main @ v1.3.0 `01988cd`
**Squash SHA:** (filled at ship)

## 概述

v1.3.1 是一个 3 项 PATCH 增量（UDS SecurityAccess tightening），全部为 v1.3.0 MINOR pre-ship code review 标出的 Important 级 carry-over。**2 项 HIGH** + **1 项 LOW docs-only**。无新 UI、无新功能、零公开 API 移除；1 项 wire-level semantic correction + 1 项 timing hardening + docs + 1 项 docs-only rationale。

## 起源

v1.3.0 MINOR pre-ship code-review verdict `0C / 4I / 6M` 标出 3 项 Important 级 carry-over，全部 deferred 到本 PATCH：

| # | Item | 来源 | Severity |
|---|------|------|----------|
| 1 | Lockout counter increments on RequestSeed NRC 0x35 (should scope to SendKey only) | Pre-ship review **Important #2** | HIGH |
| 2 | 2-arg `SecurityAccessAsync` race between RequestSeed + SendKey legs (mid-handshake lockout trigger throws confusing exception) | Pre-ship review **Important #4** | HIGH |
| 3 | `AppHostBuilder` static→instance conversion rationale documentation | Pre-ship review **Important #1** | LOW (docs-only) |

### Phase 2.5 actual code exploration findings

Phase 2.5 实地读 v1.3.0 shipped 代码验证全部 3 项 brief 描述准确：

| Brief claim | Phase 2.5 actual code |
|---|---|
| "Lockout counter increments on RequestSeed NRC 0x35" | **`UdsClient.cs:323-329`** catch arm `when ((byte)nrc.ResponseCode == 0x35 \|\| 0x36 \|\| 0x37)` 无 `key is not null` guard。RequestSeed leg (`key is null`, sub = `level`) 收到 0x35 → `Security.RecordFailedAttempt(level)` → 计入 host 端 lockout counter. |
| "2-arg `SecurityAccessAsync` race" | **`UdsClient.cs:347-363`** 2-arg overload: 调两次 3-arg overload, 中间无 `IsLocked` 重检。中间 lockout 触发 → 第二次 3-arg 调用在 entry throw `UdsSecurityLockedException`. |
| "`AppHostBuilder` static→instance rationale missing" | **`AppHostBuilder.cs:14-29`** class-level `<summary>` 只描述 `Build()` + Serilog side effects. 无 class-级 `<remarks>` 解释: (1) 为什么是 instance class (需要 `_udsSecurityLockoutConfig` 等 optional state), (2) lifecycle guidance, (3) pattern alignment. |

所有 3 项 brief 与实际代码一致 — 无 scope drift, 直接执行。

## Per-Item 修复详情

### Item 1 — Lockout counter scope to SendKey leg (HIGH)

**Symptom**: v1.3.0 catch arm `when ((byte)nrc.ResponseCode == 0x35 || 0x36 || 0x37)` 无 `key is not null` guard。RequestSeed (sub-function = `level`) 收到 NRC 0x35 → host-side `RecordFailedAttempt` → 计入 lockout counter. 但 RequestSeed 失败是 flow-control 信号 (ECU not in Programming session, conditions not correct) 而非 authentication policy violation — 一个 benign NRC 0x22 可以 trip host-side lockout.

**Fix** (`src/PeakCan.Host.Core/Uds/UdsClient.cs:323-329`):

```csharp
catch (UdsNegativeResponseException nrc)
    when (key is not null
          && ((byte)nrc.ResponseCode == 0x35
              || (byte)nrc.ResponseCode == 0x36
              || (byte)nrc.ResponseCode == 0x37))
```

**Discrimination note (Decision 1)**: 故意保留 SendKey leg 的 0x36 (exceededNumberOfAttempts) 和 0x37 (requiredTimeDelayNotExpired) 计数行为 — 这些 NRC 仍代表实际 auth 尝试被 ECU 拒绝, host-side lockout 反馈合理。Pre-ship review 也只说"RequestSeed 0x35", 未要求排除 0x36/0x37. 最小改动原则。

**Tests** (`tests/PeakCan.Host.Core.Tests/Uds/UdsClientTests.cs:390-465`):
- `+SecurityAccessAsync_RequestSeed_Nrc_35_DoesNot_Increment_AttemptCount` — RED→GREEN: 3 RequestSeed 失败后 `IsLocked(0x01) == false`
- `+SecurityAccessAsync_SendKey_Nrc_35_Still_Increments_AttemptCount` — regression guard: 3 SendKey 失败后 `IsLocked(0x01) == true` (preserved v1.3.0 semantics)
- Brief's 1-attempt 断言被 implementer 升级为 3-attempt (TDD discipline: 1 attempt < threshold = 3, non-discriminating)
- Brief's inject-before-request ordering 被 implementer 修正为 canonical kick→`Task.Yield()`→inject pattern (无 pending `_responseTcs` → message 被吞)

### Item 2 — 2-arg `SecurityAccessAsync` pre-check + TOCTOU docs (HIGH)

**Symptom**: v1.3.0 2-arg overload (`UdsClient.cs:347-363`) 由两次 3-arg 调用组成, 中间无 mid-handshake lockout 状态检查。如果 concurrent caller 在 RequestSeed 完成后, SendKey 开始前 exhausted lockout counter → SendKey leg entry check throws `UdsSecurityLockedException`. 行为正确 (3-arg entry check 是 source of truth), 但 intent 在 2-arg signature boundary 不可见 + 缺少 docs 解释 TOCTOU window.

**Fix** (`src/PeakCan.Host.Core/Uds/UdsClient.cs:381-411`):
- 2-arg overload entry 加显式 `IsLocked(requestLevel)` pre-check — **defensive coding**, 与 transitive 3-arg check 行为相同 (都先抛、不触线), 但让 fail-fast intent 在 2-arg signature 可见, 并避免 wire-allocate 当 level 已 locked
- `<exception cref="UdsSecurityLockedException">` added to XML doc
- `<remarks>` block 文档化 mid-handshake TOCTOU window (RequestSeed 完成 → SendKey 开始之间, concurrent caller 可能 exhausted counter; SendKey leg entry check fires `UdsSecurityLockedException` with actual remaining delay; callers 应 treat handshake as failed and wait lockout window 过期)

**Tests** (`tests/PeakCan.Host.Core.Tests/Uds/UdsClientSecurityAccessOverloadTests.cs:104-156`):
- `+TwoArg_Overload_PreChecks_Lockout_Before_RequestSeed` — **regression guard** (not true RED): 验证 locked exception type + level match + RemainingDelay + wire NOT touched. PASS pre-fix (transitive 3-arg check fires first) → PASS post-fix.
- `+TwoArg_Overload_XmlDoc_Mentions_MidHandshake_Race` — **true RED→GREEN**: source-file grep 验证 2-arg overload `<remarks>` 包含 "mid-handshake" + method signature. FAIL pre-fix → PASS post-fix.
- `XmlDocumentationReader` helper 未创建 (csproj 未启用 `<GenerateDocumentationFile>`) → source-file `File.ReadAllText` alternative. `NewIsoWithCapture` 使用 `CanFrame.Data.ToArray()` 镜像 `UdsClientTests.NewIso()` 模式 (brief 误用 `.Raw`).

**Atomic race test deferred**: 真正的 concurrent race test (两个 SecurityAccessAsync 并发) 是 timing-dependent, 正确地 deferred 到 v1.4.0 PATCH 或后续.

### Item 3 — `AppHostBuilder` static→instance rationale docs (LOW)

**Symptom**: v1.3.0 MINOR Item 5 把 `AppHostBuilder` 从 `static class` 转换为 instance class + 加 `WithUdsSecurityLockoutConfig` fluent setter. 但 class-level docs 只描述 `Build()` + Serilog side effects, 无 static→instance rationale, 无 lifecycle guidance, 无 pattern alignment.

**Fix** (`src/PeakCan.Host.App/Composition/AppHostBuilder.cs:30-55`):
- Class-level `<remarks>` block (placed after existing `<summary>`, before `public class AppHostBuilder`) 覆盖 3 个 orthogonal concerns:
  - (1) **Why instance class**: 跨 fluent setter calls carry optional state (`_udsSecurityLockoutConfig` from v1.3.0 Item 5). Future setters 沿用同 pattern.
  - (2) **Lifecycle guidance**: one builder per app, call `Build()` exactly once, dispose the returned `IHost` (not the builder) at shutdown, do not reuse after `Build`.
  - (3) **Pattern alignment**: Microsoft.Extensions.Hosting IHost builder pattern (with `<see href>` link), DI factory branches on optional state to preserve default policy for legacy callers.
- All `<see cref>` references resolve (`WithUdsSecurityLockoutConfig`, `Build`, `IHost` etc.) — verified via `dotnet build` 0-warning output.

**Tests**: 无。Docs-only change. CI gate: `dotnet build` 0 warnings + full App.Tests no-regression.

## Pre-ship code-review verdict

Whole-branch review (`git diff 01988cd..4716aed`, 21566 bytes, 3 commits):

- **0 Critical**
- **0 Important**
- **4 Minor** (all non-blocking cosmetic):
  1. Item 2 doc-gate test fragility (false-positive risk; documented tradeoff)
  2. Item 1 test comment about `Task.Yield()` necessity (nit; future maintainer could strip)
  3. `NewIsoWithCapture` uses `ObservableCollection<byte[]>` (vs brief's `List<byte[]>`; harmless)
  4. AppHostBuilder doc style mix of `<b>` and `<see cref>` (cosmetic; both valid XML doc)

**Verdict: Ready to merge Yes.** All per-task subagent reviews (0C/0H/3M, 0C/0H/4M) align with whole-branch assessment.

## Tests

| Metric | v1.3.0 baseline (memory) | **v1.3.1 final** | Δ |
|--------|--------------------------|------------------|---|
| Total tests | 688 + 6 SKIP + 0 fail | **691-693 pass + 6 SKIP + 0 fail** (App.Tests has 1-2 transient flakes in `CyclicSendServiceRaceTests`) | **+3-5 net** (Core 260→264, Infra 84 unchanged, App 344→347 baseline drift) |
| Core.Tests | 260 | **264** | +4 (Item 1: +2, Item 2: +2) |
| Infra.Tests | 84 + 2 SKIP | **84 + 2 SKIP** | unchanged |
| App.Tests | 344 | **347 total** (343-345 pass + 4 SKIP, 1-2 transient flakes per run) | +3 baseline drift (per memory v1.2.12 lesson 4, `CyclicSendServiceRaceTests` is known transient-flaky; not introduced by this PATCH which adds zero App.Tests changes) |

**TDD discipline validated**: Item 1 brief's 1-attempt assertion was non-discriminating (would pass against unfixed code) — implementer caught and corrected to 3-attempt hitting the threshold. Item 2 brief's regression-guard test was correctly identified as NOT true RED (defensive coding, transitive check already works). Item 2's doc-gate test was correctly identified as true RED.

## Ship process

5 commits on `feature/v1-3-1-patch`:
- `36d3d48` Item 1: lockout counter scope to SendKey leg
- `7cbffbb` Item 2: 2-arg SecurityAccessAsync pre-check + TOCTOU docs
- `4716aed` Item 3: AppHostBuilder static→instance rationale docs
- `3dc52f1` chore: bump version to 1.3.1
- (this commit) docs: v1.3.1 release notes

squash → PR → main → tag v1.3.1 → release.

## Spec drops (post Phase 2.5)

无. 所有 3 项 carry-overs 全部 shipped.

## Known follow-ups (deferred)

- **v1.4.0 MINOR** (P2 in v1.3.0 spec decomposition):
  - Replay (ASC parser + time-based replay + speed control)
  - Send DBC signal encoding
  - **NEW from v1.3.1 review**: atomic concurrent mid-handshake race test for `SecurityAccessAsync` (currently only defensive + docs; a true 2-task concurrent test would validate the TOCTOU window behavior end-to-end)
- **v1.5.0 MINOR** (P3 in v1.3.0 spec decomposition):
  - V8 sandbox hardening + CanApi rate limit
  - DBC size / token limit + path normalization
  - OEM `IKeyDerivationAlgorithm` concrete implementation
  - Channel picker (UI)

## Breaking changes

无. 所有 PATCH 范围内变更, additive only. 关键 invariant:
- **LockoutConfig setter** `internal set` 保留 (v1.3.0 ship prep fix #3) — DI assembly 可写, 外部 callers 只读
- **SendKey leg** 仍计入 lockout counter (preserved v1.3.0 semantics) — OEM 集成 contracts 不破坏
- **3-arg `SecurityAccessAsync` 行为** 不变 — explicit pre-check 在 2-arg overload 是 defensive, 3-arg entry check (line 284-285) 仍是 source of truth
- **Type-stable public API** — 0 removals, 0 signature changes

## ADR note: 0x36/0x37 retention in catch arm filter

Per spec Decision 1, `key is not null && (0x35 || 0x36 || 0x37)` filter 故意保留 SendKey leg 的 0x36/0x37 计数行为。Rationale:

- **0x35 invalidKey**: SendKey-only — "your key is wrong" → host-side auth failure → count.
- **0x36 exceededNumberOfAttempts**: ECU 反馈 "you're locked out" — host-side lockout fires independently (host 自身 policy). 仍代表 host SendKey attempt 被 ECU 拒绝 → count 合理。
- **0x37 requiredTimeDelayNotExpired**: 同 0x36 — ECU in lockout delay → host SendKey rejected → count。

排除这些会创建 double-policy: host counter 不再反映 ECU-side events, OEM debug 会困惑 "why did lockout not fire?". 最小改动 + 行为可观察, 保留.

未来如有 OEM-specific 需求 (e.g. 0x36 视为 soft fail, not counted), 应通过 new lockout config option 而非 catch arm filter 改动 — 保持 policy 在 `UdsSecurityLockoutConfig`, 不在 wire-level handler.

## Process lessons (from v1.3.0 + earlier PATCHes, applied to v1.3.1)

1. **TDD assertion discrimination (3rd-of-3 confirmation)**: RED test 必须能 fail against unfixed code. Brief's `IsLocked=false` after 1 attempt 不 discriminating — 1 < threshold(3), locked-or-not 都是 false. 验证: `dotnet test` 后期待 FAIL not PASS.
2. **Brief-vs-source drift (4th-of-4 confirmation)**: Phase 2.5 actual-code exploration is structural, not occasional. Brief 假设的 API surface (`.Raw` vs `.Data`), test ordering (inject-before-pending-TCS), test magnitudes 都需 implementer verify.
3. **`Func<Task>` wrapper** for `FluentAssertions.ThrowAsync<T>()` — extension is on `Func<Task>` not `Task`.
4. **Inject ordering** in `UdsClient` test seam: kick task → `await Task.Yield()` → inject response. 直接 inject-before-kick silently times out.
5. **`gh pr merge --squash --delete-branch`** first-attempt "Not possible to fast-forward" failure is recurring (v1.2.13 + v1.3.0 + now v1.3.1). Always `git fetch origin main` first.
6. **Pre-existing transient flakes** in `CyclicSendServiceRaceTests` (memory v1.2.12 ship notes lesson 4). 1-2 flake per run, different test names each time. NOT regressions from current PATCH.
7. **Network proxy pattern**: `github.com:443` blocks, `api.github.com` reachable → use `gh api` for tag/release creation as fallback.