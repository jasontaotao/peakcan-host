# v1.3.1 PATCH — UDS SecurityAccess Tightening Design Spec

**Date:** 2026-06-28
**Branch:** `feature/v1-3-1-patch` (cut from main @ v1.3.0 `01988cd`)
**Target version:** v1.3.1 (PATCH)
**Spec owner:** main agent
**Reviewer:** code-reviewer subagent (pre-ship)
**Pre-flight:** Phase 2.5 actual code exploration (all 3 items verified)

## 起源

v1.3.0 MINOR pre-ship code-review verdict `0C / 4I / 6M` 标出 3 项 **Important** 级 carry-over。Memory `peakcan-host-v1-3-0-shipped.md` + `docs/release-notes-v1.3.0.md` §"Known follow-ups (deferred to v1.3.1 PATCH)" 列出。

| # | Item | 来源 | Severity |
|---|------|------|----------|
| 1 | Lockout counter increments on RequestSeed NRC 0x35 (should scope to SendKey only) | Pre-ship review **Important #2** | HIGH |
| 2 | 2-arg `SecurityAccessAsync` race between RequestSeed + SendKey legs (mid-handshake lockout trigger throws confusing exception) | Pre-ship review **Important #4** | HIGH |
| 3 | `AppHostBuilder` static→instance conversion rationale documentation | Pre-ship review **Important #1** | LOW (docs-only) |

## Phase 2.5 actual code exploration findings

实际读 v1.3.0 shipped 代码确认全部 3 项 brief 描述准确：

| Brief claim | Phase 2.5 actual code |
|---|---|
| "Lockout counter increments on RequestSeed NRC 0x35" | **`UdsClient.cs:323-329`** catch arm: `when ((byte)nrc.ResponseCode == 0x35 || 0x36 || 0x37)` 无 `key is not null` guard。RequestSeed leg (`key is null`, sub = `level`) 收到 0x35 → `Security.RecordFailedAttempt(level)` → 计入 host 端 lockout counter。 |
| "2-arg `SecurityAccessAsync` race" | **`UdsClient.cs:347-363`** 2-arg overload: 调两次 3-arg overload, 中间无 `IsLocked` 重检。中间 lockout 触发 → 第二次 3-arg 调用在 entry throw `UdsSecurityLockedException`。Exception type 正确, 但用户上下文（"handshake 已走完 RequestSeed"）缺失。 |
| "`AppHostBuilder` static→instance rationale missing" | **`AppHostBuilder.cs:14-29`** class-level `<summary>` 只描述 `Build()` + Serilog side effects. 无 class-级 `<remarks>` 解释: (1) 为什么是 instance class (需要 `_udsSecurityLockoutConfig` 等 optional state across fluent calls), (2) lifecycle guidance (one builder per app, call `.Build()` once), (3) pattern (Microsoft.Extensions.Hosting.IHost builder pattern). |

所有 3 项 brief 与实际代码一致 — 无 scope drift, 直接执行。

## Scope

3 items = 1 wire-level semantic correction (lockout counter scope) + 1 timing hardening + diagnostics (2-arg race) + 1 documentation补全 (rationale).

无新 public API, 无 API 移除, 无依赖升级, 无 UI 改动, 无 behavior breaking change.

