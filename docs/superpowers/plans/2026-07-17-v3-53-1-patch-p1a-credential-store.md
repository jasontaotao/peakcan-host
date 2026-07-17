# v3.53.1 PATCH P1a Implementation Plan — ICredentialStore + Windows Credential Manager

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Provide Core-side `ICredentialStore` abstraction + App-layer `WindowsCredentialManagerStore` implementation. API keys flow through DPAPI-encrypted Windows Credential Manager instead of plaintext appsettings.json. P1b (DeepSeek Provider) + P1c (JSON Schema + whitelist) are deferred to future PATCH specs.

**Architecture:** Core interface (platform-neutral) + App implementation (Win32 P/Invoke into advapi32.dll CredRead/Write/Delete). Key namespacing `"peakcan-host:"` prefix. `CRED_PERSIST_LOCAL_MACHINE` persistence. ERROR_NOT_FOUND → null. Other Win32 errors → `CredentialStoreException`.

**Tech Stack:** C# .NET 10 / WPF / `Microsoft.Extensions.Logging.Abstractions` / FluentAssertions / NSubstitute / xUnit / `[Trait]` for platform-specific test skipping

## Global Constraints

来自 `docs/superpowers/specs/2026-07-17-v3-53-1-patch-p1a-credential-store-design.md`：

- API Key **永不写** `appsettings.json`、**永不写** log、**永不** display plaintext 给 user
- `ICredentialStore` 在 Core 层（platform-neutral interface + exception）
- `WindowsCredentialManagerStore` 在 App 层（Win32 P/Invoke）
- Key prefix: `"peakcan-host:"`
- `CRED_TYPE_GENERIC` + `CRED_PERSIST_LOCAL_MACHINE`
- `ERROR_NOT_FOUND` (1168) → return null, not exception
- 其他 Win32 errors → wrap in `CredentialStoreException`
- `[Trait("Category", "WindowsOnly")]` on integration tests; skipped on non-Windows CI
- 不引入第三方 NuGet 包（DPAPI / Credential Manager 是 Windows built-in）
- v3.53.0 ship files (StatsViewModel + subdirectory partials) 不动
- v3.52.0 + v3.52.1 ship files (Analysis records + AI partials) 不动
- pkm-capture 节流：仅在 ship-completion 时 dispatch

## File Structure

新增 + 修改：

| 文件 | LoC | 改动 |
|---|---|---|
| `src/PeakCan.Host.Core/Analysis/ICredentialStore.cs` | 35 | NEW (interface + exception) |
| `src/PeakCan.Host.Core/Tests/Analysis/InMemoryCredentialStore.cs` | 30 | NEW (test fixture) |
| `src/PeakCan.Host.App/Services/CredentialStore/WindowsCredentialManagerStore.cs` | 130 | NEW (P/Invoke impl) |
| `tests/PeakCan.Host.Core.Tests/Analysis/ICredentialStoreTests.cs` | 60 | NEW (interface contract) |
| `tests/PeakCan.Host.App.Tests/Services/CredentialStore/WindowsCredentialManagerStoreTests.cs` | 80 | NEW (5 WindowsOnly tests) |
| `src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs` | +3 | DI registration |
| **总计** | **~340 LoC** | |

---

### Task 0: Branch + spec verify + baseline

**Files:**
- Branch from main at current HEAD `2af2f47`
- Verify spec at `docs/superpowers/specs/2026-07-17-v3-53-1-patch-p1a-credential-store-design.md`

- [ ] **Step 1: Verify spec commit exists**

```bash
git log --oneline -3
git show --stat 2af2f47
```
Expected: commit `2af2f47` is visible with spec file +243 insertions.

- [ ] **Step 2: Create feature branch**

```bash
git checkout -b feature/v3-53-1-patch-p1a-credential-store main
```

- [ ] **Step 3: Verify baseline build is green**

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
```
Expected: 0 errors.

- [ ] **Step 4: Verify baseline test is green**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-build --nologo -c Debug --logger "console;verbosity=minimal"
```
Expected: pre-existing 510 Core tests pass (no Analysis regression; 16 Analysis tests still pass).

- [ ] **Step 5: Commit plan**

```bash
git add docs/superpowers/plans/2026-07-17-v3-53-1-patch-p1a-credential-store.md
git commit -m "v3.53.1 plan: P1a ICredentialStore + Windows Credential Manager (P1 LLM series step 1/3; TDD)"
```

---

