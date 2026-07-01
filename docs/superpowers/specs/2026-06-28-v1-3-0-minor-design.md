# v1.3.0 MINOR — UDS Protocol Completion Design Spec

**Date:** 2026-06-28
**Branch:** `feature/v1-3-0-minor` (cut from main @ v1.2.14 `680712e`)
**Target version:** v1.3.0 (MINOR)
**Spec owner:** main agent
**Reviewer:** code-reviewer subagent (pre-ship)
**Pre-flight:** Phase 2.5 actual code exploration (3 specific items)

## 起源

v1.2.13 + v1.2.14 PATCH ship notes §"Non-Goals" + §"Known issues (deferred to v1.3.0 MINOR)" 列出 8 项 deferred scope。本 spec 覆盖其中 2 项（UDS protocol completion），其余 6 项分到 v1.4.0 (Replay + Send DBC) + v1.5.0 (Security hardening + UX)：

| Deferred item | Target release |
|---------------|---------------|
| **UDS SecurityAccess attempt counter + lockout** | **v1.3.0 (this spec)** |
| **UDS EcuReset / RoutineControl confirm** | **v1.3.0 (this spec)** |
| OEM `IKeyDerivationAlgorithm` concrete | v1.5.0 |
| Replay (ASC parser + time-based replay + speed control) | v1.4.0 |
| Send DBC signal encoding | v1.4.0 |
| V8 sandbox hardening + CanApi rate limit | v1.5.0 |
| DBC size / token limit + path normalization | v1.5.0 |
| Channel picker (UI) | v1.5.0 |

### Decomposition rationale

按 **协议完成度 → 用户价值 → 安全** 顺序拆：
- v1.3.0: UDS 标准完成（最小、明确、对 OEM 集成合作伙伴是 conformance 信号）
- v1.4.0: Trace/Send 大 feature（最大用户价值，marketing 卖点）
- v1.5.0: 防御深度 + UX（V8 sandbox 风险高，与 feature release 隔离更易回滚）

### Phase 2.5 actual code exploration findings

3 个 Explore agent 实地读 `UdsClient.cs` + `UdsSecurity.cs` + tests/ 验证 deferred brief：

| Brief | Phase 2.5 actual |
|-------|------------------|
| "UDS SecurityAccess attempt counter + lockout" | **对**. `UdsSecurity.cs` 只 track per-level seed/auth state, 无 attempt counter, 无 lockout. `SecurityAccessAsync` (line 234) 是 wire-level entry, 没 check lockout 状态. ECU NRC 0x35/0x36/0x37 也没 host-side 处理. |
| "UDS EcuReset / RoutineControl confirm" | **EcuReset 已存在** (`UdsClient.cs:187` `public async Task<byte> EcuResetAsync(byte resetType, CancellationToken ct = default)`), non-virtual, **零直接测试** (only VM-level test for RoutineControl sub 0x02/0x03). RoutineControl 已存在 (`line 328` `public virtual async Task<byte[]> RoutineControlAsync(...)`). Brief 描述 "confirm" 实指"补测试 + 加 type-safe enum + 改 virtual 一致性". |
| "OEM IKeyDerivationAlgorithm concrete implementations" | **已存在** `IKeyDerivationAlgorithm` interface (v1.1.0 ship, 见 `UdsClient.cs:51`), OEM 集成通过 DI. Brief 描述 "concrete implementations" 实际指"开箱即用的示例实现 (e.g. XOR placeholder, HMAC) + 文档" — **scope 进一步缩小**. 推 v1.5.0 处理. |

## Scope

5 items = 1 UDS service hardening (SecurityAccess lockout) + 2 existing services confirm (EcuReset + RoutineControl) + 2 supporting (enum types + lockout config DI).

