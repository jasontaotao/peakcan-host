# W37 Implementation Plan — AscLocator god-class refactor (22nd overall)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Reduce `AscLocator.cs` from 225 LoC to ~102 LoC main + 3 NEW partials (`LoggingFlow.partial.cs` + `SearchDirsFlow.partial.cs` + `LocateFlow.partial.cs`). Public API + tests + DI unchanged.

**Architecture:** Subdirectory-partials pattern (sister of W34 DbcSendViewModel + W36 StatsViewModel). 5 `[LoggerMessage]` partials → `LoggingFlow.partial.cs` (14 LoC). Cache + load + default path → `SearchDirsFlow.partial.cs` (30 LoC). Walk + Recurse → `LocateFlow.partial.cs` (79 LoC). Main retains `IAscLocator` interface + class declaration + 1 const + 1 property + 4 readonly fields + 2 ctors + `LocateAsync` public entry.

**Tech Stack:** C# .NET 10 / WPF / Microsoft.Extensions.Logging / FluentAssertions / NSubstitute / xUnit

## Global Constraints

来自 `docs/superpowers/specs/2026-07-17-w37-asc-locator-god-class-refactor.md` + W20/W23 sister LESSONs：

- Public API 100% 保留（`IAscLocator` interface + `FileSystemAscLocator` class + `MaxSearchDepth` const + `SearchDirsPath` property + 2 ctors + `LocateAsync`）
- 测试不变（`FileSystemAscLocatorTests` 全部保留 + PASS）
- W20 LESSON: re-grep boundaries BEFORE each deletion script run; verbatim re-extraction from HEAD
- W23 LESSON: verify `IAscContentHasher` interface (sister of T8 in v3.52.0) preserved; verify `IAscLocator` signature unchanged
- W19 R1 LESSON: verbatim re-extraction; first-attempt PASS target
- CA1861 sister pattern: extract `private static readonly` if test triggers it
- 不引入新 public/internal API; 不改 tests 文件; 不改 DI 注册
- v3.54.0 ship files (DeepSeekProvider + subdirectory partials) 不动
- v3.52.0 + v3.52.1 + v3.53.0 + v3.53.1 ship files 不动
- pkm-capture 节流：仅在 ship-completion 时 dispatch

## File Structure

修改 + 新增：

| 文件 | LoC | 改动 |
|---|---|---|
| `src/PeakCan.Host.Core/Services/AscLocator.cs` | 225 → ~102 | -123 LoC (Logging + SearchDirs + Locate 移到 partials) |
| `src/PeakCan.Host.Core/Services/AscLocator/LoggingFlow.partial.cs` | ~14 | NEW (5 [LoggerMessage] partials) |
| `src/PeakCan.Host.Core/Services/AscLocator/SearchDirsFlow.partial.cs` | ~30 | NEW (cache + load + default path) |
| `src/PeakCan.Host.Core/Services/AscLocator/LocateFlow.partial.cs` | ~79 | NEW (Walk + Recurse) |

净增 0 LoC (重新分配)。Subdirectory pattern: `AscLocator/` 包含 3 partials（sister of W34 DbcSendViewModel/ 3 partials + W36 StatsViewModel/ 2 partials — **1st 3-partial subdirectory in Core layer**）。

---

### Task 0: Branch + spec verify + baseline

**Files:**
- Branch from main at current HEAD `d42adbb`
- Verify spec at `docs/superpowers/specs/2026-07-17-w37-asc-locator-god-class-refactor.md`

- [ ] **Step 1: Verify spec commit exists**

```bash
git log --oneline -3
git show --stat d42adbb
```
Expected: commit `d42adbb` is visible with spec file +138 insertions.

- [ ] **Step 2: Create feature branch**

```bash
git checkout -b feature/w37-asc-locator-god-class main
```