| # | Item | 组件 | Fix | Brief 来源 | Severity |
|---|------|------|-----|------|----------|
| 1 | Lockout counter scope | `UdsClient.cs:SecurityAccessAsync(byte, byte[]?, CancellationToken)` | `catch when` clause 加 `key is not null` guard. RequestSeed (`key is null`) 收到 NRC 0x35/0x36/0x37 → propagate 不计数. SendKey (`key is not null`) 收到 NRC → `RecordFailedAttempt` 不变 | Pre-ship Important #2 | HIGH |
| 2 | 2-arg race pre-check | `UdsClient.cs:SecurityAccessAsync(byte, CancellationToken)` | (a) entry pre-check `if (Security.IsLocked(level)) throw new UdsSecurityLockedException(...)`; (b) XML `<remarks>` 说明 mid-handshake race 行为 (TOCTOU window 不可完全消除, SendKey leg entry check 是 source of truth) | Pre-ship Important #4 | HIGH |
| 3 | AppHostBuilder rationale doc | `AppHostBuilder.cs:class XML doc` | class-level `<remarks>` 加: (1) static→instance rationale (optional state via fluent methods); (2) lifecycle (one builder per app); (3) pattern alignment (Microsoft.Extensions.Hosting IHost builder) | Pre-ship Important #1 | LOW (docs-only) |

## Non-Goals

- **Item 1 不改 0x36/0x37 行为** — Pre-ship review Important #2 只说"RequestSeed 0x35 不应计数", 没说 0x36/0x37 应该从 SendKey leg 排除。0x36/0x37 (ECU-side lockout signals) 在 SendKey leg 仍按现状计数, 因为这些 NRC 仍代表实际 auth 尝试被 ECU 拒绝。最小改动: 仅加 `key is not null` guard。
- **Item 2 不重构 2-arg overload 为 single-leg 优化** — 性能/清晰度都不在本次 scope, 只是 fail-fast + 文档。TOCTOU race 在跨 call 之间本质无法消除。
- **v1.4.0 MINOR**: Replay (ASC parser + time-based replay + speed control) + Send DBC signal encoding — 不在本 PATCH 范围。
- **v1.5.0 MINOR**: V8 sandbox + CanApi rate limit + DBC size/token limit + path normalization + OEM `IKeyDerivationAlgorithm` concrete + Channel picker — 不在本 PATCH 范围。
- 公开 API 移除 / 依赖升级 / 用户数据 schema migration — PATCH 纪律（无）。

## 设计决策

### Decision 1: Item 1 catch arm filter — `key is not null` vs `ResponseCode == 0x35` only

**选项 A** (推荐): `when (key is not null && (0x35 || 0x36 || 0x37))` — 保留 0x36/0x37 在 SendKey leg 计数行为, 仅排除 RequestSeed leg。

**选项 B**: `when (key is not null && (byte)nrc.ResponseCode == 0x35)` — 同时排除 SendKey leg 的 0x36/0x37。

**决策**: A。理由: 0x36 (exceededNumberOfAttempts) 和 0x37 (requiredTimeDelayNotExpired) 是 ECU 反馈"已 lockout"的信号。即使是 ECU-side lockout 信号, 仍代表 host 的 SendKey 尝试被 ECU 拒绝 — 计入 host 端 lockout counter 是合理反馈机制。Pre-ship review 也只说"RequestSeed 0x35", 未要求排除 0x36/0x37。最小改动原则。

### Decision 2: Item 2 entry pre-check vs catch-and-rewrap

**选项 A** (推荐): 2-arg overload entry 加显式 `IsLocked` pre-check + XML `<remarks>` 说明 mid-handshake race。

**选项 B**: 2-arg overload 在 SendKey leg 外包 try/catch `UdsSecurityLockedException`, rethrow with diagnostic context (e.g. inner exception 或 extra field)。

**决策**: A。理由:
1. 3-arg overload 已有 entry check (line 284)。2-arg overload 作为 composition of 2x 3-arg, 间接继承。补显式 pre-check 让"fail-fast on already-locked"语义对 2-arg 调用者立即可见, 无需 chase 3-arg。
2. B 需要改 `UdsSecurityLockedException` (new ctor or new property) — 增加 public API surface, PATCH 不应该 break/extend public types。
3. 2-arg overload entry 已 throw `UdsSecurityLockedException` (transitively via first 3-arg call). 显式化让 caller 可读 `xml-doc` 知道 semantics, 不需要看 3-arg 内部。
4. XML `<remarks>` 文档化 TOCTOU race window 是最 honest 的处理。