### Task 1: ICredentialStore interface + CredentialStoreException (Core)

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/ICredentialStore.cs`

**Interfaces:**
- Produces:
  - `public interface ICredentialStore { Task<string?> GetAsync(string key, CancellationToken ct = default); Task SetAsync(string key, string value, CancellationToken ct = default); Task DeleteAsync(string key, CancellationToken ct = default); }`
  - `public sealed class CredentialStoreException : Exception { public string Key { get; } public CredentialStoreException(string key, string message, Exception? inner = null); }`

- [ ] **Step 1: Write the failing test (RED)**

Create `tests/PeakCan.Host.Core.Tests/Analysis/ICredentialStoreTests.cs`:

```csharp
using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class ICredentialStoreTests
{
    [Fact]
    public void CredentialStoreException_Constructor_PopulatesKeyAndMessage()
    {
        var ex = new CredentialStoreException("api-key", "read failed");
        ex.Key.Should().Be("api-key");
        ex.Message.Should().Contain("read failed");
    }

    [Fact]
    public void CredentialStoreException_InnerException_Preserved()
    {
        var inner = new InvalidOperationException("underlying");
        var ex = new CredentialStoreException("token", "wrap", inner);
        ex.InnerException.Should().BeSameAs(inner);
    }
}
```

- [ ] **Step 2: Verify RED**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ICredentialStoreTests"
```
Expected: FAIL with `error CS0246: The type or namespace name 'CredentialStoreException' could not be found`.

- [ ] **Step 3: Write minimal implementation (GREEN)**

Create `src/PeakCan.Host.Core/Analysis/ICredentialStore.cs`:

```csharp
namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.53.1 PATCH P1a: Core-side abstraction for secure credential
/// storage. Implementations are platform-specific (Windows Credential
/// Manager, macOS Keychain, Linux libsecret, in-memory for tests).
/// Per v3.52.0 hard-boundary: API keys MUST NEVER be stored in plaintext
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

- [ ] **Step 4: Verify GREEN**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-build --nologo -c Debug --filter "FullyQualifiedName~ICredentialStoreTests"
```
Expected: 2/2 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Core/Analysis/ICredentialStore.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/ICredentialStoreTests.cs
git commit -m "v3.53.1 T1: ICredentialStore interface + CredentialStoreException (Core; 2 tests pass)"
```

---

### Task 2: InMemoryCredentialStore test fixture (Core)

**Files:**
- Create: `src/PeakCan.Host.Core.Tests/Analysis/InMemoryCredentialStore.cs` (test fixture, NOT production)

**Interfaces:**
- Implements: `ICredentialStore` (Task 1)
- Used by: future T3 contract tests (verify interface implementations match contract)

- [ ] **Step 1: Create the test fixture**

Create `tests/PeakCan.Host.Core.Tests/Analysis/InMemoryCredentialStore.cs`:

```csharp
using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.Core.Tests.Analysis;

/// <summary>Test-only in-memory ICredentialStore. NOT production code.
/// Used by interface contract tests + WindowsCredentialManagerStore
/// cross-validation in tests/.../App.Tests/Services/CredentialStore.</summary>
internal sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _store = new();

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_store.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _store.Remove(key);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Build verify (no commit needed if no test references it yet)**

```bash
dotnet build tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.csproj -c Debug --no-restore
```
Expected: 0 errors.

NOTE: This fixture file is **NOT committed yet** — it has no test that uses it. The actual usage lands in T3 (interface contract test). Move to commit at end of T3.

---

### Task 3: Add InMemoryCredentialStore-using contract test (Core)

**Files:**
- Modify: `tests/PeakCan.Host.Core.Tests/Analysis/ICredentialStoreTests.cs` (add 3 more tests)

**Interfaces:**
- Consumes: Task 1 interface + Task 2 fixture
- Produces: contract tests that verify Get/Set/Delete semantics via InMemoryCredentialStore

- [ ] **Step 1: Append contract tests**

Add to `tests/PeakCan.Host.Core.Tests/Analysis/ICredentialStoreTests.cs`:

```csharp
public class ICredentialStoreContractTests
{
    [Fact]
    public async Task GetAsync_NonExistentKey_ReturnsNull()
    {
        ICredentialStore store = new InMemoryCredentialStore();
        var result = await store.GetAsync("missing");
        result.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_ThenGetAsync_RoundTrips()
    {
        ICredentialStore store = new InMemoryCredentialStore();
        await store.SetAsync("api-key", "sk-test-12345");
        var result = await store.GetAsync("api-key");
        result.Should().Be("sk-test-12345");
    }

    [Fact]
    public async Task DeleteAsync_RemovesKey()
    {
        ICredentialStore store = new InMemoryCredentialStore();
        await store.SetAsync("token", "value");
        await store.DeleteAsync("token");
        var result = await store.GetAsync("token");
        result.Should().BeNull();
    }
}
```