- [ ] **Step 3: Verify baseline build is green**

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
```
Expected: 0 errors.

- [ ] **Step 4: Verify baseline test is green (sequential to avoid pre-existing parallel flakes)**

```bash
dotnet test PeakCan.Host.slnx --no-build --nologo -c Debug --logger "console;verbosity=minimal" -- xUnit.MaxParallelThreads=1
```
Expected: 1456 PASS / 0 FAIL / 5 SKIP (v3.54.0 ship baseline).

- [ ] **Step 5: Commit plan**

```bash
git add docs/superpowers/plans/2026-07-17-w37-asc-locator-god-class-refactor.md
git commit -m "W37 plan: AscLocator god-class refactor (3 NEW partials + main -55% LoC; 4 micro-tasks; TDD)"
```

---

### Task 1: LoggingFlow.partial.cs extraction (5 [LoggerMessage] partials)

**Files:**
- Create: `src/PeakCan.Host.Core/Services/AscLocator/LoggingFlow.partial.cs`
- Modify: `src/PeakCan.Host.Core/Services/AscLocator.cs` (remove L212-225)

**Interfaces:**
- Consumes: existing `_logger` (private readonly field stays in main)
- Produces: 5 `[LoggerMessage]` partial methods in LoggingFlow.partial.cs

- [ ] **Step 1: Read current Logging block verbatim**

```bash
git show HEAD:src/PeakCan.Host.Core/Services/AscLocator.cs | sed -n '212,225p'
```
Expected: 5 `[LoggerMessage]` partial methods (~14 LoC).

- [ ] **Step 2: Create LoggingFlow.partial.cs with verbatim methods**

Create `src/PeakCan.Host.Core/Services/AscLocator/LoggingFlow.partial.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Services;

/// <summary>W37 god-class refactor (22nd overall): 5 [LoggerMessage] partials
/// extracted from main. Sister of W34 DbcSendViewModel/ subdirectory pattern
/// (D1: 3 partials). Each partial method logs one specific scenario
/// (max-depth hit / enumerate failed / hash failed / found / config corrupt).</summary>
public sealed partial class FileSystemAscLocator
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "AscLocator max-depth {Depth} hit at {Dir}; deeper subtrees skipped")]
    private static partial void LogMaxDepthHit(ILogger logger, string dir, int depth);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AscLocator enumerate failed for {Dir}")]
    private static partial void LogEnumerateFailed(ILogger logger, Exception ex, string dir);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AscLocator hash failed for {File}")]
    private static partial void LogHashFailed(ILogger logger, Exception ex, string file);

    [LoggerMessage(Level = LogLevel.Information, Message = "AscLocator found relocated .asc at {File}")]
    private static partial void LogFound(ILogger logger, string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AscLocator search-dirs config unreadable: {Path}")]
    private static partial void LogConfigCorrupt(ILogger logger, Exception ex, string path);
}
```

The implementer MUST use the **verbatim** partial method declarations from Step 1.

- [ ] **Step 3: Delete Logging block from main file (keep class declaration + field declarations)**

Modify `src/PeakCan.Host.Core/Services/AscLocator.cs`:
- Delete lines L212-225 (5 [LoggerMessage] partial method bodies)
- File should now be ~211 LoC

W19 R1 LESSON: **re-grep the boundaries BEFORE running any deletion script.** Use `git show HEAD:src/...cs | wc -l` to confirm range.

W23 LESSON: After deletion, build immediately to catch any signature drift.

- [ ] **Step 4: Build verify**

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
```
Expected: 0 errors.

If errors: most likely `using Microsoft.Extensions.Logging;` is now orphaned in main (if no other `LogLevel` reference) — remove it. Or the partial method signatures drifted — compare verbatim.

- [ ] **Step 5: Test verify (no regression)**

```bash
dotnet test PeakCan.Host.Core.Tests/PeakCan.Host.Core.csproj --no-build --nologo -c Debug --filter "FullyQualifiedName~AscLocator|FullyQualifiedName~FileSystemAscLocator" --logger "console;verbosity=minimal"
```
Expected: existing tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Core/Services/AscLocator.cs \
        src/PeakCan.Host.Core/Services/AscLocator/LoggingFlow.partial.cs
