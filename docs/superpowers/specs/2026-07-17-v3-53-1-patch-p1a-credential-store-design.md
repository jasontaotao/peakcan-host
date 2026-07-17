# v3.53.1 PATCH P1a — ICredentialStore + Windows Credential Manager

> P1 LLM PATCH 系列 step 1/3（v3.52.0 spec 明确推迟的 4 项）。
> 3-iter scope:
> - **v3.53.1 P1a (this spec)**: ICredentialStore + Windows Credential Manager 实现（安全存储基础）
> - **v3.53.2 P1b (future)**: DeepSeek Provider + HttpClient + JSON request
> - **v3.53.3 P1c (future)**: JSON Schema validation + Evidence ID whitelist + offline-mode stub fallback

## 目标

提供 Core-side abstraction `ICredentialStore`（仅 interface），App-layer 实现 `WindowsCredentialManagerStore`（DPAPI / Win32 Credential Manager API 封装）。API Key 不再以纯文本写 `appsettings.json`，而是用户在 UI 设置后存到 Windows Credential Manager，runtime 通过 `ICredentialStore` 读取。

## 当前代码证据（已坐实）

- `src/PeakCan.Host.Core/Analysis/ILlmProvider.cs:4` — 文档注释："P1 PATCH will add: DeepSeekProvider, AzureOpenAIProvider, LocalOllamaProvider."
- v3.52.0 spec hard-boundary #13 — Evidence ID whitelist filter
- 现有 `appsettings.json` 配置模式 (Serilog + Dbc + Script)
- 现有 `ILogger<T>` pattern
- `src/PeakCan.Host.App/Services/Recording/` 无任何 HttpClient / CredentialManager 代码（grep 确认）
- peakcan-host 是 **WPF .NET 10 Windows-only app**（不在 Linux/macOS 跑）

## 5 个 D 决策（D1-D5）

| ID | 决策 | 选择 | 理由 |
|---|---|---|---|
| D1 | ICredentialStore interface 位置 | **Core 层** (`src/PeakCan.Host.Core/Analysis/ICredentialStore.cs`) | sister of `ILlmProvider`（同 namespace，同 layer）。Core layer 不能依赖 Win32，所以 interface 必须 platform-neutral。 |
| D2 | Windows Credential Manager 实现位置 | **App layer** (`src/PeakCan.Host.App/Services/CredentialStore/WindowsCredentialManagerStore.cs`) | Win32 Credential Manager API (`Advapi32.dll` CredWrite/CredRead/CredDelete) 仅 Windows 可用；封装在 App layer 是平台实现细节。 |
| D3 | API Key 加密 vs plaintext | **Windows Credential Manager = DPAPI-encrypted 自动**（选 target "Enterprise" persistence） | Windows Credential Manager 内部用 DPAPI，每个 user account 独立加密。无 API Key plaintext on disk。**不需要自己实现 DPAPI 加密**。 |
| D4 | Service lifetime | `AddSingleton<ICredentialStore, WindowsCredentialManagerStore>()` | Credential store 是 process-wide service，无 state 需 per-request。 |
| D5 | Error handling | catch `Win32Exception` + 包成 `CredentialStoreException` (Core-layer 自定义 exception)，带 user-friendly message | 用户感知到 "API Key 找不到" 时应看到清晰 message，不是 raw HRESULT。 |

## 硬边界（继承 v3.52.0）

- API Key **永不写** `appsettings.json`、**永不写** log、**永不写** `LlmAnalysisResult.RawResponseJson`、**永不** display plaintext 给 user。
- `ICredentialStore.GetAsync(key)` 返回 `string?`（null = not found）。
- Credential target = `CRED_TYPE_GENERIC` + persistence `CRED_PERSIST_LOCAL_MACHINE`（不漫游到其他 machines）。
- Key namespacing：所有 peakcan-host credentials 用 prefix `"peakcan-host:"`（避免与系统其他 app 冲突）。

## 架构

```text
ICredentialStore (Core)
   ↑
WindowsCredentialManagerStore (App/Services/CredentialStore/)
   ↓ (P/Invoke)
advapi32.dll!CredWriteW / CredReadW / CredDeleteW
   ↓
Windows Credential Manager (DPAPI-encrypted vault)
```

## 数据契约

### Core interface (`src/PeakCan.Host.Core/Analysis/ICredentialStore.cs`):

```csharp
namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.53.1 PATCH P1a: Core-side abstraction for secure credential
/// storage. Implementations are platform-specific (Windows Credential
/// Manager, macOS Keychain, Linux libsecret, in-memory for tests).
/// Per v3.52.0 hard-boundary: API keys must NEVER be stored in plaintext
/// appsettings.json or logged — they MUST flow through this interface.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Get a credential by key. Returns null if not found.
    /// Throws <see cref="CredentialStoreException"/> on platform errors.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Store a credential. Overwrites if exists.
    /// Throws <see cref="CredentialStoreException"/> on platform errors.</summary>
    Task SetAsync(string key, string value, CancellationToken ct = default);

    /// <summary>Delete a credential. No-op if not found.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>Platform-level credential access failure.
/// Wraps Win32 HRESULT / libsecret error codes with user-friendly message.</summary>
public sealed class CredentialStoreException : Exception
{
    public string Key { get; }
    public CredentialStoreException(string key, string message, Exception? inner = null)
        : base(message, inner) { Key = key; }
}
```