- [ ] **Step 2: Verify GREEN**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-build --nologo -c Debug --filter "FullyQualifiedName~ICredentialStoreTests|FullyQualifiedName~ICredentialStoreContractTests"
```
Expected: 5/5 PASS (2 from T1 + 3 from T3).

- [ ] **Step 3: Commit (T2 fixture + T3 contract tests together)**

```bash
git add tests/PeakCan.Host.Core.Tests/Analysis/InMemoryCredentialStore.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/ICredentialStoreTests.cs
git commit -m "v3.53.1 T2+T3: InMemoryCredentialStore test fixture + 3 contract tests (Core; 5 tests pass)"
```

---

### Task 4: WindowsCredentialManagerStore implementation (App)

**Files:**
- Create: `src/PeakCan.Host.App/Services/CredentialStore/WindowsCredentialManagerStore.cs`

**Interfaces:**
- Implements: `ICredentialStore` (Task 1)
- Produces: Win32 P/Invoke wrapper around advapi32.dll CredRead/CredWrite/CredDelete

- [ ] **Step 1: Create the implementation**

Create `src/PeakCan.Host.App/Services/CredentialStore/WindowsCredentialManagerStore.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
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
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;
    private const int CRED_MAX_CREDENTIAL_BLOB_SIZE = 5 * 512;  // 2560; well under Win32 max 32767

    private readonly ILogger<WindowsCredentialManagerStore> _logger;

    public WindowsCredentialManagerStore(ILogger<WindowsCredentialManagerStore> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var fullKey = KeyPrefix + key;
        if (!CredRead(fullKey, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND)
            {
                _logger.LogDebug("Credential '{Key}' not found in Windows Credential Manager", key);
                return Task.FromResult<string?>(null);
            }
            throw new CredentialStoreException(key,
                $"Failed to read credential '{key}' from Windows Credential Manager (HRESULT 0x{err:X8})");
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            return Task.FromResult<string?>(Marshal.PtrToStringUni(cred.CredentialBlob));
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value.Contains('\0')) throw new ArgumentException("Credential value cannot contain null characters", nameof(value));
        if (Encoding.Unicode.GetByteCount(value) > CRED_MAX_CREDENTIAL_BLOB_SIZE)
            throw new ArgumentException($"Credential value exceeds {CRED_MAX_CREDENTIAL_BLOB_SIZE} bytes", nameof(value));

        var fullKey = KeyPrefix + key;
        var blobBytes = Encoding.Unicode.GetBytes(value);
        var blobPtr = Marshal.AllocHGlobal(blobBytes.Length);
        var targetPtr = Marshal.StringToHGlobalUni(fullKey);
        try
        {
            Marshal.Copy(blobBytes, 0, blobPtr, blobBytes.Length);
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)blobBytes.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!CredWrite(ref credential, 0))
            {
                var err = Marshal.GetLastWin32Error();
                throw new CredentialStoreException(key,
                    $"Failed to write credential '{key}' to Windows Credential Manager (HRESULT 0x{err:X8})");
            }
            _logger.LogInformation("Credential '{Key}' stored in Windows Credential Manager", key);
            return Task.CompletedTask;
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeHGlobal(targetPtr);
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var fullKey = KeyPrefix + key;
        if (!CredDelete(fullKey, CRED_TYPE_GENERIC, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND)
            {
                _logger.LogDebug("Credential '{Key}' not found for delete (no-op)", key);
                return Task.CompletedTask;
            }
            throw new CredentialStoreException(key,
                $"Failed to delete credential '{key}' from Windows Credential Manager (HRESULT 0x{err:X8})");
        }
        _logger.LogInformation("Credential '{Key}' deleted from Windows Credential Manager", key);
        return Task.CompletedTask;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredReadW", SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredWriteW", SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW", SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr cred);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public FILETIME LastWritten;
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

