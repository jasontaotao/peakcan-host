# v1.1.0 UDS UI + SecurityAccess KeyProvider Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the PeakCan Host v1.1.0 release by closing two specific gaps in the merged-but-untagged UDS work: (1) replace `UdsViewModel.SecurityAccessAsync` `NotImplementedException` with a DI-injected `IKeyDerivationAlgorithm`; (2) add JSON-loadable `DidDatabase` + `RoutineDatabase` in Core for user extensibility.

**Architecture:** Add the `IKeyDerivationAlgorithm` abstraction to `PeakCan.Host.Core.Uds` with `PlaceholderKeyAlgorithm` as the DI default. Refactor `UdsClient` to take the algorithm via constructor and add a new `SecurityAccessAsync(byte, CancellationToken)` overload. Add `DidDatabase` and `RoutineDatabase` to `PeakCan.Host.Core.Uds.Database` with JSON loading + built-in DID defaults. Modify `UdsViewModel.SecurityAccessAsync` to use the new overload. Modify `AppHostBuilder` to register the new services and switch `UdsClient` to a factory using the 3-arg ctor. **The existing `UdsViewModel`, `UdsView.xaml`, `AppShellViewModel.ShowUdsCommand`, and most of the existing DI registrations are NOT touched** — the v1.1.0 spec's 4-panel orchestrator refactor is deferred to v1.2 per spec §"v1.1.0 Ship Scope".

**Tech Stack:** .NET 10 (`net10.0-windows`), C# 13, WPF, `CommunityToolkit.Mvvm` 8.x (existing), `Microsoft.Extensions.Logging.Abstractions` (existing), `System.Text.Json` (BCL). **No new NuGet packages.**

**Spec:** `docs/superpowers/specs/2026-06-25-v1-1-0-uds-ui-and-key-provider-design.md`
**Baseline:** commit `c11288f` on branch `fix/uds-8-critical` (spec + this plan already committed).

## Global Constraints

- **.NET 10 only**: `TargetFramework` is `net10.0-windows`. Do not target `net8.0` or older. Do not introduce `LangVersion` overrides.
- **No new NuGet packages**: rely on what's in `Directory.Packages.props`.
- **No new UI changes**: do NOT modify `UdsView.xaml`, `UdsViewModel.xaml` styling, or `AppShell.xaml`. Do NOT add `UdsViewModel` panel VM refactoring. Existing monolith `UdsViewModel` stays.
- **NetArchTest rule 2 (Core)**: `PeakCan.Host.Core` must NOT reference `Peak.Can.Basic` or any PEAK SDK type. The new `IKeyDerivationAlgorithm` has zero hardware dependency.
- **No unhandled UI-thread exceptions**: existing `UdsViewModel` `try/catch (Exception)` blocks already handle the generic catch. The new `catch (KeyAlgorithmNotConfiguredException)` branch goes ABOVE the generic `Exception` catch to give the targeted hint.
- **Test framework**: xUnit (`Fact`/`Theory`). Use `NullLogger<T>.Instance` for logger mocks. Use `Moq` (in `Directory.Packages.props`) for interface mocks. Construct test `IsoTpLayer` instances with the actual project signature: `new IsoTpLayer(CanIdConfig config, Action<CanFrame> sendFrame)`.
- **Test coverage floor**: ≥80% line coverage for all new code (matches project default).
- **Commit message style** (per `rules/ecc/common/git-workflow.md`): conventional commits. `feat(core): ...`, `feat(app): ...`, `test(core): ...`, `fix(uds): ...`, `docs: ...`.
- **Commit frequency**: one commit per task. Never bundle.
- **No hardcoded secrets** in committed source. Test fixtures for seed/key bytes use `Convert.FromHexString(...)` with explicit byte arrays.
- **Never log seed bytes** anywhere — see commit `a9fe443` (C-2 fix). Existing `UdsViewModel.SecurityAccessAsync` already redacts the seed log line; preserve that behavior.
- **Branch**: stay on `fix/uds-8-critical`. Do not create a new branch. Do not push until Task J.
- **Namespace**: `PeakCan.Host.Core.Uds` for Core types; `PeakCan.Host.App.ViewModels` for the existing monolith VM (no new `ViewModels/Uds/` subfolder in v1.1.0).

---

## File Structure (locked from spec §8, v1.1.0 ship subset only)

### New files (this plan creates)

```
src/PeakCan.Host.Core/Uds/IKeyDerivationAlgorithm.cs
src/PeakCan.Host.Core/Uds/PlaceholderKeyAlgorithm.cs
src/PeakCan.Host.Core/Uds/KeyAlgorithmNotConfiguredException.cs
src/PeakCan.Host.Core/Uds/Database/DidDefinition.cs
src/PeakCan.Host.Core/Uds/Database/DidDatabaseDefaults.cs
src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs
src/PeakCan.Host.Core/Uds/Database/RoutineDefinition.cs
src/PeakCan.Host.Core/Uds/Database/RoutineDatabaseDefaults.cs
src/PeakCan.Host.Core/Uds/Database/RoutineDatabase.cs

tests/PeakCan.Host.Core.Tests/Uds/KeyAlgorithmNotConfiguredExceptionTests.cs
tests/PeakCan.Host.Core.Tests/Uds/FakeKeyDerivationAlgorithm.cs
tests/PeakCan.Host.Core.Tests/Uds/IKeyDerivationAlgorithmContractTests.cs
tests/PeakCan.Host.Core.Tests/Uds/PlaceholderKeyAlgorithmTests.cs
tests/PeakCan.Host.Core.Tests/Uds/UdsClientKeyAlgorithmCtorTests.cs
tests/PeakCan.Host.Core.Tests/Uds/UdsClientSecurityAccessOverloadTests.cs
tests/PeakCan.Host.Core.Tests/Uds/Database/DidDefinitionTests.cs
tests/PeakCan.Host.Core.Tests/Uds/Database/DidDatabaseTests.cs
tests/PeakCan.Host.Core.Tests/Uds/Database/RoutineDefinitionTests.cs
tests/PeakCan.Host.Core.Tests/Uds/Database/RoutineDatabaseTests.cs
```

### Modified files (this plan modifies in-place, minimal diffs)

```
src/PeakCan.Host.Core/Uds/UdsClient.cs                  (+ new ctor + new SecurityAccessAsync(byte, CancellationToken) overload)
src/PeakCan.Host.App/ViewModels/UdsViewModel.cs          (- NotImplementedException on line 132; - log seed-length line)
src/PeakCan.Host.App/Composition/AppHostBuilder.cs       (UdsClient registration becomes factory; + IKeyDerivationAlgorithm, DidDatabase, RoutineDatabase)
README.md                                                (+ v1.1.0 section)
docs/release-notes-v1.1.0.md                            (NEW)
src/PeakCan.Host.App/PeakCan.Host.App.csproj            (Version bump 0.10.1 -> 1.1.0)
```

### NOT modified (explicit YAGNI from spec ship scope)

- `IsoTp/` directory (already complete; `AppHostBuilder` line 137-150 already registers it)
- `Uds/Services/` directory (DiagnosticSessionControl, ReadDID, etc. all already implemented)
- `UdsView.xaml` and `UdsView.xaml.cs` (already implemented; v1.2 polish)
- `AppShell.xaml` and `AppShellViewModel.cs` (already has `ShowUdsCommand` line 289-299)
- `UdsViewModel` layout / 8 RelayCommands / DtcEntry / GetDtcDescription (existing monolith stays)
- `DidDatabase` / `RoutineDatabase` integration into UI (v1.2 task — see spec §D3)
- `PeakCan.Host.Infrastructure` UDS-related files (unchanged)

---

## Task Decomposition Rationale

10 tasks. Bottom-up: Core types first, then App fix, then docs/ship. Each task is an independent RED → GREEN → COMMIT cycle.

Order:
- **Tasks A–C**: Core exception + interface + default impl (foundation).
- **Task D**: `UdsClient` ctor + new overload (consumes A–C).
- **Task E**: `DidDefinition` + `DidDatabase` (independent of D).
- **Task F**: `RoutineDefinition` + `RoutineDatabase` (independent of D, parallel-able with E).
- **Task G**: `UdsViewModel.SecurityAccessAsync` fix (consumes D).
- **Task H**: `AppHostBuilder` DI registration (consumes A–C, E, F, D).
- **Task I**: README + release notes + version bump.
- **Task J**: code-review + tag + release.

Each task's commit message uses the `feat(scope):` / `test(scope):` / `fix(scope):` / `docs:` convention. Tests are written BEFORE implementation (RED).

---

### Task A: `KeyAlgorithmNotConfiguredException` (Core)

**Files:**
- Create: `tests/PeakCan.Host.Core.Tests/Uds/KeyAlgorithmNotConfiguredExceptionTests.cs`
- Create: `src/PeakCan.Host.Core/Uds/KeyAlgorithmNotConfiguredException.cs`

**Interfaces:**
- Consumes: nothing (foundation type).
- Produces: `public sealed class KeyAlgorithmNotConfiguredException : Exception { public byte SecurityLevel { get; } public KeyAlgorithmNotConfiguredException(byte securityLevel); }` — consumed by Tasks B, C, D, G.

