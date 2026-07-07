# Release Notes v3.10.0 — Multi-Finding Cleanups (MINOR)

**Released:** 2026-07-07
**Parent:** v3.9.2 PATCH (`75ae11b`)
**Tag:** v3.10.0
**Branch:** `feature/v3-10-0-minor`
**Source:** 38-finding multi-agent review at `.review/00-integrated-findings.md`

## Highlights

This MINOR closes **4 review findings** from the v3.9.2 PATCH multi-agent review, using a **subagent-driven parallel-implementation** pattern: 4 implementers + 4 reviewers + 1 fix subagent dispatched concurrently against a pre-committed plan (`docs/superpowers/plans/2026-07-07-v3-10-0-minor-multi-finding-cleanups.md`).

| Task | Finding | Fix | Tests |
|------|---------|-----|-------|
| T1 | **C1** | `AppShellViewModel` injects `IMessageBoxPrompt` (replaces 2 `MessageBox.Show` sites) | +2 |
| T2 | **C3** | Extract `SessionAutoSaver<TVm>` generic base class (-250 LoC duplicate) | +3 |
| T3 | **H4** | `TraceSessionLibrary.Load` size cap (50MB) + path normalize + JsonOpts hardening | +3 |
| T4 | **H5** | `AscParser` + `ReplayOptions` + `CountingStream` (200MB defense-in-depth) | +3 |
| T4-fix | T4 SPEC_FAIL followup | Wire `ReplayOptions` DI in `AppHostBuilder` + `TraceSessionRegistry` | +3 |

**Test delta:** 1242 + 5 SKIP / 0 fail → **1256 + 5 SKIP / 0 fail** (+14 active tests across 5 commits).
**Code stats:** +5 commits / +888 / -302 production (net +586 LoC, but offset by -250 LoC T2 dedup).

## Fixes

### C1 — `AppShellViewModel` injects `IMessageBoxPrompt` (closes VM testability gap)
**Files:** `AppShellViewModel.cs`, `WpfMessageBoxPrompt.cs`, `TraceSessionAutoSaver.cs` (interface), `AppHostBuilder.cs` (DI)
**Tests:** +2 in `AppShellViewModelMessageBoxPromptTests.cs`