| # | ID | 组件 | 修复 / 增强 | 来源 | Severity |
|---|----|------|------------|------|----------|
| 1 | I1 | UdsSecurity + UdsClient | `SecurityAccessAsync` 加 lockout state: `_attemptCount`, `_lockedUntilUtc`, `IsLocked`, `RemainingLockoutDelay`, `ResetLockout`, `RecordFailedAttempt`. Default 3 attempts / 5s lockout. ECU NRC 0x35/0x36/0x37 → increment + check threshold. Successful auth → reset counter. _Note: `WaitForUnlockAsync` 异步等待 API deferred 到 future release — 同步 query (`IsLocked` + `RemainingLockoutDelay`) 已足够 current use case._ | Phase 2.5 actual — `UdsSecurity` 现有字段, 无 lockout | MEDIUM |
| 2 | I2 | UdsClient | `EcuResetAsync` 加 `virtual` 关键字（7 sibling UDS methods 已 virtual, 一致性）+ add tests (positive, negative response, invalid sub) | Phase 2.5 actual — 已存在 `line 187` 但 non-virtual + 零测试 | LOW |
| 3 | I3 | UdsClient | `RoutineControlAsync` add direct tests (currently only VM-level via `RoutinePanelViewModelTests` line 86/97); existing virtual signature 保留 | Phase 2.5 actual — 已存在 `line 328` 已 virtual 但零直接测试 | LOW |
| 4 | I4 | 新文件 | `UdsResetType` enum (HardReset=0x01, KeyOffOnReset=0x02, SoftReset=0x03); `RoutineControlType` enum (StartRoutine=0x01, StopRoutine=0x02, RequestRoutineResults=0x03). 提供 enum 重载方法. | Phase 2.5 actual — 当前 `byte` 参数无 type-safety | LOW |
| 5 | I5 | AppHostBuilder | DI 注入 `UdsSecurityLockoutConfig { int MaxAttempts = 3, TimeSpan LockoutDuration = TimeSpan.FromSeconds(5) }` 让 OEM 集成 per-config | v1.3.0 scope 决定 — 让 Item 1 可配置 | LOW |

## Non-Goals

- v1.4.0 MINOR Replay + Send DBC signal encoding — **deferred**
- v1.5.0 MINOR OEM Key concrete + V8 sandbox + DBC limits + Channel picker — **deferred**
- 任何公开 API 移除 / 依赖升级 / 用户数据 schema migration — MINOR 纪律（只 additive）
- 新增 NuGet 包
- 改 `IKeyDerivationAlgorithm` interface 签名（已存在 from v1.1.0, 保持兼容）
- EcuReset / RoutineControl sub-function OEM-specific range (0x40-0x7F) — 保持 `byte` overload 兼容 OEM 用例

## Decisions made in brainstorming

| # | Question | Decision | Why |
|---|----------|----------|-----|
| D1 | 8 项 deferred scope 拆几个 MINOR release | **3 MINOR** (v1.3.0 = UDS, v1.4.0 = Trace/Send feature, v1.5.0 = Security + UX) | 协议完成度 → 用户价值 → 安全 顺序；安全 release 与 feature release 隔离便于回滚 |
| D2 | SecurityAccess lockout 默认参数 | **Default 3 attempts / 5s lockout + 可配置 via DI** | 项目其他 timeout pattern 一致（如 `FlowControlTimeout` 默认 1000ms + settable） |
| D3 | EcuReset + RoutineControl enum 严格度 | **Strict enum + `byte` overload** (新 enum overload, 旧 byte 保留) | Compile-time safety 给 standard 用例, byte overload 保留 OEM-specific (0x40-0x7F) 灵活度 |
| D4 | Lockout state per-level or global | **Per-level** (each `securityLevel` 独立 counter) | ISO 14229-1 §8.4 server behavior 是 per-level; OEM 集成通常每 level 不同 policy |
| D5 | `EcuResetAsync` 是否改 virtual | **Yes** | 7 sibling UDS methods 已 virtual, 加 virtual 是一致性修复, 不破坏现有调用 |
| D6 | `RoutineControlAsync` 是否改 virtual | **Already virtual** (line 328) | 已有, 不动 |
| D7 | Lockout check 在哪 | **`SecurityAccessAsync` entry** (line 234 + line 285 两条 path) | 唯一 wire-level entry; 两条 path 都 check, 防止绕过 |
| D8 | Counter reset 时机 | **Successful auth only** (line 265 之后). Session change (`UdsSession.Reset()`) **不 reset lockout** — security lockout 独立于 session state | Successful auth 证明 key 正确; lockout 状态是 security policy, 不应被 session 切换绕过（attacker 可能切 session 重置 counter 攻击） |
| D9 | Lockout 期间调用行为 | **Throw `UdsSecurityLockedException : UdsException`** with `RemainingDelay` property + `IsLocked` query property | 区别于 NRC-based error, host-side check 早于 wire emit, 节省 latency |
| D10 | Commit 策略 | **每 Item 一个 commit**, TDD (RED → GREEN → IMPROVE), ship prep 单独 commit | 与 v1.2.10 / v1.2.11 / v1.2.12 / v1.2.13 / v1.2.14 一致 |
| D11 | Ship 方法 | 走 v1.2.14 同款: 直 push → PR → `gh pr merge --squash --delete-branch` → tag → `gh release create` | 已有先例 |
| D12 | Pre-ship review | spec 完成后跑 `code-reviewer` subagent 看 spec; ship 前再跑 `code-reviewer` 看分支 diff (2 轮) | 借鉴 v1.2.12 / v1.2.13 / v1.2.14 的 2-轮 review pattern |