### Decision 3: Item 3 docs placement — class `<remarks>` vs ctor `<remarks>`

**选项 A** (推荐): 加 class-level `<remarks>` block (紧跟现有 `<summary>`).

**选项 B**: 加 `WithUdsSecurityLockoutConfig` method-level `<remarks>`.

**决策**: A。rationale 属于 class-level invariant ("this is an instance class because..."), method-level doc 会被移动/重命名时丢失。`WithUdsSecurityLockoutConfig` method 已有自己的 `<summary>`, 重复 class-level 反而冗余。

## 测试策略

### Item 1 tests (in `UdsClientTests.cs`)

新增 2 个 test：

1. `SecurityAccessAsync_RequestSeed_Nrc_35_DoesNot_Increment_AttemptCount` — RED→GREEN: 
   - 构造 wire (request → NRC 0x35 negative response)
   - 调用 `SecurityAccessAsync(level: 0x01, key: null, ct)` (RequestSeed leg)
   - 断言: `Security.IsLocked(0x01) == false` AND `Security.AttemptCount == 0` (需 new internal getter 镜像 `SendFailureCount` 模式)

2. `SecurityAccessAsync_SendKey_Nrc_35_Still_Increments_AttemptCount` — regression guard:
   - 构造 wire (request → NRC 0x35)
   - 调用 `SecurityAccessAsync(level: 0x01, key: [0xAA], ct)` (SendKey leg)
   - 断言: `Security.RecordFailedAttempt` 仍被调用 (wire-level side effect)

**RED test #1**: 当前 catch arm 无 `key is not null` guard → RequestSeed 0x35 也会 `RecordFailedAttempt` → `AttemptCount == 1` ≠ 0 → FAIL。

**GREEN**: catch arm `when (key is not null && ...)`.

**Regression #2**: SendKey 路径不变 → PASS。

### Item 2 tests (in `UdsClientSecurityAccessOverloadTests.cs`)

新增 2 个 test：

1. `TwoArg_Overload_PreChecks_Lockout_Before_RequestSeed` — fail-fast on already-locked level:
   - 3 次 `Security.RecordFailedAttempt(0x01)` 触发 lockout
   - 2-arg `SecurityAccessAsync(level: 0x01, ct)` (with valid `_keyAlgorithm`)
   - 断言: 抛 `UdsSecurityLockedException` (level=0x01, RemainingDelay > 0)
   - 断言: ISO-TP wire 未触达 (无 frame sent) — 镜像 `SecurityAccessAsync_WhenLocked_ThrowsBeforeWireEmit` 模式

2. `TwoArg_Overload_DocumentsMidHandshake_RaceSemantics` — XML-doc 行为 regression:
   - 这个 test 不是 atomic race test (timing-dependent, flaky-prone), 而是 **断言现有 XML doc 包含 "mid-handshake" 或 "TOCTOU" 关键字**, 防止未来重构删掉注释 → race semantics 隐藏回归
   - 反射读 method XML doc → assert contains "mid-handshake" 或 "race"

**RED test #1**: 当前 2-arg overload 无显式 pre-check (transitively 通过 first 3-arg call 才有). 但 behavior 上仍然 throw `UdsSecurityLockedException` 因为 3-arg 第一调用就 throw. 

⚠️ **Phase 2.5 红旗**: test #1 可能不是真 RED. 2-arg overload 通过 3-arg overload entry check 已经 throw. 显式 pre-check 是 defensive coding (avoid wire-allocate for first call), 不是 behavior 改变。

**调整**: Test #1 改为断言 wire NOT touched (与现有 `SecurityAccessAsync_WhenLocked_ThrowsBeforeWireEmit` 一致). 如果 wire 未触达, 说明 3-arg overload entry check fires first — 即现有 transitive check 已足够. 显式 pre-check 在 2-arg overload 是 defensive, 无 behavior 改变 → 无 RED test.