git commit -m "W37 T1: LoggingFlow.partial.cs — 5 [LoggerMessage] partials extracted (-14 LoC main)"
```

---

### Task 2: SearchDirsFlow.partial.cs extraction (cache + load + default path)

**Files:**
- Create: `src/PeakCan.Host.Core/Services/AscLocator/SearchDirsFlow.partial.cs`
- Modify: `src/PeakCan.Host.Core/Services/AscLocator.cs` (remove L178-210)

**Interfaces:**
- Consumes: existing `SearchDirsPath` (property stays in main), `_cacheGate` + `_cachedDirs` (private readonly fields stay in main)
- Produces: `GetSearchDirs` + `LoadSearchDirsFromDisk` + `DefaultSearchDirsPath` in SearchDirsFlow.partial.cs

- [ ] **Step 1: Read current SearchDirs block verbatim**

```bash
git show HEAD:src/PeakCan.Host.Core/Services/AscLocator.cs | sed -n '178,210p'
```
Expected: `GetSearchDirs` (~9 LoC) + `LoadSearchDirsFromDisk` (~16 LoC) + `DefaultSearchDirsPath` (~5 LoC) = ~30 LoC.

- [ ] **Step 2: Create SearchDirsFlow.partial.cs with verbatim methods**

Create `src/PeakCan.Host.Core/Services/AscLocator/SearchDirsFlow.partial.cs`:

```csharp
using System.Text.Json;

namespace PeakCan.Host.Core.Services;

/// <summary>W37 god-class refactor (22nd overall): search-dirs cache +
/// load + default path extracted from main. Sister of W34 DbcSendViewModel/
/// subdirectory pattern. The 3 methods form a coupled responsibility:
/// GetSearchDirs (lazy-init with double-checked locking) delegates to
/// LoadSearchDirsFromDisk (reads %APPDATA%/PeakCan.Host/asc-search-dirs.json)
/// which uses DefaultSearchDirsPath for the fallback path. Keeping all 3
/// in the same partial avoids cross-partial state exposure.</summary>
public sealed partial class FileSystemAscLocator
{
    /// <summary>Double-checked locking lazy init. Returns the cached
    /// list on subsequent calls; loads from disk on first access.
    /// Sister of sister pattern in v3.6.4 PATCH.</summary>
    private List<string> GetSearchDirs()
    {
        if (_cachedDirs is not null) return _cachedDirs;
        lock (_cacheGate)
        {
            if (_cachedDirs is not null) return _cachedDirs;
            _cachedDirs = LoadSearchDirsFromDisk();
            return _cachedDirs;
        }
    }

    /// <summary>Reads JSON array of absolute directory paths from
    /// <see cref="SearchDirsPath"/>. Missing or corrupt file → empty
    /// list (locator returns null → caller falls back to path-only
    /// resolution).</summary>
    private List<string> LoadSearchDirsFromDisk()
    {
        try
        {
            if (!File.Exists(SearchDirsPath)) return new List<string>();
            var json = File.ReadAllText(SearchDirsPath);
            if (string.IsNullOrWhiteSpace(json)) return new List<string>();
            var dirs = JsonSerializer.Deserialize<List<string>>(json);
            return dirs ?? new List<string>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LogConfigCorrupt(_logger, ex, SearchDirsPath);
            return new List<string>();
        }
    }

    /// <summary>Default path to the user-known search dirs JSON file.
    /// <c>%APPDATA%/PeakCan.Host/asc-search-dirs.json</c> per v3.6.4 PATCH.</summary>
    private static string DefaultSearchDirsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return System.IO.Path.Combine(appData, "PeakCan.Host", "asc-search-dirs.json");
    }
}
```

The implementer MUST use the **verbatim** method bodies from Step 1.

- [ ] **Step 3: Delete SearchDirs block from main file**

Modify `src/PeakCan.Host.Core/Services/AscLocator.cs`:
- Delete lines L178-210 (`GetSearchDirs` + `LoadSearchDirsFromDisk` + `DefaultSearchDirsPath`)
- File should now be ~181 LoC

W19 R1 LESSON: re-grep boundaries. W23 LESSON: build immediately after.

- [ ] **Step 4: Build verify**

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
```
Expected: 0 errors.

