# v3.53.1 PATCH P1a — ICredentialStore + Windows Credential Manager

> P1 LLM PATCH 系列 step 1/3（v3.52.0 spec 明确推迟的 4 项）。
> 3-iter scope:
> - **v3.53.1 P1a (this ship)**: ICredentialStore + Windows Credential Manager 实现（安全存储基础）
> - **v3.53.2 P1b (future)**: DeepSeek Provider + HttpClient + JSON request
> - **v3.53.3 P1c (future)**: JSON Schema validation + Evidence ID whitelist + offline-mode stub fallback

## 概述

提供 Core-side abstraction `ICredentialStore`（仅 interface），App-layer 实现 `WindowsCredentialManagerStore`（Win32 P/Invoke → advapi32.dll CredRead/CredWrite/CredDelete → Windows Credential Manager）。API Key 不再以纯文本写 `appsettings.json`，而是通过 DPAPI 加密存储在 OS Credential Manager。

## 用户可见变化

**无 UI 变化**（P1a 是基础设施）。未来 P1b 或 P2 PATCH 会加入 API Key 输入界面。

## 安全保证

- **API Key 永不 plaintext on disk**：所有 API Key 走 Windows Credential Manager（OS-level DPAPI 加密，按 current user scope）。
- **永不写 log**：`SetAsync`/`GetAsync`/`DeleteAsync` 日志只包含 key NAME，不含 VALUE。
- **永不 display plaintext**：P1a 不涉及 UI；未来 UI 切到 password box 不回显。
- **`CRED_PERSIST_LOCAL_MACHINE`**：不漫游到其他 machines（防止 cross-machine API Key leakage）。
- **Key namespacing**：所有 peakcan-host credentials 用 `"peakcan-host:"` prefix（避免与系统其他 app 冲突）。
- **`ERROR_NOT_FOUND` 处理**：GetAsync 返回 null（不是 exception），DeleteAsync 是 no-op。

## 数据契约

新增 2 个 Core 类型（`PeakCan.Host.Core.Analysis` namespace）：

```csharp
public interface ICredentialStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public sealed class CredentialStoreException : Exception
{
    public string Key { get; }
    public CredentialStoreException(string key, string message, Exception? inner = null);
}
```

新增 1 个 App 类型（`PeakCan.Host.App.Services.CredentialStore` namespace）：

```csharp
public sealed class WindowsCredentialManagerStore : ICredentialStore
{
    public WindowsCredentialManagerStore(ILogger<WindowsCredentialManagerStore> logger);
    // GetAsync/SetAsync/DeleteAsync impl via advapi32.dll CredRead/Write/Delete
}
```

## 5 个核心决策

| ID | 决策 | 选择 |
|---|---|---|
| D1 | Interface 位置 | Core 层 `PeakCan.Host.Core.Analysis`（platform-neutral） |
| D2 | Win32 impl 位置 | App 层 `PeakCan.Host.App.Services.CredentialStore`（platform-specific） |
| D3 | 加密方式 | 间接用 DPAPI（via Windows Credential Manager） |
| D4 | Service lifetime | Singleton（process-wide） |
| D5 | Error handling | `CredentialStoreException` 包装 Win32 HRESULT with user-friendly message |

## 架构里程碑

- **首个安全敏感代码进入 peakcan-host**（之前无 HttpClient / DPAPI / CredentialManager 代码）
- **首个 Core-side platform abstraction**（future macOS / Linux 实现可加）
- **3 NEW 1/3 lesson candidates** 观察成功（待 2nd 观察后晋升）
- **T4 → T5 fix-loop observability**: T4 implementer missed `Marshal.PtrToStringUni` 的 length bound（Win32 CREDENTIAL.CredentialBlob 不是 null-terminated），T5 integration test `SetAsync_OverwriteExistingKey` 暴露了 1-line bug → T4-fix 修复。这是 subagent-driver protocol 的设计意图：TDD 测试 catch production bug。

## 计数

- 6 commits on `feature/v3-53-1-patch-p1a-credential-store`
  - T1 ICredentialStore interface + 2 tests
  - T2+T3 InMemoryCredentialStore fixture + 3 contract tests
  - T4 WindowsCredentialManagerStore P/Invoke impl
  - T4-fix 1-line security fix (PtrToStringUni length)
  - T5 5 WindowsCredentialManagerStoreTests integration tests
  - T6 DI registration
- ~340 LoC 增量（ICredentialStore 35 + WindowsCredentialManagerStore 143 + InMemoryCredentialStore 26 + tests 140 + DI 4）
- 1433 → **1443 PASS / 0 FAIL / 5 SKIP**（+10: 2 exception tests + 3 contract tests + 5 integration tests）
- 1 个 security bug caught by integration test (T5 → T4-fix loop)

## 3 NEW 1/3 lesson candidates（待 2nd 观察后晋升）

| 候选 | 期望观察点 |
|---|---|
| `core-side-credential-abstraction-must-be-platform-neutral-with-app-layer-impl` | T1+T4 验证: interface in Core, impl in App |
| `win32-pinvoke-credread-must-check-error-not-found-as-null-not-exception` | T4 验证: ERROR_NOT_FOUND (1168) → null, not throws |
| `win32-credread-blob-must-pass-length-to-marshal-ptrtostringuni-not-rely-on-null-terminator` | T4-fix 验证: CREDENTIAL.CredentialBlob 是 length-prefixed 不是 null-terminated → 必须传 length 给 PtrToStringUni |

## 显式不在范围（推到后续 PATCH）

- **v3.53.2 P1b PATCH**: DeepSeek Provider + HttpClient + JSON request body。P1a 提供 `ICredentialStore`，P1b 注入到 DeepSeekProvider 用于 API Key 读取。
- **v3.53.3 P1c PATCH**: JSON Schema validation + Evidence ID whitelist + offline-mode stub fallback。
- **API Key rotation / TTL / expiry**: 推迟。
- **UI 设置界面**: 推迟到 P1b 或 P2。
- **macOS Keychain / Linux libsecret implementations**: peakcan-host 是 Windows-only；YAGNI。

## 关联

- Spec: `docs/superpowers/specs/2026-07-17-v3-53-1-patch-p1a-credential-store-design.md` (`2af2f47`)
- Plan: `docs/superpowers/plans/2026-07-17-v3-53-1-patch-p1a-credential-store.md` (`45ee6ee`)
- v3.52.0 spec 推迟项：见 `docs/superpowers/specs/2026-07-16-ai-inference-v1-design.md` hard-boundary #13 + D1/D5 deferred items
- v3.52.1 ship（前置 PATCH）：`dc60b70`
- v3.53.0 ship（前置 refactor MINOR）：`d02d511`

## 验证

```bash
dotnet build PeakCan.Host.slnx -c Debug           # 0 errors, 0 warnings on touched code
dotnet test PeakCan.Host.slnx --no-build -c Debug  # 1443 PASS / 0 FAIL / 5 SKIP
```

## 已知并行测试 flake（不影响 ship）

`sister of v3.52.0/v3.52.1/v3.53.0`: `Replay.AscParserTests` + `IReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` + `Uds.UdsClientConcurrentSecurityAccessTests.TwoArg_Overload_ConcurrentMidHandshakeLockoutFlip_PostStateConsistent` 在并行测试（`MaxParallelThreads > 1`）下偶发失败（wall-clock timing tests 在 CPU 负载高时不稳定）。`dotnet test -- xUnit.MaxParallelThreads=1` 单线程运行即可 1443/1443 PASS。