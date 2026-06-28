# v1.3.0 MINOR Release Notes

**Ship date:** 2026-06-28
**Branch:** `feature/v1-3-0-minor` (cut from main @ v1.2.14 `680712e`)
**Base:** main @ v1.2.14 `680712e`
**Squash SHA:** (filled at ship)

## 概述

v1.3.0 是一个 5 项 MINOR 增量（UDS 协议完成），起源于 v1.2.13 + v1.2.14 PATCH ship notes 列的 8 项 deferred scope，经 Phase 2.5 actual-code exploration 后 3 项 drop 为 no-op。**1 项 MEDIUM** + **4 项 LOW**。无新 UI、无新功能、零公开 API 移除；全部为 UDS 协议增强（ISO 14229-1 conformance）。

## 起源

8 项 deferred scope 拆 3 MINOR release：
- **v1.3.0** (this spec): UDS protocol completion — 最小、明确、对 OEM 集成合作伙伴是 conformance 信号
- v1.4.0: Replay + Send DBC signal encoding (大 feature, marketing 卖点)
- v1.5.0: V8 sandbox + DBC limits + OEM Key concrete + Channel picker (防御深度 + UX)

按 **协议完成度 → 用户价值 → 安全** 顺序拆，安全 release 与 feature release 隔离便于回滚。

### Phase 2.5 actual code exploration findings

3 个 Explore agent 实地读 `UdsClient.cs` + `UdsSecurity.cs` + tests/ 验证 deferred brief：

| Brief | Phase 2.5 actual |
|-------|------------------|
| SecurityAccess attempt counter + lockout | **真实缺口**. `UdsSecurity` 只 track per-level seed/auth state, 无 counter / lockout. ECU NRC 0x35/0x36/0x37 也无 host-side 处理. |
| EcuReset / RoutineControl confirm | **已存在**. EcuReset 在 `UdsClient.cs:187` (non-virtual, 零直接测试); RoutineControl 在 `line 328` (已 virtual, 零直接测试). Brief "confirm" 实指"补测试 + 加 type-safe enum + virtual 一致性". |
| OEM `IKeyDerivationAlgorithm` concrete | **已存在 interface** (v1.1.0). "concrete implementations" 实际指"示例实现 + 文档" — 推 v1.5.0. |

## Per-Item 修复详情

### Item 1 — `UdsSecurity` lockout state (MEDIUM)

**Symptom**: `UdsSecurity` 无 attempt counter / lockout. ECU NRC 0x35/0x36/0x37 也无 host-side 处理. 失败 attempt 无累计.