If error: most likely `using System.Text.Json;` is now orphaned in main (if no other JSON reference) — remove it.

- [ ] **Step 5: Test verify (no regression)**

```bash
dotnet test PeakCan.Host.Core.Tests/PeakCan.Host.Core.csproj --no-build --nologo -c Debug --filter "FullyQualifiedName~AscLocator|FullyQualifiedName~FileSystemAscLocator" --logger "console;verbosity=minimal"
```
Expected: existing tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Core/Services/AscLocator.cs \
        src/PeakCan.Host.Core/Services/AscLocator/SearchDirsFlow.partial.cs
git commit -m "W37 T2: SearchDirsFlow.partial.cs — cache + load + default path extracted (-30 LoC main)"
```

---

### Task 3: LocateFlow.partial.cs extraction (Walk + Recurse)

**Files:**
- Create: `src/PeakCan.Host.Core/Services/AscLocator/LocateFlow.partial.cs`
- Modify: `src/PeakCan.Host.Core/Services/AscLocator.cs` (remove L98-176)

**Interfaces:**
- Consumes: existing `_hasher` (private readonly field stays in main), `_logger` (stays in main), `MaxSearchDepth` const (stays in main)
- Produces: `WalkAsync` (recursive walk, ~64 LoC) + `RecurseAsync` (~13 LoC) in LocateFlow.partial.cs

- [ ] **Step 1: Read current Walk + Recurse block verbatim**

```bash
git show HEAD:src/PeakCan.Host.Core/Services/AscLocator.cs | sed -n '98,176p'
```
Expected: `WalkAsync` (~64 LoC) + `RecurseAsync` (~13 LoC) = ~77 LoC.

- [ ] **Step 2: Create LocateFlow.partial.cs with verbatim methods**

Create `src/PeakCan.Host.Core/Services/AscLocator/LocateFlow.partial.cs`:

```csharp
using System.IO;

namespace PeakCan.Host.Core.Services;

/// <summary>W37 god-class refactor (22nd overall): Walk + Recurse
/// extracted from main. Sister of W34 DbcSendViewModel/SendFlow.partial.cs
/// pattern. The 2 methods are tightly coupled: RecurseAsync delegates
/// back to WalkAsync for each subdir. Keeping them in the same partial
/// avoids cross-partial state exposure.</summary>
public sealed partial class FileSystemAscLocator
{
    /// <summary>Recursive walk. Returns the first matching file's
    /// path, or <c>null</c> if nothing in this subtree matches.</summary>
    private async Task<string?> WalkAsync(
        string dir,
        int depth,
        string contentHash,
        CancellationToken ct)
    {
        if (depth >= MaxSearchDepth)
        {
            LogMaxDepthHit(_logger, dir, depth);
            return null;
        }
        // Match files at THIS level before recursing — keeps the search
        // hit-locality high (a moved .asc is usually still at a similar
        // tree depth).
        IEnumerable<string> files;
        IEnumerable<string> subdirs;
        try
        {
            files = Directory.EnumerateFiles(dir, "*.asc", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Per-directory ACLs / transient errors: log + skip rather
            // than abort the entire search. The other configured roots
            // may still contain the recording.
            LogEnumerateFailed(_logger, ex, dir);
            files = Array.Empty<string>();
            subdirs = Array.Empty<string>();
            return await RecurseAsync(subdirs, depth, contentHash, ct).ConfigureAwait(false);
        }
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) return null;
            // Extension check is case-insensitive on Windows (NTFS)
            // but we double-check to be safe on case-sensitive filesystems.
            if (!file.EndsWith(".asc", StringComparison.OrdinalIgnoreCase)) continue;
            string? hash = null;
            try
            {
                hash = await _hasher.ComputeAsync(file, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                LogHashFailed(_logger, ex, file);
                continue;
            }
            if (string.Equals(hash, contentHash, StringComparison.OrdinalIgnoreCase))
            {
                LogFound(_logger, file);
                return file;
            }
        }
        try
        {
            subdirs = Directory.EnumerateDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LogEnumerateFailed(_logger, ex, dir);
            return null;
        }
        return await RecurseAsync(subdirs, depth, contentHash, ct).ConfigureAwait(false);
    }