### App implementation (`src/PeakCan.Host.App/Services/CredentialStore/WindowsCredentialManagerStore.cs`):

```csharp
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.App.Services.CredentialStore;

/// <summary>v3.53.1 PATCH P1a: Windows Credential Manager backend for
/// ICredentialStore. Uses advapi32.dll CredRead/CredWrite/CredDelete.
/// Credentials are DPAPI-encrypted by the OS, scoped to current user.
/// Per v3.52.0 hard-boundary: API keys MUST use this path, never
/// appsettings.json.
/// </summary>
public sealed class WindowsCredentialManagerStore : ICredentialStore
{
    private const string KeyPrefix = "peakcan-host:";
    private readonly ILogger<WindowsCredentialManagerStore> _logger;

    public WindowsCredentialManagerStore(ILogger<WindowsCredentialManagerStore> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var fullKey = KeyPrefix + key;
        try
        {
            if (!CredRead(fullKey, CRED_TYPE_GENERIC, 0, out var credPtr))
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ERROR_NOT_FOUND) return Task.FromResult<string?>(null);
                throw new CredentialStoreException(key, $"CredRead failed: HRESULT 0x{err:X8}");
            }
            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                var value = Marshal.PtrToStringUni(cred.CredentialBlob);
                return Task.FromResult<string?>(value);
            }
            finally { CredFree(credPtr); }
        }
        catch (Win32Exception ex)
        {
            throw new CredentialStoreException(key, $"Credential store read failed for '{key}'", ex);
        }
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        // ... P/Invoke CredWrite with CRED_PERSIST_LOCAL_MACHINE
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        // ... P/Invoke CredDelete
    }

    // P/Invoke declarations
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr cred);

    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
```

## 错误处理

| 场景 | 处理 |
|---|---|
| 平台不是 Windows | `PlatformNotSupportedException` (ctor) — peakcan-host is Windows-only, but defensive |
| Win32 error 1168 (NOT_FOUND) | return null (not an error) |
| Other Win32 errors | wrap in `CredentialStoreException` |
| API Key 包含 null character | throw `ArgumentException` in `SetAsync` |
| API Key > 32767 chars (CRED_MAX_CREDENTIAL_BLOB_SIZE) | throw `ArgumentException` in `SetAsync` |

## 测试策略（TDD）

- **Core 层**（`tests/PeakCan.Host.Core.Tests/Analysis/ICredentialStoreTests.cs`）:
  - Interface contract tests using `InMemoryCredentialStore`（test fixture，**不算 production code**）
  - Tests for `CredentialStoreException` message formatting
  - All Core tests run on any platform (no Win32 dependency)

- **App 层**（`tests/PeakCan.Host.App.Tests/Services/CredentialStore/WindowsCredentialManagerStoreTests.cs`）:
  - **`[Trait("Category", "WindowsOnly")]`** — skipped on non-Windows CI
  - Integration test: `SetAsync` → `GetAsync` round-trip
  - Integration test: `GetAsync` on non-existent key returns null
  - Integration test: `DeleteAsync` removes key
  - Integration test: `SetAsync` overwrites existing key
  - **Skip count expected: +5 tests marked WindowsOnly**（platform 兼容性）

## 复评/不做项

- **macOS Keychain / Linux libsecret 实现** —— 明确不做（peakcan-host 是 Windows-only app；YAGNI）。
- **DPAPI 自己实现** —— 明确不做（用 Windows Credential Manager 间接用 DPAPI）。
- **API Key rotation / TTL** —— 明确不做（P1b PATCH）。
- **UI 设置界面** —— 明确不做（P1b 或 P2 PATCH）。
- **`appsettings.json` 旧 API Key 迁移** —— 明确不做（pre-launch product，无 migration）。

## 4 NEW 1/3 lesson candidates 观察

| 候选 | 期望观察点 |
|---|---|
| `core-side-credential-abstraction-must-be-platform-neutral-with-app-layer-impl` | P1a 1st observation: ICredentialStore in Core, impl in App |
| `win32-pinvoke-credread-must-check-error-not-found-as-null-not-exception` | P1a 1st observation: ERROR_NOT_FOUND → return null |
| `credential-store-credential-exception-must-wrap-win32-hresult-with-user-friendly-message` | P1a 1st observation: Win32Exception → CredentialStoreException |
| `test-fixture-in-memory-credential-store-enables-platform-neutral-core-tests` | P1a 1st observation: InMemoryCredentialStore test double |

## 文件 / LoC 估算

| 文件 | LoC |
|---|---|
| `src/PeakCan.Host.Core/Analysis/ICredentialStore.cs` | 35 (interface + exception) |
| `src/PeakCan.Host.App/Services/CredentialStore/WindowsCredentialManagerStore.cs` | 130 (P/Invoke + impl) |
| `tests/PeakCan.Host.Core.Tests/Analysis/ICredentialStoreTests.cs` | 60 (interface contract tests) |
| `tests/PeakCan.Host.Core.Tests/Analysis/InMemoryCredentialStore.cs` | 30 (test fixture) |
| `tests/PeakCan.Host.App.Tests/Services/CredentialStore/WindowsCredentialManagerStoreTests.cs` | 80 (5 integration tests) |
| `src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs` | +3 (DI registration) |
| **总计** | **~340 LoC** |

## 待 SPEC 用户复核

本文为 v3.53.1 PATCH P1a spec。请 review 后批准，下一步进入 writing-plans 写实施计划。P1b + P1c 留待后续 PATCH spec 单独走。