**替代**: 加 wire-emit count assertion. 显式 pre-check 路径: 0 wire frames (pre-check fires). Transitive 路径 (current): 0 wire frames (first 3-arg entry check fires). 两者都 0. Test 通过.

**Decision**: 保留 test #1 as **wire-emit assertion + locked-exception-type assertion**. 验证 fix is non-breaking. 不强求 RED-before-GREEN. Phase 2.5 红旗: implementer 必须 doc 这点 — Item 2 不是 RED→GREEN behavior 改变, 是 defensive coding + docs.

Test #2 是 XML-doc inspection. 用 `XmlDocumentationReader` 反射读 method doc → assert contains key string. 不需要 RED→GREEN; 是 doc gate。

### Item 3 tests

无测试。Docs-only change。CI verification 通过 `dotnet build` (warning-as-error on missing XML doc) 已足够。

## 风险与缓解

| 风险 | 概率 | 影响 | 缓解 |
|------|------|------|------|
| Item 1 catch arm 改动破坏现有 wire-level test (e.g. `Nrc_35_Increments_AttemptCount` 假设 SendKey leg) | LOW | MEDIUM (regression) | 测试前 grep `UdsClientTests.cs` 所有 "NRC 0x35" / "AttemptCount" references. 预期: 所有现有 test 用 key != null (SendKey), 不会破坏 |
| Item 2 pre-check 性能开销 (每 call 1 lock acquire) | LOW | LOW | `IsLocked` 已经走 lock + dictionary lookup, 单次开销 < 1μs, 不可观测 |
| Item 2 test #1 不是真正 RED | MEDIUM | LOW (process discipline) | Phase 2.5 doc 标记 "defensive coding + docs, no behavior change". 不强求 RED→GREEN discipline violation |
| Item 3 docs 修改被 pre-commit hook (warning-as-error) 拒绝 | LOW | LOW | 现有 `<summary>` 已 pass warning-as-error. 加 `<remarks>` 不会触发 warning |
| Network proxy 在 ship 步骤 flaky | LOW | MEDIUM (per v1.3.0 + v1.2.14 ship notes) | 优先 `gh api` 路径 (github.com:443 阻塞时 api.github.com 直连); proxy 127.0.0.1:7897 备选 |

## Ship process

1. `git checkout -b feature/v1-3-1-patch` (从 main @ v1.3.0 `01988cd`)
2. Per-Item TDD (3 tasks: Item 1 RED+GREEN, Item 2 defensive+XML doc, Item 3 XML doc)
3. Pre-ship code-review (code-reviewer subagent sonnet on `main...HEAD`)
4. Bump `Directory.Build.props` 1.3.0 → 1.3.1
5. Write `docs/release-notes-v1.3.1.md` (mirror v1.3.0 template)
6. Push branch → PR → squash → tag v1.3.1 → release
7. Update memory file `peakcan-host-v1-3-1-shipped.md`

## 后续 (v1.4.0 MINOR 候选)

不在本 PATCH scope:
- Replay (ASC parser + time-based replay + speed control)
- Send DBC signal encoding

## 关键文件

- `src/PeakCan.Host.Core/Uds/UdsClient.cs:279-330` — 3-arg `SecurityAccessAsync` (Item 1 改 catch arm)
- `src/PeakCan.Host.Core/Uds/UdsClient.cs:347-363` — 2-arg `SecurityAccessAsync` (Item 2 改 pre-check + XML doc)
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs:14-29` — class XML doc (Item 3 改 remarks)
- `tests/PeakCan.Host.Core.Tests/Uds/UdsClientTests.cs:335-369` — append Item 1 tests
- `tests/PeakCan.Host.Core.Tests/Uds/UdsClientSecurityAccessOverloadTests.cs` — append Item 2 tests
- `Directory.Build.props` — version bump
- `docs/release-notes-v1.3.1.md` — new