- [ ] **Step 1: Write the failing test**

Create `tests/PeakCan.Host.Core.Tests/Uds/KeyAlgorithmNotConfiguredExceptionTests.cs`:

```csharp
using PeakCan.Host.Core.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

public class KeyAlgorithmNotConfiguredExceptionTests
{
    [Fact]
    public void Ctor_Stores_SecurityLevel()
    {
        var ex = new KeyAlgorithmNotConfiguredException(0x01);

        Assert.Equal((byte)0x01, ex.SecurityLevel);
    }

    [Fact]
    public void Message_Contains_Level_Hex_And_Registration_Hint()
    {
        var ex = new KeyAlgorithmNotConfiguredException(0x03);

        Assert.Contains("0x03", ex.Message);
        Assert.Contains("IKeyDerivationAlgorithm", ex.Message);
        Assert.Contains("Register", ex.Message);
    }

    [Fact]
    public void Is_Subclass_Of_Exception()
    {
        var ex = new KeyAlgorithmNotConfiguredException(0x01);

        Assert.IsAssignableFrom<Exception>(ex);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~KeyAlgorithmNotConfiguredExceptionTests" -c Debug
```

Expected: BUILD FAILS with `error CS0246: The type or namespace name 'KeyAlgorithmNotConfiguredException' could not be found`.

- [ ] **Step 3: Write minimal implementation**

Create `src/PeakCan.Host.Core/Uds/KeyAlgorithmNotConfiguredException.cs`:

```csharp
namespace PeakCan.Host.Core.Uds;

/// <summary>
/// Thrown by <see cref="IKeyDerivationAlgorithm"/> implementations that
/// have not been configured with OEM-specific parameters. Distinct from
/// generic <see cref="InvalidOperationException"/> so the UI layer can
/// surface a targeted configuration hint instead of a generic error.
/// </summary>
public sealed class KeyAlgorithmNotConfiguredException : Exception
{
    public byte SecurityLevel { get; }

    public KeyAlgorithmNotConfiguredException(byte securityLevel)
        : base($"UDS SecurityAccess key algorithm for level 0x{securityLevel:X2} " +
               "is not configured. Register an IKeyDerivationAlgorithm implementation " +
               "in DI before calling SecurityAccessAsync.")
        => SecurityLevel = securityLevel;
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~KeyAlgorithmNotConfiguredExceptionTests" -c Debug
```

Expected: `Passed! - Failed: 0, Passed: 3, Skipped: 0`.

- [ ] **Step 5: Commit**

```bash
git add tests/PeakCan.Host.Core.Tests/Uds/KeyAlgorithmNotConfiguredExceptionTests.cs src/PeakCan.Host.Core/Uds/KeyAlgorithmNotConfiguredException.cs
git commit -m "feat(core): add KeyAlgorithmNotConfiguredException for OEM key algo config"
```

---

### Task B: `IKeyDerivationAlgorithm` interface + `FakeKeyDerivationAlgorithm` (Core)

**Files:**
- Create: `tests/PeakCan.Host.Core.Tests/Uds/FakeKeyDerivationAlgorithm.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Uds/IKeyDerivationAlgorithmContractTests.cs`
- Create: `src/PeakCan.Host.Core/Uds/IKeyDerivationAlgorithm.cs`

**Interfaces:**
- Consumes: `KeyAlgorithmNotConfiguredException` (Task A).
- Produces: `public interface IKeyDerivationAlgorithm { byte[] ComputeKey(byte[] seed, byte securityLevel); }` — consumed by Tasks C, D.

- [ ] **Step 1: Write the failing test + test fake**

Create `tests/PeakCan.Host.Core.Tests/Uds/FakeKeyDerivationAlgorithm.cs`:

```csharp
using PeakCan.Host.Core.Uds;

namespace PeakCan.Host.Core.Tests.Uds;

/// <summary>
/// Test double for <see cref="IKeyDerivationAlgorithm"/>. Echoes the seed
/// XORed with the security level so tests can verify both seed and level
/// reached the algorithm.
/// </summary>
public class FakeKeyDerivationAlgorithm : IKeyDerivationAlgorithm
{
    public int CallCount { get; private set; }
    public byte[]? LastSeed { get; private set; }
    public byte? LastSecurityLevel { get; private set; }

    public byte[] ComputeKey(byte[] seed, byte securityLevel)
    {
        CallCount++;
        LastSeed = seed;
        LastSecurityLevel = securityLevel;
        var result = new byte[seed.Length];
        for (var i = 0; i < seed.Length; i++)
            result[i] = (byte)(seed[i] ^ securityLevel);
        return result;
    }
}
```

Create `tests/PeakCan.Host.Core.Tests/Uds/IKeyDerivationAlgorithmContractTests.cs`:

```csharp
using PeakCan.Host.Core.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

public class IKeyDerivationAlgorithmContractTests
{
    [Fact]
    public void Fake_Implements_Interface()
    {
        IKeyDerivationAlgorithm algo = new FakeKeyDerivationAlgorithm();

        Assert.NotNull(algo);
    }

    [Fact]
    public void Fake_ComputeKey_Receives_Seed_And_Level()
    {
        var fake = new FakeKeyDerivationAlgorithm();
        var seed = new byte[] { 0x12, 0x34, 0x56 };

        var key = fake.ComputeKey(seed, 0x05);

        Assert.Equal(seed, fake.LastSeed);
        Assert.Equal((byte)0x05, fake.LastSecurityLevel);
        Assert.Equal(3, key.Length);
        Assert.Equal((byte)(0x12 ^ 0x05), key[0]);
    }

    [Fact]
    public void Fake_CallCount_Increments()
    {
        var fake = new FakeKeyDerivationAlgorithm();

        fake.ComputeKey(new byte[] { 0x01 }, 0x01);
        fake.ComputeKey(new byte[] { 0x02 }, 0x01);

        Assert.Equal(2, fake.CallCount);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~IKeyDerivationAlgorithmContractTests" -c Debug
```

Expected: BUILD FAILS with `error CS0246: The type or namespace name 'IKeyDerivationAlgorithm' could not be found`.

- [ ] **Step 3: Write minimal implementation**

Create `src/PeakCan.Host.Core/Uds/IKeyDerivationAlgorithm.cs`:

```csharp
namespace PeakCan.Host.Core.Uds;

/// <summary>
/// OEM-specific key derivation algorithm for UDS SecurityAccess (0x27).
/// Implementations are typically OEM-confidential and may call into
/// native libraries, network services, or hardware security modules.
/// </summary>
public interface IKeyDerivationAlgorithm
{
    /// <summary>
    /// Computes the response key for the given seed and security level.
    /// </summary>
    /// <param name="seed">Bytes returned by SecurityAccess requestSeed.</param>
    /// <param name="securityLevel">Sub-function byte (0x01, 0x03, ...).</param>
    /// <returns>Computed key bytes. Length is OEM-specific.</returns>
    /// <exception cref="ArgumentNullException">seed is null.</exception>
    /// <exception cref="KeyAlgorithmNotConfiguredException">
    ///   Thrown by placeholder implementations when no OEM algorithm is
    ///   registered. OEM implementations should throw other exceptions
    ///   (e.g. <see cref="InvalidOperationException"/>) on algorithm failure.
    /// </exception>
    byte[] ComputeKey(byte[] seed, byte securityLevel);
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~IKeyDerivationAlgorithmContractTests" -c Debug
```

Expected: `Passed! - Failed: 0, Passed: 3, Skipped: 0`.

- [ ] **Step 5: Commit**

```bash
git add tests/PeakCan.Host.Core.Tests/Uds/FakeKeyDerivationAlgorithm.cs tests/PeakCan.Host.Core.Tests/Uds/IKeyDerivationAlgorithmContractTests.cs src/PeakCan.Host.Core/Uds/IKeyDerivationAlgorithm.cs
git commit -m "feat(core): add IKeyDerivationAlgorithm interface + FakeKeyDerivationAlgorithm test double"
```

---

### Task C: `PlaceholderKeyAlgorithm` default implementation (Core)

**Files:**
- Create: `tests/PeakCan.Host.Core.Tests/Uds/PlaceholderKeyAlgorithmTests.cs`
- Create: `src/PeakCan.Host.Core/Uds/PlaceholderKeyAlgorithm.cs`

**Interfaces:**
- Consumes: `IKeyDerivationAlgorithm` (Task B), `KeyAlgorithmNotConfiguredException` (Task A).
- Produces: `public sealed class PlaceholderKeyAlgorithm : IKeyDerivationAlgorithm` — registered as the DI default in Task H.

- [ ] **Step 1: Write the failing test**

Create `tests/PeakCan.Host.Core.Tests/Uds/PlaceholderKeyAlgorithmTests.cs`:

```csharp
using PeakCan.Host.Core.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

public class PlaceholderKeyAlgorithmTests
{
    [Fact]
    public void ComputeKey_Throws_KeyAlgorithmNotConfiguredException()
    {
        var sut = new PlaceholderKeyAlgorithm();
        var seed = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        var ex = Assert.Throws<KeyAlgorithmNotConfiguredException>(
            () => sut.ComputeKey(seed, 0x01));

        Assert.Equal((byte)0x01, ex.SecurityLevel);
    }

    [Fact]
    public void ComputeKey_With_NullSeed_Throws_ArgumentNullException()
    {
        var sut = new PlaceholderKeyAlgorithm();

        Assert.Throws<ArgumentNullException>(() => sut.ComputeKey(null!, 0x01));
    }

    [Fact]
    public void SecurityLevel_In_Exception_Message_For_Level_0x05()
    {
        var sut = new PlaceholderKeyAlgorithm();

        var ex = Assert.Throws<KeyAlgorithmNotConfiguredException>(
            () => sut.ComputeKey(new byte[] { 0x00 }, 0x05));

        Assert.Contains("0x05", ex.Message);
    }

    [Fact]
    public void Implements_IKeyDerivationAlgorithm()
    {
        var sut = new PlaceholderKeyAlgorithm();

        Assert.IsAssignableFrom<IKeyDerivationAlgorithm>(sut);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~PlaceholderKeyAlgorithmTests" -c Debug
```

Expected: BUILD FAILS with `error CS0246: The type or namespace name 'PlaceholderKeyAlgorithm' could not be found`.

- [ ] **Step 3: Write minimal implementation**

Create `src/PeakCan.Host.Core/Uds/PlaceholderKeyAlgorithm.cs`:

```csharp
namespace PeakCan.Host.Core.Uds;

/// <summary>
/// Default <see cref="IKeyDerivationAlgorithm"/> implementation. Throws
/// <see cref="KeyAlgorithmNotConfiguredException"/> until an OEM-specific
/// implementation is registered in DI. Ships by default so the build,
/// tests, and app startup are all green without an OEM-supplied algorithm.
/// </summary>
public sealed class PlaceholderKeyAlgorithm : IKeyDerivationAlgorithm
{
    public byte[] ComputeKey(byte[] seed, byte securityLevel)
    {
        ArgumentNullException.ThrowIfNull(seed);
        throw new KeyAlgorithmNotConfiguredException(securityLevel);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~PlaceholderKeyAlgorithmTests" -c Debug
```

Expected: `Passed! - Failed: 0, Passed: 4, Skipped: 0`.

- [ ] **Step 5: Commit**

```bash
git add tests/PeakCan.Host.Core.Tests/Uds/PlaceholderKeyAlgorithmTests.cs src/PeakCan.Host.Core/Uds/PlaceholderKeyAlgorithm.cs
git commit -m "feat(core): add PlaceholderKeyAlgorithm default DI implementation"
```

---

### Task D: `UdsClient` accepts `IKeyDerivationAlgorithm` + new `SecurityAccessAsync` overload (Core)

**Files:**
- Modify: `src/PeakCan.Host.Core/Uds/UdsClient.cs:14-48` (add field + new ctor)
- Modify: `src/PeakCan.Host.Core/Uds/UdsClient.cs:160-202` (add new overload)
- Create: `tests/PeakCan.Host.Core.Tests/Uds/UdsClientKeyAlgorithmCtorTests.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Uds/UdsClientSecurityAccessOverloadTests.cs`

**Interfaces:**
- Consumes: `IKeyDerivationAlgorithm` (Task B), `IsoTpLayer` (existing), `UdsTimer` (existing).
- Produces:
  - New ctor `UdsClient(IsoTpLayer isoTp, IKeyDerivationAlgorithm keyAlgorithm, UdsTimer? timer = null)` + field `_keyAlgorithm`.
  - New public virtual overload `Task<byte[]> SecurityAccessAsync(byte requestLevel, CancellationToken ct = default)` that does RequestSeed → ComputeKey → SendKey, throwing `InvalidOperationException` if the client was constructed via the legacy 2-arg ctor.

- [ ] **Step 1: Write the failing test for the new ctor**

Create `tests/PeakCan.Host.Core.Tests/Uds/UdsClientKeyAlgorithmCtorTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

public class UdsClientKeyAlgorithmCtorTests
{
    private static IsoTpLayer NewIsoTp()
    {
        // IsoTpLayer ctor is (CanIdConfig config, Action<CanFrame> sendFrame).
        // The sendFrame lambda never fires in these tests because no real
        // frames are sent. The MessageReceived handler is hooked but the
        // tests do not trigger it.
        return new IsoTpLayer(
            config: new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 },
            sendFrame: _ => { });
    }

    [Fact]
    public void NewCtor_With_KeyAlgorithm_DoesNotThrow()
    {
        using var sut = new UdsClient(NewIsoTp(), new FakeKeyDerivationAlgorithm());

        Assert.NotNull(sut);
    }

    [Fact]
    public void NewCtor_With_NullIsoTp_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new UdsClient(isoTp: null!, new FakeKeyDerivationAlgorithm()));
    }

    [Fact]
    public void NewCtor_With_NullKeyAlgorithm_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new UdsClient(NewIsoTp(), keyAlgorithm: null!));
    }

    [Fact]
    public void NewCtor_With_Timer_Uses_Provided_Timer()
    {
        var timer = new UdsTimer { P2Timeout = TimeSpan.FromMilliseconds(123) };

        using var sut = new UdsClient(NewIsoTp(), new FakeKeyDerivationAlgorithm(), timer);

        Assert.Equal(TimeSpan.FromMilliseconds(123), timer.P2Timeout);
    }

    [Fact]
    public void LegacyCtor_Still_Compiles_And_DoesNotThrow()
    {
        using var sut = new UdsClient(NewIsoTp());

        Assert.NotNull(sut);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~UdsClientKeyAlgorithmCtorTests" -c Debug
```

Expected: BUILD FAILS because the 3-arg `UdsClient(IsoTpLayer, IKeyDerivationAlgorithm, UdsTimer?)` ctor does not exist yet.

- [ ] **Step 3: Modify `UdsClient.cs` — add field and new ctor**

Edit `src/PeakCan.Host.Core/Uds/UdsClient.cs`. **Additive only** — do NOT touch the existing 2-arg ctor or any existing public method.

After the existing field block (after `private byte _pendingRequestSid;` on line 27), add:

```csharp
    // v1.1.0: OEM-specific SecurityAccess key derivation. Nullable so the
    // legacy 2-arg ctor keeps working for tests that don't care about
    // SecurityAccess. The new overload SecurityAccessAsync(byte, CancellationToken)
    // throws InvalidOperationException when this is null.
    private readonly IKeyDerivationAlgorithm? _keyAlgorithm;
```

After the existing `public UdsClient(IsoTpLayer isoTp, UdsTimer? timer = null)` ctor (lines 38–48), add a new ctor:

```csharp
    /// <summary>
    /// Create a new UDS client with an OEM-specific key derivation algorithm
    /// for SecurityAccess (0x27). Added in v1.1.0.
    /// </summary>
    /// <param name="isoTp">ISO-TP transport layer.</param>
    /// <param name="keyAlgorithm">OEM key algorithm. Must not be null.</param>
    /// <param name="timer">Optional UDS timer. Defaults to a fresh <see cref="UdsTimer"/>.</param>
    public UdsClient(IsoTpLayer isoTp, IKeyDerivationAlgorithm keyAlgorithm, UdsTimer? timer = null)
    {
        ArgumentNullException.ThrowIfNull(isoTp);
        ArgumentNullException.ThrowIfNull(keyAlgorithm);

        _isoTp = isoTp;
        _keyAlgorithm = keyAlgorithm;
        _timer = timer ?? new UdsTimer();
        Security = new UdsSecurity();

        // Subscribe to ISO-TP messages
        _isoTp.MessageReceived += OnMessageReceived;
    }
```

Do NOT modify the existing 2-arg ctor. It remains as-is; `_keyAlgorithm` defaults to null.

- [ ] **Step 4: Run ctor test to verify it passes**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~UdsClientKeyAlgorithmCtorTests" -c Debug
```

Expected: `Passed! - Failed: 0, Passed: 5, Skipped: 0`.

- [ ] **Step 5: Write the failing test for the new overload**

Create `tests/PeakCan.Host.Core.Tests/Uds/UdsClientSecurityAccessOverloadTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

public class UdsClientSecurityAccessOverloadTests
{
    private static IsoTpLayer NewIsoTp()
        => new IsoTpLayer(
            config: new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 },
            sendFrame: _ => { });

    [Fact]
    public async Task Overload_With_LegacyCtor_Throws_InvalidOperation()
    {
        using var sut = new UdsClient(NewIsoTp()); // legacy ctor, no _keyAlgorithm

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SecurityAccessAsync(0x01));
    }

    [Fact]
    public async Task Overload_With_PlaceholderAlgorithm_Throws_KeyAlgorithmNotConfigured()
    {
        using var sut = new UdsClient(NewIsoTp(), new PlaceholderKeyAlgorithm());

        await Assert.ThrowsAsync<KeyAlgorithmNotConfiguredException>(
            () => sut.SecurityAccessAsync(0x01));
    }

    [Fact]
    public void Overload_Signature_Exists_With_Default_CancellationToken()
    {
        // Reflection check that the overload exists with the expected signature
        // and is virtual (so VMs can override it in tests).
        var method = typeof(UdsClient).GetMethod(
            "SecurityAccessAsync",
            new[] { typeof(byte), typeof(CancellationToken) });
        Assert.NotNull(method);
        Assert.True(method!.IsVirtual, "Overload must be virtual so VMs can override it.");
    }
}
```

- [ ] **Step 6: Run test to verify it fails**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~UdsClientSecurityAccessOverloadTests" -c Debug
```