- [ ] **Step 2: Build verify**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```
Expected: 0 errors.

If error: most likely the `using System.Runtime.InteropServices.ComTypes;` import or `FILETIME` struct layout. Check that the App.csproj has `<TargetFramework>net10.0-windows</TargetFramework>` (required for Win32 P/Invoke).

- [ ] **Step 3: Commit**

```bash
git add src/PeakCan.Host.App/Services/CredentialStore/WindowsCredentialManagerStore.cs
git commit -m "v3.53.1 T4: WindowsCredentialManagerStore P/Invoke impl (App; 0 build errors)"
```

---

### Task 5: WindowsCredentialManagerStore integration tests (App)

**Files:**
- Create: `tests/PeakCan.Host.App.Tests/Services/CredentialStore/WindowsCredentialManagerStoreTests.cs`

**Interfaces:**
- Tests: WindowsCredentialManagerStore round-trip + ERROR_NOT_FOUND + delete + overwrite

- [ ] **Step 1: Write the integration tests**

Create `tests/PeakCan.Host.App.Tests/Services/CredentialStore/WindowsCredentialManagerStoreTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.CredentialStore;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.CredentialStore;

[Trait("Category", "WindowsOnly")]
public class WindowsCredentialManagerStoreTests
{
    private static WindowsCredentialManagerStore MakeStore()
        => new(NullLogger<WindowsCredentialManagerStore>.Instance);

    private static string TestKey()
        => $"test-{Guid.NewGuid():N}";  // unique per test to avoid cross-test pollution