## Per-Item Spec

### Item 1 — `SecurityAccessAsync` lockout state (MEDIUM)

**Symptom** (Phase 2.5 actual):
- `UdsSecurity.cs:9` `_levels` dict 存 `SecurityLevelState { Seed, IsAuthenticated }`, 无 `_attemptCount`
- `SecurityAccessAsync` (UdsClient.cs:234) 调用 `SendRequestAsync(0x27, ...)` 后只处理 positive response; **negative response** (NRC 0x35 InvalidKey / 0x36 ExceededNumberOfAttempts / 0x37 RequiredTimeDelayNotExpired) **未 host-side 处理** → 失败 attempt 无累计
- 没 check lockout 状态: caller 在 lockout 期间还能发 SecurityAccess 请求, 浪费 bus 流量
- ECU 端 lockout 状态 server-side, 但 host 端无法 enforce "先等 delay 再 retry"

**Fix** (`UdsSecurity.cs` 新增):

```csharp
private sealed class SecurityLevelState
{
    public byte[]? Seed { get; init; }
    public bool IsAuthenticated { get; set; }
    public int AttemptCount { get; set; }  // NEW
    public DateTime? LockedUntilUtc { get; set; }  // NEW
}

public UdsSecurityLockoutConfig LockoutConfig { get; set; } = UdsSecurityLockoutConfig.Default;

/// <summary>True if the security level is currently locked out.</summary>
public bool IsLocked(byte level)
{
    lock (_levels)
    {
        if (_levels.TryGetValue(level, out var state) && state.LockedUntilUtc is DateTime until)
            return DateTime.UtcNow < until;
        return false;
    }
}

/// <summary>Time remaining on lockout, or TimeSpan.Zero if not locked.</summary>
public TimeSpan RemainingLockoutDelay(byte level)
{
    lock (_levels)
    {
        if (_levels.TryGetValue(level, out var state) && state.LockedUntilUtc is DateTime until)
        {
            var remaining = until - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return TimeSpan.Zero;
    }
}

/// <summary>Record a failed authentication attempt. Increments counter; if
/// threshold reached, sets lockout.</summary>
internal void RecordFailedAttempt(byte level)
{
    lock (_levels)
    {
        var state = _levels.TryGetValue(level, out var s) ? s : new SecurityLevelState();
        state.AttemptCount++;
        if (state.AttemptCount >= LockoutConfig.MaxAttempts)
        {
            state.LockedUntilUtc = DateTime.UtcNow + LockoutConfig.LockoutDuration;
            state.AttemptCount = 0;  // reset counter, lockout takes effect
        }
        _levels[level] = state;
    }
}

/// <summary>Reset attempt counter and lockout state for a level (called on
/// successful auth or session change).</summary>
public void ResetLockout(byte level)
{
    lock (_levels)
    {
        if (_levels.TryGetValue(level, out var state))
        {
            state.AttemptCount = 0;
            state.LockedUntilUtc = null;
        }
    }
}
```