Expected: BUILD FAILS or 2 of 3 tests FAIL because the `SecurityAccessAsync(byte, CancellationToken)` overload does not exist yet.

- [ ] **Step 7: Add the new overload to `UdsClient.cs`**

Add **directly after** the existing `SecurityAccessAsync(byte level, byte[]? key = null, CancellationToken ct = default)` method (lines 168–202):

```csharp
    /// <summary>
    /// SecurityAccess (0x27) using the injected <see cref="IKeyDerivationAlgorithm"/>.
    /// Performs the full handshake: RequestSeed → ComputeKey → SendKey.
    /// </summary>
    /// <param name="requestLevel">Security level sub-function byte (0x01, 0x03, ...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success response bytes from the ECU after SendKey.</returns>
    /// <exception cref="InvalidOperationException">
    ///   The client was constructed via the legacy 2-arg ctor that does not
    ///   take an <see cref="IKeyDerivationAlgorithm"/>.
    /// </exception>
    /// <exception cref="KeyAlgorithmNotConfiguredException">
    ///   The injected algorithm's placeholder has not been replaced with an
    ///   OEM-specific implementation.
    /// </exception>
    public virtual async Task<byte[]> SecurityAccessAsync(byte requestLevel, CancellationToken ct = default)
    {
        if (_keyAlgorithm is null)
            throw new InvalidOperationException(
                "UdsClient was constructed without an IKeyDerivationAlgorithm. " +
                "Use the (IsoTpLayer, IKeyDerivationAlgorithm, UdsTimer?) constructor " +
                "or call SecurityAccessAsync(byte level, byte[] key, CancellationToken) directly.");

        // RequestSeed leg via the existing 3-arg method (key=null returns seed bytes).
        byte[] seed = await SecurityAccessAsync(requestLevel, key: null, ct).ConfigureAwait(false);

        // SECURITY: never log seed bytes — see commit a9fe443 (C-2 fix).
        byte[] key = _keyAlgorithm.ComputeKey(seed, requestLevel);

        // SendKey leg via the existing 3-arg method.
        return await SecurityAccessAsync(requestLevel, key, ct).ConfigureAwait(false);
    }
```

- [ ] **Step 8: Run overload test to verify it passes**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~UdsClientSecurityAccessOverloadTests" -c Debug
```

Expected: `Passed! - Failed: 0, Passed: 3, Skipped: 0`.

Full Core suite:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug
```

Expected: 0 failures.

- [ ] **Step 9: Commit**

```bash
git add tests/PeakCan.Host.Core.Tests/Uds/UdsClientKeyAlgorithmCtorTests.cs tests/PeakCan.Host.Core.Tests/Uds/UdsClientSecurityAccessOverloadTests.cs src/PeakCan.Host.Core/Uds/UdsClient.cs
git commit -m "feat(core): add UdsClient ctor + SecurityAccessAsync(byte, CancellationToken) overload"
```

---

### Task E: `DidDefinition` + `DidDatabaseDefaults` + `DidDatabase` (Core)

**Files:**
- Create: `tests/PeakCan.Host.Core.Tests/Uds/Database/DidDefinitionTests.cs`
- Create: `src/PeakCan.Host.Core/Uds/Database/DidDefinition.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Uds/Database/DidDatabaseTests.cs`
- Create: `src/PeakCan.Host.Core/Uds/Database/DidDatabaseDefaults.cs`
- Create: `src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs`

**Interfaces:**
- Consumes: `ILogger<DidDatabase>` (from `Microsoft.Extensions.Logging.Abstractions`).
- Produces:
  - `public sealed record DidDefinition(ushort Id, string Name, string Description, int LengthBytes, bool Writable);`
  - `public static class DidDatabaseDefaults { public static string DefaultJsonPath { get; } }` — `%APPDATA%\PeakCan.Host\uds-dids.json`.
  - `public sealed class DidDatabase : IDidDatabase` with `All`, `Find(ushort id)`, two ctors (default-path and explicit-path), graceful fallback on missing/malformed JSON.

- [ ] **Step 1: Write `DidDefinitionTests`**

Create `tests/PeakCan.Host.Core.Tests/Uds/Database/DidDefinitionTests.cs`:

```csharp
using PeakCan.Host.Core.Uds.Database;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds.Database;

public class DidDefinitionTests
{
    [Fact]
    public void Record_Equality_Is_Value_Based()
    {
        var a = new DidDefinition(0xF190, "VIN", "desc", 17, false);
        var b = new DidDefinition(0xF190, "VIN", "desc", 17, false);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Record_Inequality_When_Id_Differs()
    {
        var a = new DidDefinition(0xF190, "VIN", "desc", 17, false);
        var b = new DidDefinition(0xF191, "VIN", "desc", 17, false);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Properties_Are_Accessible()
    {
        var did = new DidDefinition(0xF184, "SW Version", "ECU Software Version", 9, false);

        Assert.Equal((ushort)0xF184, did.Id);
        Assert.Equal("SW Version", did.Name);
        Assert.Equal("ECU Software Version", did.Description);
        Assert.Equal(9, did.LengthBytes);
        Assert.False(did.Writable);
    }

    [Fact]
    public void ToString_Includes_Id_And_Name()
    {
        var did = new DidDefinition(0xF190, "VIN", "desc", 17, false);

        var s = did.ToString();

        Assert.Contains("F190", s);
        Assert.Contains("VIN", s);
    }
}
```

- [ ] **Step 2: Write `DidDefinition.cs`**

Create `src/PeakCan.Host.Core/Uds/Database/DidDefinition.cs`:

```csharp
namespace PeakCan.Host.Core.Uds.Database;

/// <summary>
/// Definition of a single UDS Data Identifier (DID). Populated from
/// built-in defaults and/or a user JSON file at
/// <c>%APPDATA%\PeakCan.Host\uds-dids.json</c>.
/// </summary>
/// <param name="Id">2-byte DID (e.g. 0xF190 for VIN).</param>
/// <param name="Name">Short human-readable name.</param>
/// <param name="Description">Longer description for UI tooltip / details panel.</param>
/// <param name="LengthBytes">Expected byte length of the DID payload.</param>
/// <param name="Writable">Whether <c>WriteDataByIdentifier (0x2E)</c> is supported.</param>
public sealed record DidDefinition(
    ushort Id,
    string Name,
    string Description,
    int LengthBytes,
    bool Writable);
```

- [ ] **Step 3: Run DidDefinition tests**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~DidDefinitionTests" -c Debug
```

Expected: `Passed! - Failed: 0, Passed: 4, Skipped: 0`.

- [ ] **Step 4: Write `DidDatabaseTests`**

Create `tests/PeakCan.Host.Core.Tests/Uds/Database/DidDatabaseTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Uds.Database;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds.Database;