The 2 missing-asc-files dialogs (`OpenSessionAsync` / `OpenRecentSessionAsync`) previously called `MessageBox.Show` directly, breaking VM unit testability (the existing 11 `Substitute.For<IMessageBoxPrompt>()` doubles in `AppLifecycleShutdownTests` couldn't cover the path). The fix adds a new `IMessageBoxPrompt.ShowInformationAsync` overload (OK-only modal), wires it through DI, and replaces both `MessageBox.Show` sites.

### C3 — Extract `SessionAutoSaver<TVm>` generic base class
**Files:** `SessionAutoSaver.cs` (NEW, 196 LoC), `TraceSessionAutoSaver.cs` (rewritten), `ReplaySessionAutoSaver.cs` (rewritten)
**Tests:** +3 in `SessionAutoSaverBaseTests.cs` (contract tests via FakeAutoSaver test subclass); 12 pre-existing subclass tests preserved

The two concrete auto-savers were 95% identical (~250 LoC of duplicate code). Extracted the orchestration (TrySave / TryLoad / Apply / log hooks via `protected virtual void` + `[LoggerMessage]` source-gen) into a generic base. The subclasses are now thin (~30 LoC) config-only: VM type, provider, prompt copy, log message IDs.

### H4 — `TraceSessionLibrary.Load` size cap + path normalize
**Files:** `TraceSessionLibrary.cs`
**Tests:** +3 in `TraceSessionLibraryTests.cs`

Mirrors the `RecentSessionsService` (v3.8.8 PATCH F2) and `TraceViewerService` (v3.9.1 PATCH Bug #2) size-cap patterns. Adds `MaxLoadFileBytes = 50 MB` constant + `FileInfo.Length` precheck + `PathNormalizer.Normalize` defense-in-depth at the load seam. Also adds `MaxDepth = 64` + `ReferenceHandler = ReferenceHandler.IgnoreCycles` to `JsonOpts` (L7 lesson — uniform JSON-Options hardening across all 7 sites).

### H5 — `AscParser` enforces stream-size cap (defense-in-depth)
**Files:** `ReplayOptions.cs` (NEW), `AscParser.cs`, `TraceViewerService.cs`, `AppHostBuilder.cs` (DI), `TraceSessionRegistry.cs` (DI)
**Tests:** +3 in `AscParserTests.cs` (oversize-seekable, oversize-non-seekable, undersize) + +3 integration in `AppHostBuilderTests.cs`

`AscParser.ParseAsync(stream, ReplayOptions, ...)` now enforces a configurable stream-size cap (default 200 MB). Seekable streams use `stream.Length` precheck; non-seekable streams wrap in a `CountingStream` that throws `ReplayLoadException` on oversize. The cap is configurable via `Replay:MaxFileSizeBytes` in `appsettings.json`. The service-layer `TraceViewerService.MaxAscFileBytes` cap remains in place — both layers now defend.

## Process: subagent-driven parallel implementation

This MINOR used a new pattern: 4 implementer subagents + 4 reviewer subagents dispatched in parallel against a pre-committed plan, with explicit file-scope lists in each dispatch prompt. The pattern surfaced one issue: T4's implementer reported "AppHostBuilder wiring survived stash" but the reviewer caught via `git diff --stat` that the file was never actually added to the commit (likely `git stash` ate untracked files, lesson already captured). The T4 fix subagent + re-reviewer closed the loop within the same dispatch batch.

See:
- Plan: `docs/superpowers/plans/2026-07-07-v3-10-0-minor-multi-finding-cleanups.md`
- T2 per-piece topic: `01-Projects/peakcan-host/development/v3-10-0-minor-t2-c3-session-auto-saver-extraction-2026-07-07.md`
- T4 fix-up topic: `01-Projects/peakcan-host/development/v3-10-0-minor-t4-fix-app-host-builder-wiring-2026-07-07.md`

## NEW 1-of-1 lessons

1. **`partial-method-override-across-inheritance-requires-explicit-virtual-not-partial`** (from T2) — C# 13 forbids `partial` methods in a derived class from overriding `partial` methods in a base class. For template-method logging in a generic base, use `protected virtual void` (not `partial void`) and let the subclass use plain methods or `[LoggerMessage]`-attributed methods.

2. **`parallel-subagent-git-stash-with-u-can-eat-untracked-files`** (from T2) — `git stash -u` (or stash including untracked) silently drops untracked files. When parallel subagents do `git stash` operations, untracked file edits (e.g., a new factory binding in `AppHostBuilder.cs`) can vanish. Verify with `git diff --stat` after each commit, not just `git status`.

3. **`subagent-driven-development-with-parallel-implementers-requires-explicit-file-scope`** (from v3.10.0 overall) — When dispatching ≥2 parallel subagents on a multi-task plan, each dispatch prompt must include: (a) the full list of files this task OWNS (modify/create), (b) the full list of files SIBLING tasks OWN (DO NOT TOUCH), (c) the rationale (independent-task design + parallel safety). Without this, parallel work creates ambiguous ownership and risk of silent overwrites.

## Upgrade notes

No breaking changes. All new public API surfaces are additive:
- `IMessageBoxPrompt.ShowInformationAsync` (new method)
- `TraceSessionLibrary.MaxLoadFileBytes` (new const)
- `ReplayOptions` (new record) + `Replay:MaxFileSizeBytes` config key
- `AscParser.ParseAsync(Stream, ReplayOptions, ILogger?, CancellationToken)` (new overload)
- `SessionAutoSaver<TVm>` (new abstract base class)

Existing 1-arg `TraceViewerService(ILogger)` ctor and 1-arg `TraceSessionRegistry(...)` ctor preserved via chain to the new ctors.

## Deferred to v3.11.0 MINOR

The remaining 34 review findings:
- **C2** `ReplayViewModel` god class split (1153 LoC → 3 VMs; 1+ day)
- **H3** ODX/PDX import path normalization (vendor-format specific)
- **H6** `asc-search-dirs.json` allowlist
- **H7** `BuildSnapshot` async + dedup (needs `TraceSessionSnapshotBuilder` extraction)
- **H8** `TraceViewerViewModel.RebuildSignalsCore` 145-LoC split
- **H9** `ReplayService.EmitFrame` sync-over-async
- **M1-M13** Mechanical cleanups (naming, XAML keys, exception types, etc.)

## Next

- v3.11.0 MINOR — C2 god-class split + H3/H6 vendor-format + H7-H9 refactor
- v3.9.x PATCH chain — visual UI smoke testing of all v3.9.x features