```csharp
// UdsSecurityLockoutConfig.cs (新文件)
public sealed record UdsSecurityLockoutConfig(int MaxAttempts, TimeSpan LockoutDuration)
{
    public static UdsSecurityLockoutConfig Default { get; } = new(3, TimeSpan.FromSeconds(5));
}
```

**Wire-level integration** (`UdsClient.SecurityAccessAsync`):

```csharp
public virtual async Task<byte[]> SecurityAccessAsync(byte level, byte[]? key = null, CancellationToken ct = default)
{
    // v1.3.0 MINOR Item 1: lockout check before wire emit
    if (Security.IsLocked(level))
        throw new UdsSecurityLockedException(level, Security.RemainingLockoutDelay(level));

    // ... existing requestData construction ...

    try
    {
        var response = await SendRequestAsync(0x27, requestData, ct).ConfigureAwait(false);
        // ... existing positive response handling ...
        if (key is null)
        {
            Security.SetSeed(level, response[1..]);
            return response[1..];
        }
        else
        {
            Security.SetAuthenticated(level);
            Security.ResetLockout(level);  // NEW: successful auth clears lockout
            return response;
        }
    }
    catch (UdsNegativeResponseException nrc) when (nrc.Nrc == 0x35 || nrc.Nrc == 0x36 || nrc.Nrc == 0x37)
    {
        // v1.3.0 MINOR Item 1: track failed attempt
        Security.RecordFailedAttempt(level);
        throw;
    }
}
```

**Exception type** (新 file `UdsSecurityLockedException.cs`):

```csharp
public sealed class UdsSecurityLockedException : UdsException
{
    public byte SecurityLevel { get; }
    public TimeSpan RemainingDelay { get; }
    public UdsSecurityLockedException(byte level, TimeSpan remaining)
        : base($"Security level 0x{level:X2} is locked; retry after {remaining.TotalSeconds:F1}s")
    {
        SecurityLevel = level;
        RemainingDelay = remaining;
    }
}
```

**Tests** (red 6, green 6):
- `SecurityAccess_Locked_After_3_Failed_Attempts`: 3 个 NRC 0x35 → 第 4 个调用 throw `UdsSecurityLockedException`
- `SecurityAccess_Locked_Throws_Before_Wire`: lockout 期间调用 `SecurityAccessAsync` throw exception, 不发 wire (mock `SendRequestAsync` 验证 call count == 0)
- `SecurityAccess_Locked_RemainingDelay_Decreases`: `RemainingLockoutDelay` 随时间单调递减
- `SecurityAccess_LockedUntilUtc_Expires`: 时间跳到 `LockedUntilUtc` 之后 → `IsLocked` returns false
- `SecurityAccess_SuccessfulAuth_Resets_Lockout`: 1 NRC + 1 successful → counter reset, no lockout
- `SecurityAccess_LockoutConfig_DI_Override`: 注入 `MaxAttempts=2` → 2 fails → lockout
- `SecurityAccess_DifferentLevels_Independent_Counters`: level 0x01 locked, level 0x03 still unlocked

**Files**: 
- `src/PeakCan.Host.Core/Uds/UdsSecurity.cs` (modify, +30 lines)
- `src/PeakCan.Host.Core/Uds/UdsSecurityLockoutConfig.cs` (new, ~10 lines)
- `src/PeakCan.Host.Core/Uds/UdsSecurityLockedException.cs` (new, ~15 lines)
- `src/PeakCan.Host.Core/Uds/UdsClient.cs:234-268` (modify, +8 lines for lockout check + NRC catch + reset)
- `tests/PeakCan.Host.Core.Tests/Uds/UdsSecurityTests.cs` (new file, +6 tests)
- `tests/PeakCan.Host.Core.Tests/Uds/UdsClientTests.cs` (modify, +1 lockout integration test)