**Fix**:
- `UdsSecurity.cs` 加 per-level lockout state (`AttemptCount` + `LockedUntilUtc` 字段)
- 新方法: `IsLocked(byte level)`, `RemainingLockoutDelay(byte level)`, `ResetLockout(byte level)` (public), `RecordFailedAttempt(byte level)` (internal)
- `UdsSecurity.Reset()` 改成 in-place clear auth/seed 但 **保留 lockout state** — D8 invariant: lockout 是 security policy, 不应被 session 切换绕过
- `UdsSecurityLockoutConfig` record (`MaxAttempts` + `LockoutDuration`, `Default = new(3, TimeSpan.FromSeconds(5))`)
- `UdsClient.SecurityAccessAsync` 入口加 `IsLocked(level)` check, 命中 throw `UdsSecurityLockedException`
- NRC 0x35/0x36/0x37 catch arm 调 `RecordFailedAttempt(level)`
- 成功 auth (SendKey) → `ResetLockout(level)`
- `LockoutConfig` setter is `internal set` (ship prep fix #3) — DI assembly 可写, 外部 callers 只读

**Tests**: +7 `UdsSecurityTests` (lockout threshold, expiry, monotonic decrease, ResetLockout, different-levels independent, D8 session-change preservation) + 1 `UdsClientTests.SecurityAccessAsync_WhenLocked_ThrowsBeforeWireEmit` (deterministic `sent.Should().BeEmpty()` assertion per ship prep fix).

### Item 2 — `EcuResetAsync` virtual + `UdsResetType` enum + tests (LOW)

**Symptom**: `EcuResetAsync(byte)` 存在但 non-virtual + 零直接测试. `byte` 参数无 type-safety.

**Fix**:
- `EcuResetAsync(byte, CancellationToken)` 加 `virtual` 关键字 (11 sibling UDS methods 现在都 virtual)
- 加 defensive `response.Length > 0` guard 防止 `IndexOutOfRangeException`
- 新 enum overload `EcuResetAsync(UdsResetType, CancellationToken)`
- `UdsResetType` enum: `HardReset=0x01`, `KeyOffOnReset=0x02`, `SoftReset=0x03` (ISO 14229-1 §10.2)
- `byte` overload 保留兼容 OEM-specific 0x40-0x7F

**Tests**: +6 wire-level tests + `SpyUdsClientForEcuReset` 验证 virtual dispatch.

### Item 3 — `RoutineControlAsync` direct tests + `RoutineControlType` enum (LOW)

**Symptom**: `RoutineControlAsync` 已 virtual 但零直接测试 (only VM-level via `RoutinePanelViewModelTests`).

**Fix**:
- `RoutineControlAsync` signature 不变 (已 virtual)
- 新 enum overload `RoutineControlAsync(RoutineControlType, ushort, byte[]?, CancellationToken)`
- `RoutineControlType` enum: `StartRoutine=0x01`, `StopRoutine=0x02`, `RequestRoutineResults=0x03` (ISO 14229-1 §10.4)
- `byte` overload 保留兼容 OEM-specific 0x40-0x7F

**Tests**: +6 wire-level tests + `SpyUdsClientForRoutineControl` 验证 virtual dispatch.

### Item 4 — Type-safe enums (LOW)

`UdsResetType` + `RoutineControlType` enums (in Items 2 + 3).

### Item 5 — `AppHostBuilder` DI wiring (LOW)

**Symptom**: `UdsSecurityLockoutConfig` 缺 DI path 让 OEM 集成 per-config.

**Fix**:
- `UdsClient` 新 ctor overload `(IsoTpLayer, IKeyDerivationAlgorithm, UdsSecurityLockoutConfig, UdsTimer?, ILogger<UdsSession>?)` chains to existing 3-arg ctor 然后 set `Security.LockoutConfig`
- `AppHostBuilder` 转换 `static class` → instance class + 加 `WithUdsSecurityLockoutConfig(UdsSecurityLockoutConfig)` fluent builder method
- DI factory branches on `_udsSecurityLockoutConfig is { } lockoutConfig` (default policy preserved for legacy callers)
- 14 call sites updated: 13 tests + `App.xaml.cs` production caller

**Tests**: +1 `AppHostBuilderTests.WithUdsSecurityLockoutConfig_Injects_To_UdsSecurity`.

## Known follow-ups (deferred to v1.3.1 PATCH)

| # | Item | 来源 |
|---|------|------|
| 1 | Lockout counter increments on RequestSeed NRC 0x35 (should scope to SendKey only) | Pre-ship review Important #2 |
| 2 | 2-arg `SecurityAccessAsync` race between RequestSeed + SendKey legs (mid-handshake lockout trigger throws confusing exception) | Pre-ship review Important #4 |
| 3 | `AppHostBuilder` static→instance conversion rationale documentation | Pre-ship review Important #1 |

## Tests

| Metric | v1.2.14 baseline | **v1.3.0 final** |
|--------|------------------|-------------------|
| Total tests | 667 + 6 SKIP + 0 fail | **688 + 6 SKIP + 0 fail** (+21 net) |
| Core.Tests | 240 | **260** (+20) |
| Infra.Tests | 84 | 84 (no change) |
| App.Tests | 343 | **344** (+1) |

## Ship process

11 commits on `feature/v1-3-0-minor`:
- `97a84d5` spec(v1.3.0): UDS protocol completion
- `50e3753` plan(v1.3.0): 5 tasks
- `8e975f6` Item 1 part 1 (UdsSecurity lockout state + config)
- `8179c98` Item 1 part 2 (UdsClient wire-level integration)
- `e07f6fa` Task 2 review fix (wire-emit-absence assertion)
- `3f67dad` Item 2 (EcuResetAsync virtual + UdsResetType enum + tests)
- `1e0aa79` Item 3 (RoutineControlAsync direct tests + RoutineControlType enum)
- `962bd62` Item 5 (AppHostBuilder.WithUdsSecurityLockoutConfig)
- `3a02c28` ship prep #3 (LockoutConfig internal set)
- `3fe228d` ship prep #2 (UdsClientTests trailing newline)
- `a802105` ship prep #1 (spec alignment with shipped API)
- squash → PR #7 → main → tag v1.3.0 → release

## Spec drops (post Phase 2.5)

| Brief | Drop reason |
|-------|-------------|
| OEM `IKeyDerivationAlgorithm` concrete | Interface 已存在 (v1.1.0); "concrete impl" 推 v1.5.0 |
| Replay + Send DBC + V8 sandbox + DBC limits + Channel picker | Decomposed to v1.4.0 / v1.5.0 |

## Decomposition context

8 deferred items → 3 MINOR releases:
- **v1.3.0 (this release)**: UDS protocol completion (2 items: lockout + service confirm)
- **v1.4.0**: Replay (ASC parser + time-based replay + speed control) + Send DBC signal encoding (最大用户价值)
- **v1.5.0**: V8 sandbox hardening + CanApi rate limit + DBC size/token limit + path normalization + OEM `IKeyDerivationAlgorithm` concrete + Channel picker

## Breaking changes

无. 所有 MINOR 范围内变更, additive only. `LockoutConfig` setter 从 `public set` 改成 `internal set` 是 strengthening, 不破坏既有 callers (DI 注入场景走 ctor overload).