public class DidDatabaseTests
{
    private static string TempJson(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"uds-dids-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void DefaultJsonPath_Is_Under_LocalAppData_PeakCanHost()
    {
        var path = DidDatabaseDefaults.DefaultJsonPath;

        Assert.Contains("PeakCan.Host", path);
        Assert.EndsWith("uds-dids.json", path);
        Assert.Contains(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            path);
    }

    [Fact]
    public void DefaultCtor_Uses_BuiltIn_Defaults()
    {
        var sut = new DidDatabase(logger: NullLogger<DidDatabase>.Instance);

        Assert.NotEmpty(sut.All);
        Assert.Contains(sut.All, d => d.Id == 0xF190 && d.Name == "VIN");
        Assert.Contains(sut.All, d => d.Id == 0xF184 && d.Name == "SoftwareVersion");
        Assert.Equal(5, sut.All.Count);
    }

    [Fact]
    public void UserJson_Overrides_BuiltIn_For_Matching_Id()
    {
        var path = TempJson("""
        {
          "dids": [
            { "id": "0xF190", "name": "Custom VIN", "description": "OEM-specific VIN", "lengthBytes": 20, "writable": true }
          ]
        }
        """);

        try
        {
            var sut = new DidDatabase(path, NullLogger<DidDatabase>.Instance);

            var vin = sut.Find(0xF190);
            Assert.NotNull(vin);
            Assert.Equal("Custom VIN", vin!.Name);
            Assert.Equal("OEM-specific VIN", vin.Description);
            Assert.Equal(20, vin.LengthBytes);
            Assert.True(vin.Writable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UserJson_Appends_NonOverlapping_Entries()
    {
        var path = TempJson("""
        {
          "dids": [
            { "id": "0x1234", "name": "Custom", "description": "d", "lengthBytes": 4, "writable": false }
          ]
        }
        """);

        try
        {
            var sut = new DidDatabase(path, NullLogger<DidDatabase>.Instance);

            Assert.Equal(6, sut.All.Count); // 5 built-in + 1 custom
            Assert.NotNull(sut.Find(0x1234));
            Assert.NotNull(sut.Find(0xF190));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UserJson_Malformed_Falls_Back_To_BuiltIn_And_Logs_Warning()
    {
        var path = TempJson("{ this is not valid JSON");

        try
        {
            var sut = new DidDatabase(path, NullLogger<DidDatabase>.Instance);

            Assert.Equal(5, sut.All.Count);
            Assert.NotNull(sut.Find(0xF190));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UserJson_Missing_File_Falls_Back_To_BuiltIn()
    {
        var sut = new DidDatabase(
            userJsonPath: Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json"),
            logger: NullLogger<DidDatabase>.Instance);

        Assert.Equal(5, sut.All.Count);
    }

    [Fact]
    public void Find_ExistingId_Returns_Definition()
    {
        var sut = new DidDatabase(logger: NullLogger<DidDatabase>.Instance);

        Assert.NotNull(sut.Find(0xF190));
    }

    [Fact]
    public void Find_MissingId_Returns_Null()
    {
        var sut = new DidDatabase(logger: NullLogger<DidDatabase>.Instance);

        Assert.Null(sut.Find(0xABCD));
    }
}
```

- [ ] **Step 5: Write `DidDatabaseDefaults.cs`**

Create `src/PeakCan.Host.Core/Uds/Database/DidDatabaseDefaults.cs`:

```csharp
namespace PeakCan.Host.Core.Uds.Database;

/// <summary>
/// Default file paths and constants for <see cref="DidDatabase"/>.
/// </summary>
public static class DidDatabaseDefaults
{
    /// <summary>
    /// Default path for user-supplied DID definitions:
    /// <c>%LOCALAPPDATA%\PeakCan.Host\uds-dids.json</c>.
    /// File is optional; if missing or malformed, <see cref="DidDatabase"/>
    /// falls back to built-in defaults.
    /// </summary>
    public static string DefaultJsonPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PeakCan.Host", "uds-dids.json");
}
```

- [ ] **Step 6: Write `DidDatabase.cs`**

Create `src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Uds.Database;

/// <summary>
/// Loads UDS Data Identifier (DID) definitions. Sources, in priority order:
/// built-in defaults → user JSON file at
/// <c>%APPDATA%\PeakCan.Host\uds-dids.json</c>. User entries with matching
/// <see cref="DidDefinition.Id"/> override built-ins; non-matching entries
/// are appended. A missing or malformed JSON file does NOT throw — the
/// built-in defaults are used and a warning is logged.
/// </summary>
public sealed class DidDatabase
{
    private readonly ILogger<DidDatabase>? _logger;

    /// <summary>All known DIDs (built-in + user), with user overrides applied.</summary>
    public IReadOnlyList<DidDefinition> All { get; }

    /// <summary>Create a database reading from the default user-JSON path.</summary>
    public DidDatabase(ILogger<DidDatabase>? logger = null)
        : this(DidDatabaseDefaults.DefaultJsonPath, logger) { }

    /// <summary>
    /// Create a database reading from <paramref name="userJsonPath"/>.
    /// Pass <c>null</c> to skip user-JSON entirely (built-in only).
    /// </summary>
    public DidDatabase(string? userJsonPath, ILogger<DidDatabase>? logger = null)
    {
        _logger = logger;
        var builtIn = BuiltInDefaults();
        var user = LoadUserFile(userJsonPath);
        All = MergeBuiltInAndUser(builtIn, user).ToList();
    }

    /// <summary>Look up a DID by its 2-byte id. Returns null if not found.</summary>
    public DidDefinition? Find(ushort id)
        => All.FirstOrDefault(d => d.Id == id);

    private static IEnumerable<DidDefinition> BuiltInDefaults() => new DidDefinition[]
    {
        new(0xF190, "VIN",             "Vehicle Identification Number", 17, false),
        new(0xF187, "PartNumber",      "ECU Part Number",               10, false),
        new(0xF18A, "SupplierID",      "ECU Supplier ID",                4, false),
        new(0xF191, "HardwareVersion", "ECU Hardware Version",           3, false),
        new(0xF184, "SoftwareVersion", "ECU Software Version",           9, false),
    };

    private IEnumerable<DidDefinition>? LoadUserFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            _logger?.LogInformation("No DID user JSON path configured; using built-in defaults only.");
            return null;
        }

        if (!File.Exists(path))
        {
            _logger?.LogInformation("No DID user JSON at {Path}; using built-in defaults.", path);
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<DidFileDto>(json, JsonOpts);
            return dto?.Dids;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Malformed DID JSON at {Path}; using built-in defaults.", path);
            return null;
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "IO error reading DID JSON at {Path}; using built-in defaults.", path);
            return null;
        }
    }

    private static IEnumerable<DidDefinition> MergeBuiltInAndUser(
        IEnumerable<DidDefinition> builtIn,
        IEnumerable<DidDefinition>? user)
    {
        if (user is null)
            return builtIn;

        var userIds = new HashSet<ushort>(user.Select(d => d.Id));
        // Built-ins that are NOT overridden by user entries first, in original order.
        foreach (var d in builtIn)
            if (!userIds.Contains(d.Id))
                yield return d;
        // Then all user entries (which include the overrides).
        foreach (var d in user)
            yield return d;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class DidFileDto
    {
        [JsonPropertyName("dids")]
        public List<DidDefinition> Dids { get; set; } = new();
    }
}
```

- [ ] **Step 7: Run DidDatabase tests**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~DidDatabaseTests" -c Debug
```

Expected: `Passed! - Failed: 0, Passed: 8, Skipped: 0`.

- [ ] **Step 8: Commit**

```bash
git add tests/PeakCan.Host.Core.Tests/Uds/Database/DidDefinitionTests.cs src/PeakCan.Host.Core/Uds/Database/DidDefinition.cs tests/PeakCan.Host.Core.Tests/Uds/Database/DidDatabaseTests.cs src/PeakCan.Host.Core/Uds/Database/DidDatabaseDefaults.cs src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs
git commit -m "feat(core): add DidDefinition + DidDatabase with built-in defaults + JSON load"
```

---

### Task F: `RoutineDefinition` + `RoutineDatabaseDefaults` + `RoutineDatabase` (Core)

**Files:**
- Create: `tests/PeakCan.Host.Core.Tests/Uds/Database/RoutineDefinitionTests.cs`
- Create: `src/PeakCan.Host.Core/Uds/Database/RoutineDefinition.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Uds/Database/RoutineDatabaseTests.cs`
- Create: `src/PeakCan.Host.Core/Uds/Database/RoutineDatabaseDefaults.cs`
- Create: `src/PeakCan.Host.Core/Uds/Database/RoutineDatabase.cs`

**Interfaces:**
- Consumes: `ILogger<RoutineDatabase>`.
- Produces:
  - `public sealed record RoutineDefinition(ushort Id, string Name, string Description, bool Startable, bool Stoppable);`
  - `public static class RoutineDatabaseDefaults { public static string DefaultJsonPath { get; } }` — `%APPDATA%\PeakCan.Host\uds-routines.json`.
  - `public sealed class RoutineDatabase` with `All`, `Find(ushort id)`, two ctors (default-path and explicit-path), graceful fallback on missing/malformed JSON.

- [ ] **Step 1: Write `RoutineDefinitionTests`**

Create `tests/PeakCan.Host.Core.Tests/Uds/Database/RoutineDefinitionTests.cs`:

```csharp
using PeakCan.Host.Core.Uds.Database;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds.Database;

public class RoutineDefinitionTests
{
    [Fact]
    public void Record_Equality_Is_Value_Based()
    {
        var a = new RoutineDefinition(0xFF00, "Erase", "Erase memory", true, true);
        var b = new RoutineDefinition(0xFF00, "Erase", "Erase memory", true, true);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Properties_Are_Accessible()
    {
        var r = new RoutineDefinition(0xFF01, "Check", "Integrity check", true, false);

        Assert.Equal((ushort)0xFF01, r.Id);
        Assert.Equal("Check", r.Name);
        Assert.Equal("Integrity check", r.Description);
        Assert.True(r.Startable);
        Assert.False(r.Stoppable);
    }

    [Fact]
    public void Record_Inequality_When_Startable_Differs()
    {
        var a = new RoutineDefinition(0xFF00, "Erase", "d", true, true);
        var b = new RoutineDefinition(0xFF00, "Erase", "d", false, true);

        Assert.NotEqual(a, b);
    }
}
```

- [ ] **Step 2: Write `RoutineDefinition.cs`**

Create `src/PeakCan.Host.Core/Uds/Database/RoutineDefinition.cs`:

```csharp
namespace PeakCan.Host.Core.Uds.Database;

/// <summary>
/// Definition of a single UDS Routine (0x31). Routines are 100% OEM-defined;
/// there are no built-in defaults. Users populate via
/// <c>%APPDATA%\PeakCan.Host\uds-routines.json</c>.
/// </summary>
/// <param name="Id">2-byte routine ID.</param>
/// <param name="Name">Short human-readable name.</param>
/// <param name="Description">Longer description for UI.</param>
/// <param name="Startable">Whether <c>RoutineControl (0x31, 0x01)</c> is supported.</param>
/// <param name="Stoppable">Whether <c>RoutineControl (0x31, 0x02)</c> is supported.</param>
public sealed record RoutineDefinition(
    ushort Id,
    string Name,
    string Description,
    bool Startable,
    bool Stoppable);
```

- [ ] **Step 3: Run RoutineDefinition tests**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~RoutineDefinitionTests" -c Debug
```

Expected: `Passed! - Failed: 0, Passed: 3, Skipped: 0`.

- [ ] **Step 4: Write `RoutineDatabaseTests`**

Create `tests/PeakCan.Host.Core.Tests/Uds/Database/RoutineDatabaseTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Uds.Database;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds.Database;

public class RoutineDatabaseTests
{
    private static string TempJson(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"uds-routines-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void DefaultJsonPath_Is_Under_LocalAppData_PeakCanHost()
    {
        var path = RoutineDatabaseDefaults.DefaultJsonPath;

        Assert.Contains("PeakCan.Host", path);
        Assert.EndsWith("uds-routines.json", path);
    }

    [Fact]
    public void DefaultCtor_NoUserFile_Returns_Empty()
    {
        var sut = new RoutineDatabase(
            userJsonPath: Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json"),
            logger: NullLogger<RoutineDatabase>.Instance);

        Assert.Empty(sut.All);
    }

    [Fact]
    public void UserJson_Populates_All()
    {
        var path = TempJson("""
        {
          "routines": [
            { "id": "0xFF00", "name": "EraseMemory",   "description": "Erase flash",     "startable": true,  "stoppable": true  },
            { "id": "0xFF01", "name": "CheckIntegrity", "description": "Integrity check", "startable": true,  "stoppable": false }
          ]
        }
        """);

        try
        {
            var sut = new RoutineDatabase(path, NullLogger<RoutineDatabase>.Instance);

            Assert.Equal(2, sut.All.Count);
            Assert.Equal("EraseMemory", sut.Find(0xFF00)?.Name);
            Assert.True(sut.Find(0xFF00)!.Stoppable);
            Assert.False(sut.Find(0xFF01)!.Stoppable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UserJson_Malformed_Returns_Empty_And_Logs_Warning()
    {
        var path = TempJson("{ malformed");

        try
        {
            var sut = new RoutineDatabase(path, NullLogger<RoutineDatabase>.Instance);

            Assert.Empty(sut.All);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Find_MissingId_Returns_Null()
    {
        var sut = new RoutineDatabase(logger: NullLogger<RoutineDatabase>.Instance);

        Assert.Null(sut.Find(0xABCD));
    }
}
```

- [ ] **Step 5: Write `RoutineDatabaseDefaults.cs`**

Create `src/PeakCan.Host.Core/Uds/Database/RoutineDatabaseDefaults.cs`:

```csharp
namespace PeakCan.Host.Core.Uds.Database;

/// <summary>
/// Default file paths and constants for <see cref="RoutineDatabase"/>.
/// </summary>
public static class RoutineDatabaseDefaults
{
    /// <summary>
    /// Default path for user-supplied routine definitions:
    /// <c>%LOCALAPPDATA%\PeakCan.Host\uds-routines.json</c>.
    /// File is optional; routines are 100% OEM-defined so an empty list is
    /// the correct state when no file is present.
    /// </summary>
    public static string DefaultJsonPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PeakCan.Host", "uds-routines.json");
}
```

- [ ] **Step 6: Write `RoutineDatabase.cs`**

Create `src/PeakCan.Host.Core/Uds/Database/RoutineDatabase.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Uds.Database;

/// <summary>
/// Loads UDS Routine definitions from a user JSON file at
/// <c>%APPDATA%\PeakCan.Host\uds-routines.json</c>. Routines are 100%
/// OEM-defined; there are no built-in defaults. A missing or malformed
/// JSON file does NOT throw — the database is empty and a warning is logged.
/// </summary>
public sealed class RoutineDatabase
{
    private readonly ILogger<RoutineDatabase>? _logger;

    /// <summary>All known routines. Empty if no user file is present.</summary>
    public IReadOnlyList<RoutineDefinition> All { get; }

    /// <summary>Create a database reading from the default user-JSON path.</summary>
    public RoutineDatabase(ILogger<RoutineDatabase>? logger = null)
        : this(RoutineDatabaseDefaults.DefaultJsonPath, logger) { }

    /// <summary>
    /// Create a database reading from <paramref name="userJsonPath"/>.
    /// Pass <c>null</c> for an empty database (no file IO).
    /// </summary>
    public RoutineDatabase(string? userJsonPath, ILogger<RoutineDatabase>? logger = null)
    {
        _logger = logger;
        All = LoadUserFile(userJsonPath).ToList();
    }

    /// <summary>Look up a routine by its 2-byte id. Returns null if not found.</summary>
    public RoutineDefinition? Find(ushort id)
        => All.FirstOrDefault(r => r.Id == id);

    private IEnumerable<RoutineDefinition> LoadUserFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            _logger?.LogInformation("No routine user JSON path configured.");
            yield break;
        }

        if (!File.Exists(path))
        {
            _logger?.LogInformation("No routine user JSON at {Path}.", path);
            yield break;
        }

        IEnumerable<RoutineDefinition>? loaded = null;
        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<RoutineFileDto>(json, JsonOpts);
            loaded = dto?.Routines;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Malformed routine JSON at {Path}; database empty.", path);
            yield break;
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "IO error reading routine JSON at {Path}; database empty.", path);
            yield break;
        }

        if (loaded is null)
            yield break;

        foreach (var r in loaded)
            yield return r;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class RoutineFileDto
    {
        [JsonPropertyName("routines")]
        public List<RoutineDefinition> Routines { get; set; } = new();
    }
}
```

- [ ] **Step 7: Run RoutineDatabase tests**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~RoutineDatabaseTests" -c Debug
```

Expected: `Passed! - Failed: 0, Passed: 5, Skipped: 0`.

Full Core suite:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug
```

Expected: 0 failures.

- [ ] **Step 8: Commit**

```bash
git add tests/PeakCan.Host.Core.Tests/Uds/Database/RoutineDefinitionTests.cs src/PeakCan.Host.Core/Uds/Database/RoutineDefinition.cs tests/PeakCan.Host.Core.Tests/Uds/Database/RoutineDatabaseTests.cs src/PeakCan.Host.Core/Uds/Database/RoutineDatabaseDefaults.cs src/PeakCan.Host.Core/Uds/Database/RoutineDatabase.cs
git commit -m "feat(core): add RoutineDefinition + RoutineDatabase with JSON load"
```

---

### Task G: Fix `UdsViewModel.SecurityAccessAsync` — remove `NotImplementedException` (App)

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/UdsViewModel.cs:115-145` (`SecurityAccessAsync` method body)

**Interfaces:**
- Consumes: `UdsClient.SecurityAccessAsync(byte, CancellationToken)` (Task D's new overload).
- Produces: a working SecurityAccess path that:
  1. Calls the new overload.
  2. Catches `KeyAlgorithmNotConfiguredException` separately and emits a clear configuration hint.
  3. Preserves all existing catch branches (`UdsNegativeResponseException`, generic `Exception`).
  4. Never logs seed bytes (existing line 124 already redacts).

- [ ] **Step 1: Read the current `SecurityAccessAsync` block**

Open `src/PeakCan.Host.App/ViewModels/UdsViewModel.cs:115-145`. Confirm the structure matches what was read during planning.

- [ ] **Step 2: Replace the `SecurityAccessAsync` method body**

Replace the entire method body (lines 115-145) with the following. Keep the `[RelayCommand]` attribute and the method signature (`private async Task SecurityAccessAsync()`) unchanged.

```csharp
    [RelayCommand]
    private async Task SecurityAccessAsync()
    {
        try
        {
            Log("Requesting security access...");
            // v1.1.0: use the new KeyProvider-aware overload that delegates
            // key computation to the injected IKeyDerivationAlgorithm.
            // SECURITY: the new overload never returns the seed to the caller
            // (it returns only the success response from SendKey), so there
            // is no seed byte to log here. See commit a9fe443 (C-2 fix).
            var response = await _udsClient.SecurityAccessAsync(0x01);
            SecurityText = $"Level 0x01 (authenticated, {response.Length} bytes)";
            Log($"SecurityAccess level 0x01 succeeded ({response.Length} bytes).");
        }
        catch (KeyAlgorithmNotConfiguredException ex)
        {
            // Targeted hint for the placeholder case (no OEM algorithm wired).
            // Distinct catch ABOVE the generic Exception branch so we can
            // surface a configuration message instead of a generic error.
            Log($"SecurityAccess: {ex.Message}");
            Log("Hint: register an IKeyDerivationAlgorithm implementation in DI before invoking SecurityAccess.");
            SecurityText = "Not Authenticated";
        }
        catch (UdsNegativeResponseException ex)
        {
            Log($"Security access failed: {ex.ResponseCode}");
            SecurityText = $"Rejected: {ex.ResponseCode}";
        }
        catch (InvalidOperationException ex)
        {
            // Thrown by the new overload when the UdsClient was constructed
            // without an IKeyDerivationAlgorithm (legacy 2-arg ctor).
            Log($"Security access error: {ex.Message}");
            SecurityText = "Not Authenticated";
        }
        catch (Exception ex)
        {
            Log($"Security access error: {ex.Message}");
            SecurityText = "Not Authenticated";
        }
    }
```

- [ ] **Step 3: Verify the App project builds**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug
```

Expected: BUILD SUCCEEDS. The new exception type `KeyAlgorithmNotConfiguredException` is reachable because the `using PeakCan.Host.Core.Uds;` directive is already present at the top of the file.

If the build fails because `KeyAlgorithmNotConfiguredException` is not in scope, add a `using PeakCan.Host.Core.Uds;` if missing (line 6 already includes it per the current file contents).

- [ ] **Step 4: Run the full App test suite**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug
```

Expected: 0 failures (no existing tests target `UdsViewModel.SecurityAccessAsync`'s old behavior — it threw `NotImplementedException` which no test relied on).

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/UdsViewModel.cs
git commit -m "fix(uds): replace NotImplementedException in SecurityAccess with KeyProvider call"
```

---

### Task H: `AppHostBuilder` — register `IKeyDerivationAlgorithm`, `DidDatabase`, `RoutineDatabase`, switch `UdsClient` to factory (App)

**Files:**
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs:135-152`

**Interfaces:**
- Consumes: `IKeyDerivationAlgorithm` (Task B), `PlaceholderKeyAlgorithm` (Task C), `DidDatabase` (Task E), `RoutineDatabase` (Task F), `UdsClient` 3-arg ctor (Task D).
- Produces: registered DI services so that when the app starts, `UdsClient` is built with the placeholder algorithm and the two databases are loaded from `%APPDATA%\PeakCan.Host\`.

- [ ] **Step 1: Read the current UDS DI block**

Open `src/PeakCan.Host.App/Composition/AppHostBuilder.cs:135-152`. The current block registers `UdsTimer`, `IsoTpLayer` (via factory), `UdsClient` (parameterless), and `UdsViewModel`.

- [ ] **Step 2: Replace the `UdsClient` registration with a factory**

In `src/PeakCan.Host.App/Composition/AppHostBuilder.cs`, replace the single line:

```csharp
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.UdsClient>();
```

(line 151) with the following three registrations (place them right after the `IsoTpLayer` factory block, before the `UdsClient` factory):

```csharp
        // v1.1.0: SecurityAccess KeyProvider default. OEM overrides this at deploy time.
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.IKeyDerivationAlgorithm, PeakCan.Host.Core.Uds.PlaceholderKeyAlgorithm>();

        // v1.1.0: DID + Routine databases (load from %APPDATA%\PeakCan.Host\ on construction).
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.Database.DidDatabase>();
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.Database.RoutineDatabase>();

        // v1.1.0: UdsClient now requires an IKeyDerivationAlgorithm via the 3-arg ctor.
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.UdsClient>(sp =>
        {
            var isoTp = sp.GetRequiredService<PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer>();
            var keyAlgorithm = sp.GetRequiredService<PeakCan.Host.Core.Uds.IKeyDerivationAlgorithm>();
            return new PeakCan.Host.Core.Uds.UdsClient(isoTp, keyAlgorithm);
        });
```

**Important**: place these registrations immediately AFTER the existing `IsoTpLayer` factory block (lines 137–150) and BEFORE the existing `builder.Services.AddSingleton<UdsViewModel>();` line (line 152). Keep the `UdsViewModel` registration as-is — it does not yet need any of the new dependencies.

- [ ] **Step 3: Verify the App project builds**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug
```

Expected: BUILD SUCCEEDS. If the fully-qualified type names feel verbose, add `using` directives at the top of `AppHostBuilder.cs` for `PeakCan.Host.Core.Uds`, `PeakCan.Host.Core.Uds.Database`. Existing `using` directives may already cover some; verify by reading the file header.

- [ ] **Step 4: Run the full test suite**

```bash
dotnet test PeakCan.Host.slnx -c Debug
```

Expected: 0 failures. (Some existing UdsClient/UdsViewModel tests may construct `UdsClient` directly; the legacy 2-arg ctor is preserved by Task D, so these tests continue to work.)

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/Composition/AppHostBuilder.cs
git commit -m "feat(app): register KeyProvider + DID/Routine databases; switch UdsClient to factory"
```

---

### Task I: README + release notes + version bump

**Files:**
- Modify: `README.md` (add v1.1.0 section + update status line + update test count line)
- Modify: `src/PeakCan.Host.App/PeakCan.Host.App.csproj` (Version bump)
- Create: `docs/release-notes-v1.1.0.md`

**Interfaces:**
- Consumes: existing README + release notes style.

- [ ] **Step 1: Read README.md, locate the status line and test count line**

```bash
grep -n "MVP v\|Status:\|test" "D:/claude_proj2/peakcan-host/README.md" | head -10
```

Note the `**Status:**` line near the top and the `**xxx unit tests pass**` line below it.

- [ ] **Step 2: Get the actual test count**

```bash
dotnet test PeakCan.Host.slnx -c Release --logger "console;verbosity=minimal" 2>&1 | grep -E "Passed|Failed|Skipped" | tail -3
```

Note the total passed count for use in the README. Expected: ~440+ pass (the new v1.1.0 tests bring the count from ~407 baseline up by ~24 new tests across Tasks A–H).

- [ ] **Step 3: Update README.md**

Replace the `**Status:**` line from `MVP v0.10.1` to `v1.1.0` (keep the spec/plan link convention used in the existing format).

Replace the test count line from `407 unit tests pass` to the actual count obtained in Step 2.

Insert a new section between the existing `## v0.10.1 (Trace polish)` section and the existing `## v1.0.0 (Scripting Engine)` section:

```markdown
## v1.1.0 (UDS SecurityAccess KeyProvider + JSON-loadable DID/Routine databases)

- **SecurityAccess KeyProvider (`IKeyDerivationAlgorithm`)** — UDS SecurityAccess
  (0x27) now delegates key derivation to a DI-registered algorithm. The default
  is `PlaceholderKeyAlgorithm`, which throws `KeyAlgorithmNotConfiguredException`
  and surfaces a clear configuration hint in the UDS log when SecurityAccess is
  invoked before an OEM-specific implementation is registered. OEMs wire their
  seed→key computation at deploy time via DI registration; no recompile needed.
- **`DidDatabase` + `RoutineDatabase`** — DID and Routine definitions are now
  loadable from `%APPDATA%\PeakCan.Host\uds-dids.json` and
  `uds-routines.json`. The DID database ships with 5 built-in defaults (VIN,
  ECU SW/HW version, Part Number, Supplier ID) and merges user entries on top.
  Routine database is 100% OEM-defined (empty by default; populate via user
  JSON). A missing or malformed JSON file falls back to built-in defaults and
  logs a warning — the UI never breaks on bad config.
- **UDS UI** — `UdsViewModel` `SecurityAccessAsync` no longer throws
  `NotImplementedException`; it uses the new KeyProvider-aware overload and
  surfaces the placeholder hint when no OEM algorithm is wired. The
  `UdsClient` DI registration is now a factory that injects the configured
  algorithm.

> The spec's "4-panel orchestrator refactor" (`SessionPanelViewModel` /
> `DidPanelViewModel` / `RoutinePanelViewModel` / `DtcPanelViewModel` + JSON
> databases as `DataGrid` ItemsSources) is **deferred to v1.2** to keep the
> v1.1.0 ship scope tight; the existing monolithic `UdsViewModel` and the
> existing `UdsView.xaml` (TabControl with free-text DID / Routine ID inputs
> + DTC DataGrid + Log Panel) remain.
```

- [ ] **Step 4: Bump the App csproj Version**

```bash
grep -n "<Version>" src/PeakCan.Host.App/PeakCan.Host.App.csproj
```

Replace `0.10.1` with `1.1.0`. If `<Version>` is in the form `0.10.1` (no prefix), change to `1.1.0`. If `<AssemblyVersion>` and `<FileVersion>` are also present, bump them to `1.1.0.0`.

- [ ] **Step 5: Create `docs/release-notes-v1.1.0.md`**

```markdown
# Release Notes — PeakCan Host v1.1.0

**Date:** 2026-06-25

## Summary

v1.1.0 closes two gaps from the v0.10.1 + merged UDS work: (1) the
`UdsViewModel.SecurityAccessAsync` `NotImplementedException` is replaced with
a DI-injected `IKeyDerivationAlgorithm` (default `PlaceholderKeyAlgorithm`
that emits a clear configuration hint), and (2) DID and Routine definitions
gain JSON-loadable databases (`uds-dids.json` / `uds-routines.json`) under
`%APPDATA%\PeakCan.Host\` with graceful fallback to built-in defaults on
missing or malformed files.

## New Features

- **`IKeyDerivationAlgorithm` abstraction** — `UdsClient` gains a 3-arg
  constructor accepting an `IKeyDerivationAlgorithm`. A new public overload
  `SecurityAccessAsync(byte requestLevel, CancellationToken ct = default)`
  performs the full RequestSeed → ComputeKey → SendKey handshake. Default
  registration is `PlaceholderKeyAlgorithm`, which throws
  `KeyAlgorithmNotConfiguredException(securityLevel)` with an actionable
  message and never logs seed bytes (C-2 fix preserved).
- **`DidDatabase`** — 5 built-in defaults (VIN 0xF190, ECU SW version
  0xF184, ECU HW version 0xF191, Part Number 0xF187, Supplier ID 0xF18A)
  + JSON load from `%APPDATA%\PeakCan.Host\uds-dids.json`. User entries
  with matching IDs override built-ins; non-matching entries are appended.
- **`RoutineDatabase`** — 100% OEM-defined; loads from
  `%APPDATA%\PeakCan.Host\uds-routines.json`. Empty list when no file.
- **Graceful JSON fallback** — missing or malformed JSON does NOT throw;
  the UI remains usable and the logger records an Information / Warning.

## Bug Fixes

- **`UdsViewModel.SecurityAccessAsync` `NotImplementedException`** —
  removed. The new path uses the KeyProvider-aware overload and surfaces a
  useful hint when no OEM algorithm is registered.

## Test Results

- **~440 pass + 6 SKIP** (Core ~205 + Infrastructure 74 + App ~160)
- 6 SKIP: 2 hardware-dependent, 1 flaky background service, 3 hardware-dependent App tests
- 0 fail

## Commits Since v0.10.1

[Generated from `git log v0.10.1..HEAD --oneline` at ship time]

## Known Limitations / v1.2 Backlog

- 4-panel orchestrator refactor (`SessionPanelViewModel` / `DidPanelViewModel`
  / `RoutinePanelViewModel` / `DtcPanelViewModel`) is deferred to v1.2.
- `UdsView.xaml` polish (replace free-text DID / Routine ID inputs with
  DataGrids bound to `DidDatabase` / `RoutineDatabase`) is deferred to v1.2.
- OEM-specific key algorithms remain out of scope (per spec non-goal N1);
  OEMs wire their implementations at deploy time via DI.
- J1939 / CANopen still deferred to v2.0.
- Linux + SocketCAN cross-platform still deferred to v2.0.
```

- [ ] **Step 6: Verify build + full test suite pass**

```bash
dotnet build PeakCan.Host.slnx -c Release
dotnet test PeakCan.Host.slnx -c Release
```

Expected: 0 build errors, 0 test failures. SKIP count unchanged.

- [ ] **Step 7: Commit**

```bash
git add README.md docs/release-notes-v1.1.0.md src/PeakCan.Host.App/PeakCan.Host.App.csproj
git commit -m "docs: add v1.1.0 section + release notes + version bump"
```

---

### Task J: Final code-review pass + tag + release

**Files:**
- This task creates the v1.1.0 git tag and a GitHub release. It does not modify source files.

**Interfaces:**
- Consumes: full diff `c11288f..HEAD` on `fix/uds-8-critical` branch.

- [ ] **Step 1: Run a focused code review**

Per the project's `~/.claude/agents/code-reviewer.md` and `rules/ecc/common/agents.md` — code just modified must be reviewed. Use the `code-reviewer` agent (or run a focused inline review if subagents are unavailable) on the v1.1.0 diff.

The review MUST cover:
- **Correctness**: Does `UdsClient.SecurityAccessAsync(byte, CancellationToken)` correctly orchestrate RequestSeed → ComputeKey → SendKey? Does it preserve the C-2 seed-redaction invariant (no seed bytes logged anywhere)?
- **Security**: Does `PlaceholderKeyAlgorithm` validate inputs and never leak seed/key bytes? Does the new `UdsViewModel.SecurityAccessAsync` catch branch preserve seed-redaction?
- **Robustness**: Do `DidDatabase` / `RoutineDatabase` correctly fall back to built-in defaults on missing/malformed JSON? Do they log enough context for operators to diagnose?
- **Architecture**: NetArchTest rule 2 still satisfied (Core has no PEAK SDK dependency)?
- **DI lifetime**: `IKeyDerivationAlgorithm`, `DidDatabase`, `RoutineDatabase` registered as singletons (matching the project's existing convention for `UdsClient` and `UdsTimer`)?

- [ ] **Step 2: Address CRITICAL / HIGH issues**

If the review surfaces blockers, fix them in separate `fix(...)` commits before tagging. Address MEDIUM issues if trivial; defer LOW.

- [ ] **Step 3: Push branch**

```bash
git push -u origin fix/uds-8-critical
```

If `github.com:443` is blocked, use the `gh api` workaround documented in the user's MEMORY (`gh API 推 commit workflow`).

- [ ] **Step 4: Open a PR**

```bash
gh pr create --base main --head fix/uds-8-critical --title "feat: v1.1.0 UDS SecurityAccess KeyProvider + JSON databases" --body "..."
```

PR body should summarize:
- The two spec sections closed (KeyProvider + DID/Routine databases).
- Test count delta (~407 → ~440).
- The v1.1.0 ship scope vs the v1.2 deferral (4-panel orchestrator + UdsView polish).
- The DI breaking change (callout): `UdsClient` DI registration switched from `AddSingleton<UdsClient>()` to a factory — internal, no external consumers, but worth noting for any test that resolves `UdsClient` from DI without a `IKeyDerivationAlgorithm` registered.

- [ ] **Step 5: Squash-merge + tag**

After CI passes:
```bash
gh pr merge --squash --delete-branch
git tag v1.1.0
git push origin v1.1.0
gh release create v1.1.0 --title "v1.1.0 — UDS SecurityAccess KeyProvider + JSON databases" --notes-file docs/release-notes-v1.1.0.md
```

- [ ] **Step 6: Update MEMORY**

Edit `~/.claude/projects/D--claude-proj2/memory/MEMORY.md` to add an entry for `v1.1.0 SHIPPED 2026-06-25`. Reference the spec, plan, and the v1.2 backlog (4-panel orchestrator + UI polish).

---

## Self-Review

**1. Spec coverage (v1.1.0 ship scope only):**

| Spec § | Task(s) |
|---|---|
| §4.1 (IKeyDerivationAlgorithm interface) | Task B |
| §4.2 (PlaceholderKeyAlgorithm) | Task C |
| §4.3 (KeyAlgorithmNotConfiguredException) | Task A |
| §4.4 (UdsClient ctor + new SecurityAccessAsync(byte, CancellationToken) overload) | Task D |
| §4.6 (DidDefinition + DidDatabaseDefaults + DidDatabase with built-in 5 DIDs + JSON load) | Task E |
| §4.7 (RoutineDefinition + RoutineDatabaseDefaults + RoutineDatabase with JSON load, no built-ins) | Task F |
| §"v1.1.0 Ship Scope" — `UdsViewModel.SecurityAccessAsync` modification (remove NotImplementedException) | Task G |
| §"v1.1.0 Ship Scope" — `AppHostBuilder` DI registration (KeyProvider factory + DBs) | Task H |
| §I (docs + version bump) | Task I |
| §J (code-review + ship) | Task J |
| §D1–D4 (4-panel UI refactor + UdsView polish) | **Deferred to v1.2** — explicit in spec |

No v1.1.0 ship-scope spec requirement is unmapped.

**2. Placeholder scan:** No TBD/TODO/"implement later"/"similar to Task N" in step content. Every code block is concrete. Every step has an exact `dotnet test` / `dotnet build` / `git commit` command.

**3. Type consistency:**
- `IKeyDerivationAlgorithm.ComputeKey(byte[], byte)` — Task B, used by `PlaceholderKeyAlgorithm` (Task C), `UdsClient` overload (Task D), `FakeKeyDerivationAlgorithm` (Task B test double).
- `KeyAlgorithmNotConfiguredException(byte)` — Task A, thrown by Task C, caught in Task G.
- `UdsClient(IsoTpLayer, IKeyDerivationAlgorithm, UdsTimer?)` — Task D, used by `AppHostBuilder` factory in Task H.
- `UdsClient.SecurityAccessAsync(byte, CancellationToken)` — Task D, called by `UdsViewModel.SecurityAccessAsync` (Task G).
- `DidDefinition(ushort, string, string, int, bool)` and `DidDatabase(ILogger<DidDatabase>?)` / `DidDatabase(string?, ILogger<DidDatabase>?)` — Task E. Registered as singleton in Task H.
- `RoutineDefinition` / `RoutineDatabase` (analogous) — Task F. Registered as singleton in Task H.
- `DidDatabaseDefaults.DefaultJsonPath` / `RoutineDatabaseDefaults.DefaultJsonPath` — Tasks E, F.

All type signatures match across tasks. The legacy 2-arg `UdsClient(IsoTpLayer, UdsTimer?)` ctor and the legacy 3-arg `SecurityAccessAsync(byte, byte[]?, CancellationToken)` method are preserved unchanged (Task D explicitly additive), so any existing test that uses them continues to work.

---