    /// <summary>Iterate subdirs, recurse into each via WalkAsync.</summary>
    private async Task<string?> RecurseAsync(
        IEnumerable<string> subdirs,
        int depth,
        string contentHash,
        CancellationToken ct)
    {
        foreach (var sub in subdirs)
        {
            if (ct.IsCancellationRequested) return null;
            var found = await WalkAsync(sub, depth + 1, contentHash, ct).ConfigureAwait(false);
            if (found is not null) return found;
        }
        return null;
    }
}
```

The implementer MUST use the **verbatim** method bodies from Step 1.

- [ ] **Step 3: Delete Walk + Recurse block from main file**

Modify `src/PeakCan.Host.Core/Services/AscLocator.cs`:
- Delete lines L98-176 (`WalkAsync` + `RecurseAsync`)
- File should now be ~102 LoC

W19 R1 LESSON: re-grep boundaries. W23 LESSON: build immediately after.

- [ ] **Step 4: Build verify**

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
```
Expected: 0 errors.

If error: most likely `using System.IO;` is now orphaned in main (if no other System.IO reference) — verify and remove.

- [ ] **Step 5: Test verify (no regression)**

```bash
dotnet test PeakCan.Host.Core.Tests/PeakCan.Host.Core.csproj --no-build --nologo -c Debug --filter "FullyQualifiedName~AscLocator|FullyQualifiedName~FileSystemAscLocator" --logger "console;verbosity=minimal"
```
Expected: existing tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Core/Services/AscLocator.cs \
        src/PeakCan.Host.Core/Services/AscLocator/LocateFlow.partial.cs
git commit -m "W37 T3: LocateFlow.partial.cs — Walk + Recurse extracted (-77 LoC main; total 225→~102 LoC)"
```

---

### Task 4: Full solution CI + coverage check

- [ ] **Step 1: Full solution build**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore
```
Expected: 0 errors.

- [ ] **Step 2: Full solution test (sequential to avoid pre-existing parallel flakes)**

```bash
dotnet test PeakCan.Host.slnx --no-build --nologo -c Debug --logger "console;verbosity=minimal" -- xUnit.MaxParallelThreads=1
```
Expected: 1456 PASS / 0 FAIL / 5 SKIP (matches v3.54.0 ship baseline; refactor PATCH zero test count change).

- [ ] **Step 3: Verify LoC delta**

```bash
wc -l src/PeakCan.Host.Core/Services/AscLocator.cs \
      src/PeakCan.Host.Core/Services/AscLocator/LoggingFlow.partial.cs \
      src/PeakCan.Host.Core/Services/AscLocator/SearchDirsFlow.partial.cs \
      src/PeakCan.Host.Core/Services/AscLocator/LocateFlow.partial.cs
```
Expected: main ≤ 110 LoC; subdirectory 3 partials totaling ~115 LoC; cumulative ≈ 225 LoC (no net change).

---

### Task 5: Release notes + tier-3 ship

**Files:**
- Create: `docs/release-notes-v3-55-0-minor.md`
- Modify: `src/Directory.Build.props` (version bump 3.54.0 → 3.55.0)
- Push + PR + squash + tag + GH release

- [ ] **Step 1: Write release notes**