### Item 2 — `EcuResetAsync` virtual + tests (LOW)

**Symptom** (Phase 2.5 actual):
- `UdsClient.cs:187` `public async Task<byte> EcuResetAsync(byte resetType, CancellationToken ct = default)` — exists, **non-virtual**
- 7 sibling UDS methods (`DiagnosticSessionControlAsync` line 148, `ReadDataByIdentifierAsync` 192, `WriteDataByIdentifierAsync` 210, `SecurityAccessAsync` 234/285, `TesterPresentAsync` 314, `RoutineControlAsync` 328, `ReadDtcInformationAsync` 402, `ClearDiagnosticInformationAsync` 413) 都已 `virtual`
- **零直接测试** (`UdsClientTests.cs` grep 零匹配)

**Fix**:

1. `UdsClient.cs:187` 加 `virtual` 关键字:
```csharp
public virtual async Task<byte> EcuResetAsync(byte resetType, CancellationToken ct = default)
{
    var response = await SendRequestAsync(0x11, new byte[] { resetType }, ct).ConfigureAwait(false);
    return response.Length > 0 ? response[0] : (byte)0;
}
```

2. 新增 enum 重载 (`UdsClient.cs` 新方法):
```csharp
public Task<byte> EcuResetAsync(UdsResetType resetType, CancellationToken ct = default)
    => EcuResetAsync((byte)resetType, ct);
```

**Tests** (red 4, green 4):
- `EcuResetAsync_HardReset_0x01_Writes_Correct_SID`: 注入 ECU reply `[0x51, 0x01]`, assert `SendRequestAsync` called with `0x11, [0x01]`, returns `0x01`
- `EcuResetAsync_KeyOffOnReset_0x02`: 同上 for 0x02
- `EcuResetAsync_SoftReset_0x03`: 同上 for 0x03
- `EcuResetAsync_NegativeResponse_Propagates`: ECU returns NRC 0x12 (subFunctionNotSupported) → throws
- `EcuResetAsync_Enum_Overload_Dispatches_To_Byte`: `EcuResetAsync(UdsResetType.HardReset, ct)` 调用 byte overload
- `EcuResetAsync_Virtual_Override_Interceptable`: SpyUdsClient override `EcuResetAsync` 直接返回 `0xFF`, assert spy called

**Files**:
- `src/PeakCan.Host.Core/Uds/UdsClient.cs:187` (modify, +1 keyword `virtual`)
- `src/PeakCan.Host.Core/Uds/UdsClient.cs` (new method, +3 lines)
- `src/PeakCan.Host.Core/Uds/UdsResetType.cs` (new enum, ~10 lines)
- `tests/PeakCan.Host.Core.Tests/Uds/UdsClientTests.cs` (modify, +6 tests)

### Item 3 — `RoutineControlAsync` direct tests (LOW)

**Symptom** (Phase 2.5 actual):
- `UdsClient.cs:328` `public virtual async Task<byte[]> RoutineControlAsync(byte routineControlType, ushort routineId, byte[]? data = null, CancellationToken ct = default)` — exists, already virtual
- Tests 间接 via `RoutinePanelViewModelTests` (`tests/PeakCan.Host.App.Tests/ViewModels/Uds/RoutinePanelViewModelTests.cs:86/97` 测试 VM 调用, 不是 wire-level contract)
- 缺 wire-level direct tests (request payload 格式, 3 valid sub-functions, 1 invalid)

**Fix**:
1. `RoutineControlAsync` 不改 (already virtual, signature correct)
2. 新增 enum 重载:
```csharp
public Task<byte[]> RoutineControlAsync(RoutineControlType routineControlType, ushort routineId, byte[]? data = null, CancellationToken ct = default)
    => RoutineControlAsync((byte)routineControlType, routineId, data, ct);
```