    [Fact]
    public async Task GetAsync_NonExistentKey_ReturnsNull()
    {
        var store = MakeStore();
        var result = await store.GetAsync(TestKey());
        result.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_ThenGetAsync_RoundTrips()
    {
        var store = MakeStore();
        var key = TestKey();
        try
        {
            await store.SetAsync(key, "sk-test-roundtrip-value");
            var result = await store.GetAsync(key);
            result.Should().Be("sk-test-roundtrip-value");
        }
        finally { await store.DeleteAsync(key); }
    }

    [Fact]
    public async Task SetAsync_OverwriteExistingKey()
    {
        var store = MakeStore();
        var key = TestKey();
        try
        {
            await store.SetAsync(key, "first-value");
            await store.SetAsync(key, "second-value");
            var result = await store.GetAsync(key);
            result.Should().Be("second-value");
        }
        finally { await store.DeleteAsync(key); }
    }

    [Fact]
    public async Task DeleteAsync_RemovesKey()
    {
        var store = MakeStore();
        var key = TestKey();
        await store.SetAsync(key, "to-be-deleted");
        await store.DeleteAsync(key);
        var result = await store.GetAsync(key);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_NonExistentKey_NoOp()
    {
        var store = MakeStore();
        // Should not throw; ERROR_NOT_FOUND is handled gracefully.
        var act = async () => await store.DeleteAsync(TestKey());
        await act.Should().NotThrowAsync();
    }
}
```

- [ ] **Step 2: Run tests (WindowsOnly trait skipped on non-Windows; this is Windows)**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~WindowsCredentialManagerStoreTests" --logger "console;verbosity=minimal"
```
Expected: 5/5 PASS (Windows env).

- [ ] **Step 3: Commit**

```bash
git add tests/PeakCan.Host.App.Tests/Services/CredentialStore/WindowsCredentialManagerStoreTests.cs
git commit -m "v3.53.1 T5: WindowsCredentialManagerStore integration tests (App; 5 WindowsOnly tests pass)"
```

---

### Task 6: DI registration + verify no regression

**Files:**
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs` (add 1 line)

**Interfaces:**
- Adds: `services.AddSingleton<PeakCan.Host.Core.Analysis.ICredentialStore, PeakCan.Host.App.Services.CredentialStore.WindowsCredentialManagerStore>();`

- [ ] **Step 1: Find injection site**

Run:
```bash
grep -n "AddSingleton\|BuildServiceProvider" src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs | head -10
```

- [ ] **Step 2: Add DI registration**

Insert BEFORE the v3.52.0 analysis pipeline registrations:
```csharp
// v3.53.1 PATCH P1a: API Key secure storage via Windows Credential Manager
services.AddSingleton<PeakCan.Host.Core.Analysis.ICredentialStore,
                     PeakCan.Host.App.Services.CredentialStore.WindowsCredentialManagerStore>();
```

- [ ] **Step 3: Build + test verify**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-build --nologo -c Debug --filter "FullyQualifiedName~WindowsCredentialManagerStoreTests|FullyQualifiedName~AnalysisFlowTests|FullyQualifiedName~AnchorSnapshotFlowTests" --logger "console;verbosity=minimal"
```
Expected: 0 errors; 5 + 4 + 2 = 11 tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs
git commit -m "v3.53.1 T6: DI registration for WindowsCredentialManagerStore (App)"
```

---

### Task 7: Full solution CI + coverage check

- [ ] **Step 1: Full solution build**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore
```
Expected: 0 errors.

- [ ] **Step 2: Full solution test (sequential to avoid pre-existing parallel flakes)**

```bash
dotnet test PeakCan.Host.slnx --no-build --nologo -c Debug --logger "console;verbosity=minimal" -- xUnit.MaxParallelThreads=1
```
Expected: 1433 + 2 (T1) + 3 (T3) + 5 (T5) = **1443 PASS / 0 FAIL / 5 SKIP**.

- [ ] **Step 3: Verify LoC delta**

```bash
git diff --stat 2af2f47..HEAD -- 'src/*' 'tests/*'
```
Expected: ~6 files changed, ~340 LoC.

---

### Task 8: Release notes + tier-3 ship

**Files:**
- Create: `docs/release-notes-v3-53-1.md`
- Modify: `src/Directory.Build.props` (version bump 3.53.0 → 3.53.1)
- Push + PR + squash + tag + GH release

- [ ] **Step 1: Write release notes**

Create `docs/release-notes-v3-53-1.md` covering:
- P1a scope: ICredentialStore + Windows Credential Manager
- API Key 永不 plaintext on disk (DPAPI-encrypted)
- 5 NEW 1/3 lesson candidates
- P1b + P1c explicitly deferred

- [ ] **Step 2: Bump version**

In `src/Directory.Build.props`:
```xml
<Version>3.53.1</Version>
<AssemblyVersion>3.53.1.0</AssemblyVersion>
<FileVersion>3.53.1.0</FileVersion>
<InformationalVersion>3.53.1</InformationalVersion>
```

- [ ] **Step 3: Tier-3 ship**

```bash
git add docs/release-notes-v3-53-1.md src/Directory.Build.props
git commit -m "v3.53.1: version bump + release notes (P1a ICredentialStore + Windows Credential Manager)"
git push -u origin feature/v3-53-1-patch-p1a-credential-store
gh pr create --base main --title "v3.53.1 PATCH P1a: ICredentialStore + Windows Credential Manager (P1 LLM series step 1/3)" --body-file docs/release-notes-v3-53-1.md
gh pr merge --squash --delete-branch
git reset --hard origin/main  # force-correct per sister pattern (v3.52.0/v3.52.1)
git tag -a v3.53.1 -m "v3.53.1 PATCH P1a — ICredentialStore + Windows Credential Manager"
git push origin v3.53.1
gh release create v3.53.1 --notes-file docs/release-notes-v3-53-1.md
```

---

## Sister-lesson candidates to monitor

| Lesson | Status | What T1-T6 might observe |
|---|---|---|
| `core-side-credential-abstraction-must-be-platform-neutral-with-app-layer-impl` | NEW 1/3 (P1a) | T1 + T4: interface in Core, impl in App |
| `win32-pinvoke-credread-must-check-error-not-found-as-null-not-exception` | NEW 1/3 (P1a) | T4: ERROR_NOT_FOUND → return null |
| `credential-store-credential-exception-must-wrap-win32-hresult-with-user-friendly-message` | NEW 1/3 (P1a) | T4: Win32Exception → CredentialStoreException with Key + message |
| `test-fixture-in-memory-credential-store-enables-platform-neutral-core-tests` | NEW 1/3 (P1a) | T2 + T3: InMemoryCredentialStore for Core tests |

## Verification

- `dotnet build PeakCan.Host.slnx`: 0 errors
- `dotnet test PeakCan.Host.slnx`: 1443 PASS / 0 FAIL / 5 SKIP
- Core tests cover interface contract (5 tests, platform-neutral)
- App tests cover Windows integration (5 tests, `[Trait("Category", "WindowsOnly")]`)
- DI registered, no public API surface change beyond `ICredentialStore` + `CredentialStoreException`
- Tag v3.53.1 + GH release published

## Out of scope (YAGNI)

- **P1b PATCH (v3.53.2)**: DeepSeek Provider + HttpClient + JSON request body
- **P1c PATCH (v3.53.3)**: JSON Schema validation + Evidence ID whitelist + offline-mode stub fallback
- **macOS Keychain / Linux libsecret implementations**: peakcan-host is Windows-only
- **API Key rotation / TTL / expiry**: deferred
- **UI 设置界面** for entering API Key: deferred to P1b or P2
- **`appsettings.json` migration**: pre-launch product, no migration needed
- **Self-managed DPAPI encryption**: Windows Credential Manager handles it
- **macOS/Linux test fallback**: skipped on non-Windows via `[Trait]`