Create `docs/release-notes-v3-55-0-minor.md` covering:
- W37 god-class refactor summary
- 3 NEW partials (LoggingFlow + SearchDirsFlow + LocateFlow)
- Main file 225 → ~102 LoC (-55%)
- Public API + tests + DI unchanged
- 6 NEW 1/3 lesson candidates
- Sister of W3-W36 series (22nd god-class SHIP)
- 1st 3-partial subdirectory in Core layer (sister of W34 App 3-partials)

- [ ] **Step 2: Bump version**

In `src/Directory.Build.props`:
```xml
<Version>3.55.0</Version>
<AssemblyVersion>3.55.0.0</AssemblyVersion>
<FileVersion>3.55.0.0</FileVersion>
<InformationalVersion>3.55.0</InformationalVersion>
```

- [ ] **Step 3: Tier-3 ship**

```bash
git add docs/release-notes-v3-55-0-minor.md src/Directory.Build.props
git commit -m "v3.55.0: version bump + release notes (AscLocator god-class refactor; 22nd)"
git push -u origin feature/w37-asc-locator-god-class
gh pr create --base main --title "v3.55.0 MINOR: AscLocator god-class refactor (22nd overall; 3 NEW partials)" --body-file docs/release-notes-v3-55-0-minor.md
gh pr merge --squash --delete-branch
git reset --hard origin/main  # force-correct per sister pattern (W35/v3.52.0/v3.52.1/v3.53.0/v3.53.1/v3.54.0)
git tag -a v3.55.0 -m "v3.55.0 MINOR — AscLocator god-class refactor (22nd overall)"
git push origin v3.55.0
gh release create v3.55.0 --notes-file docs/release-notes-v3-55-0-minor.md
```

---

## Sister-lesson candidates to monitor

| Lesson | Status | What T1-T3 might observe |
|---|---|---|
| `largest-method-can-move-when-flow-is-discrete-walk-not-orchestrator` | NEW 1/3 (W37) | T3: WalkAsync (~64 LoC) 整段移到 LocateFlow (sister of W36 D4 ctor variant) |
| `recursive-walk-must-stay-coupled-with-recurse-helper-in-same-partial` | NEW 1/3 (W37) | T3: WalkAsync + RecurseAsync 同 partial (递归相互调用) |
| `cached-search-dirs-must-stay-coupled-with-load-and-default-path-helpers-in-same-partial` | NEW 1/3 (W37) | T2: cache 状态 + 加载 + 默认路径同 partial |
| `logger-message-partials-can-be-extracted-to-isolated-logging-partial-when-class-has-5-plus-log-calls` | NEW 1/3 (W37) | T1: 5 [LoggerMessage] partial → LoggingFlow 单独 partial |
| `3-partial-subdirectory-pattern-empirical-w34-w37` | NEW 1/3 (W37) | T1-T3: W34 DbcSendViewModel 3 + W37 AscLocator 3 (1st Core layer 3-partial) |
| `cache-gate-locks-once-and-only-on-first-read-pattern-emerges-when-class-lazy-loads-from-disk` | NEW 1/3 (W37) | T2: double-checked locking in GetSearchDirs |

## Verification

- `dotnet build PeakCan.Host.slnx`: 0 errors
- `dotnet test PeakCan.Host.slnx`: 1456 PASS / 0 FAIL / 5 SKIP
- `wc -l src/.../AscLocator.cs`: ≤ 110 LoC
- 3 NEW partials exist in `AscLocator/` subdirectory
- Public API surface unchanged (IAscLocator + FileSystemAscLocator + MaxSearchDepth + SearchDirsPath + 2 ctors + LocateAsync)
- DI registration unchanged
- All `FileSystemAscLocatorTests` tests pass without modification
- Tag v3.55.0 + GH release published

## Out of scope (YAGNI)

- W38+ god-class refactor (DbcTokenizer / DbcSendViewModel / RecordService) — separate work
- P2 PATCH: API Key UI — separate work
- `IAscLocator` interface 移 separate file — not needed; partial 拆分只针对 class
- New tests (refactor PATCH should have zero test change)
- `IAscContentHasher` refactor — separate work if needed