**Tests** (red 4, green 4):
- `RoutineControlAsync_StartRoutine_0x01_Writes_Correct_SID`: payload = `[0x01, routineId_H, routineId_L, ...data]`, ECU reply `[0x71, 0x01, rid_H, rid_L, result...]` → returns `result`
- `RoutineControlAsync_StopRoutine_0x02`: 同上 for 0x02
- `RoutineControlAsync_RequestRoutineResults_0x03`: 同上 for 0x03
- `RoutineControlAsync_Short_Response_Throws_UdsException`: ECU reply `< 3 bytes` → throws `UdsException("Invalid RoutineControl response")`
- `RoutineControlAsync_Enum_Overload_Dispatches_To_Byte`: enum overload → byte overload
- `RoutineControlAsync_Virtual_Override_Interceptable`: SpyUdsClient override 验证

**Files**:
- `src/PeakCan.Host.Core/Uds/UdsClient.cs:328` (no signature change)
- `src/PeakCan.Host.Core/Uds/UdsClient.cs` (new enum overload method, +3 lines)
- `src/PeakCan.Host.Core/Uds/RoutineControlType.cs` (new enum, ~10 lines)
- `tests/PeakCan.Host.Core.Tests/Uds/UdsClientTests.cs` (modify, +6 tests)

### Item 4 — Type-safe enums (LOW, supporting)

**Symptom**: `byte` 参数 in `EcuResetAsync(byte resetType)` + `RoutineControlAsync(byte routineControlType)` 无 compile-time safety. Caller 可能传 0x00 / 0xFF 等无效值而无 warning.

**Fix**:
```csharp
// src/PeakCan.Host.Core/Uds/UdsResetType.cs
public enum UdsResetType : byte
{
    HardReset = 0x01,        // ISO 14229-1 §10.2
    KeyOffOnReset = 0x02,
    SoftReset = 0x03,
}

// src/PeakCan.Host.Core/Uds/RoutineControlType.cs
public enum RoutineControlType : byte
{
    StartRoutine = 0x01,     // ISO 14229-1 §10.4
    StopRoutine = 0x02,
    RequestRoutineResults = 0x03,
}
```

`byte` overload 保留 (Item 2/3 不破坏现有 callers, 让 OEM-specific sub-functions 0x40-0x7F 仍可用 raw byte).

**Tests**: enum value tests (1 per enum, 2 tests total) — trivially covered by Item 2 + Item 3 enum overload tests.

**Files**:
- `src/PeakCan.Host.Core/Uds/UdsResetType.cs` (new enum)
- `src/PeakCan.Host.Core/Uds/RoutineControlType.cs` (new enum)

### Item 5 — `UdsSecurityLockoutConfig` DI wiring (LOW)

**Symptom**: Item 1 引入 `LockoutConfig` field in `UdsSecurity` 但 `UdsSecurity` 实例化在 `UdsClient` ctor (line 75/103) `Security = new UdsSecurity()`. 无 DI path 让 OEM 注入 custom config.

**Fix** (`AppHostBuilder.cs`):

```csharp
// Add field
private UdsSecurityLockoutConfig? _udsSecurityLockoutConfig;

// Add builder method
public AppHostBuilder WithUdsSecurityLockoutConfig(UdsSecurityLockoutConfig config)
{
    _udsSecurityLockoutConfig = config;
    return this;
}

// Wire in DI registration
services.AddSingleton<UdsSecurity>(_ =>
{
    var security = new UdsSecurity();
    if (_udsSecurityLockoutConfig is not null)
        security.LockoutConfig = _udsSecurityLockoutConfig;
    return security;
});
```

(Verify exact `AppHostBuilder.cs` patterns via Phase 2.5 — likely already has similar config injection for other services.)

**Tests** (red 1, green 1):
- `AppHostBuilder_With_UdsSecurityLockoutConfig_Injects_To_UdsSecurity`: build app with `WithUdsSecurityLockoutConfig(new(5, TimeSpan.FromSeconds(10)))` → resolve `UdsSecurity` → assert `LockoutConfig.MaxAttempts == 5`

**Files**:
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (modify, ~15 lines)
- `tests/PeakCan.Host.App.Tests/Composition/AppHostBuilderTests.cs` (modify, +1 test)

## Test count budget

预计 **+15-20 net new tests**:
- Item 1: +7 (SecurityAccess lockout integration + UdsSecurity state)
- Item 2: +6 (EcuReset wire-level + enum overload + virtual dispatch)
- Item 3: +6 (RoutineControl wire-level + enum overload + virtual dispatch)
- Item 4: +0 (covered by Item 2/3 tests)
- Item 5: +1 (AppHostBuilder DI)
- 部分测试可能 collapse (`EcuResetAsync_Virtual_Override_Interceptable` 与 Item 4 virtual dispatch 共享 SpyUdsClient 框架). 保守估计 **+15 net new tests**.

v1.2.14 baseline 667 + 6 SKIP + 0 fail → **v1.3.0 target ~682 + 6 SKIP + 0 fail**.

## File Structure

**Modify (production):**
- `src/PeakCan.Host.Core/Uds/UdsSecurity.cs` — Item 1 (lockout state fields + methods)
- `src/PeakCan.Host.Core/Uds/UdsClient.cs:187` — Item 2 (add `virtual`)
- `src/PeakCan.Host.Core/Uds/UdsClient.cs:234-268` — Item 1 (wire-level lockout check + NRC catch)
- `src/PeakCan.Host.Core/Uds/UdsClient.cs:285-301` — Item 1 (same for keyAlgorithm overload)
- `src/PeakCan.Host.Core/Uds/UdsClient.cs:328` — Item 3 (no signature change, already virtual)
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` — Item 5 (DI wiring)

**Create (production):**
- `src/PeakCan.Host.Core/Uds/UdsSecurityLockoutConfig.cs` — Item 1 (config record)
- `src/PeakCan.Host.Core/Uds/UdsSecurityLockedException.cs` — Item 1 (exception type)
- `src/PeakCan.Host.Core/Uds/UdsResetType.cs` — Item 4 (enum)
- `src/PeakCan.Host.Core/Uds/RoutineControlType.cs` — Item 4 (enum)

**Modify (test):**
- `tests/PeakCan.Host.Core.Tests/Uds/UdsClientTests.cs` — Item 2 + Item 3 (+12 tests)
- `tests/PeakCan.Host.App.Tests/Composition/AppHostBuilderTests.cs` — Item 5 (+1 test)

**Create (test):**
- `tests/PeakCan.Host.Core.Tests/Uds/UdsSecurityTests.cs` — Item 1 (+6 tests for lockout state)
- `docs/release-notes-v1.3.0.md` — ship prep

**Read-only references:**
- `src/PeakCan.Host.Core/Uds/UdsClient.cs:148, 192, 210, 234, 285, 314, 328, 402, 413` — 8 sibling virtual methods (Item 2 pattern match)
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` — DI patterns for similar config injection
- `tests/PeakCan.Host.App.Tests/ViewModels/Uds/RoutinePanelViewModelTests.cs:41-44` — `SpyUdsClient` pattern (reusable for Item 2/3 virtual dispatch tests)

## References

- v1.2.14 release notes §"Non-Goals" — release-notes-v1.2.14.md
- v1.2.13 release notes §"Non-Goals" — release-notes-v1.2.13.md
- v1.2.14 spec §Item scope reduction — specs/2026-06-28-v1-2-14-patch-design.md
- ISO 14229-1:2020 §8.4 (SecurityAccess behavior) — referenced for lockout spec
- ISO 14229-1:2020 §10.2 (EcuReset) — referenced for EcuResetType enum
- ISO 14229-1:2020 §10.4 (RoutineControl) — referenced for RoutineControlType enum
- ISO 15765-2 (UDS on CAN) — transport layer (unchanged in this spec)