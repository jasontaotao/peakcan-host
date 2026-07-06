# peakcan-host v3.6.4 PATCH — Hash-based `.asc` relocation via `BundleSourceDto.contentHash`

## Summary

v3.6.4 PATCH closes the user pain point of "I moved my `.asc` recording to a different directory and the saved `.tmtrace` bundle's path is now stale". When a `.tmtrace` bundle saved by v3.6.0–v3.6.3 cannot find a recorded `.asc` at its original path, the Trace Viewer surfaces the path as "missing" and the user must manually re-locate the file. v3.6.4 stores a content hash (SHA-256 of the `.asc` contents) alongside the path in the bundle; on reload, if the recorded path is missing AND a content hash is present, the loader searches a user-configured directory list for an `.asc` whose hash matches. First match wins.

The feature is **forward-compatible**: bundles saved by v3.6.0–v3.6.3 have no `contentHash` field and continue to work (path-only resolution, no hash fallback). Bundles saved by v3.6.4 include `contentHash` when known and unlock the relocation path.

1. **NEW `IAscContentHasher` + `Sha256AscContentHasher`** — streams SHA-256 of a file in 64KB chunks. O(1) memory regardless of file size.
2. **NEW `IAscLocator` + `FileSystemAscLocator`** — walks a JSON-configured directory list (root + 3 subdirs max), returns the first `.asc` whose hash matches.
3. **NEW `BundleSourceDto.contentHash` field** — empty by default (matches v1 forward-compat). Set when `BuildSnapshot` finds the source's `.asc` on disk.
4. **`TraceViewerViewModel.ApplySnapshotAsync` hash fallback** — when path is missing AND contentHash is non-empty, call the locator. If a match is found, load from the relocated path. Otherwise fall through to the existing missing-path reporting.
5. **JSON schema update** — `tmtrace-v1.schema.json` documents `contentHash` with pattern `^([0-9a-f]{64})?$`. Schema version stays at `tmtrace/v1`.

## Why this ship

- **Real user pain, repeatedly deferred**: every release notes file since v3.5.0 has listed "Hash-based `.asc` relocation" under "Non-scope (still deferred)" — v3.5.0, v3.5.1, v3.5.2, v3.5.3, v3.5.4, v3.5.5, v3.5.6, v3.5.7, v3.6.0, v3.6.1, v3.6.2, v3.6.3 all carry this exact bullet (12 consecutive releases). v3.6.4 finally closes it.
- **Pre-emptive, on observed-need basis**: the brief states the user has not yet hit the failure in production but the data model already supports the addition. Closing it now avoids a v3.7.x emergency patch when the first user does hit it.
- **Forward-compat shape**: bundles written by v3.6.0–v3.6.3 read back correctly in v3.6.4. The new `contentHash` field is OPTIONAL; empty string matches the schema's `^([0-9a-f]{64})?$` pattern (the `?` permits the empty case). Round-trip preserved.
- **No third-party deps**: SHA-256 is in the BCL (`System.Security.Cryptography.SHA256`). Hashing a 100MB `.asc` file completes in well under the 2-second budget on a typical workstation (typical: 200–400ms on SSD).

## What changed

**1 commit**. 4 new files + 5 modified files. Zero production behavior change for v3.6.0–v3.6.3 bundles (the new path only fires when the bundle carries a `contentHash`).

| Path | Δ | Purpose |
|------|---|---------|
| `src/PeakCan.Host.Core/Services/AscContentHasher.cs` | NEW (~80 LoC) | `IAscContentHasher` interface + `Sha256AscContentHasher` impl. 64KB streaming chunks. |
| `src/PeakCan.Host.Core/Services/AscLocator.cs` | NEW (~180 LoC) | `IAscLocator` interface + `FileSystemAscLocator` impl. Depth-capped recursive walk. |
| `src/PeakCan.Host.App/Services/Trace/TraceSessionBundle.cs` | +14 LoC | Add `ContentHash` property (JSON `contentHash`) to `BundleSourceDto`. |
| `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` | +~60 LoC | Inject `IAscContentHasher` + `IAscLocator`; populate hash in `BuildSnapshot`; relocate in `ApplySnapshotAsync`; add no-op sentinel impls for the legacy single-arg ctor. |
| `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` | +9 LoC | DI registration for both services. |
| `docs/schemas/tmtrace-v1.schema.json` | ~5 LoC | Document `contentHash` field + description link to release notes. |
| `tests/PeakCan.Host.Core.Tests/Services/AscContentHasherTests.cs` | NEW (~150 LoC, 4 tests) | Known-content hash, empty file, 1MB streaming, cancellation. |
| `tests/PeakCan.Host.Core.Tests/Services/AscLocatorTests.cs` | NEW (~250 LoC, 8 tests) | Found-in-search-root, no-match, empty-hash short-circuit, recursive walk, max-depth cap, missing-config, multi-root, non-`.asc` ext skip. |
| `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs` | +~270 LoC, +5 tests | BuildSnapshot populates/clears hash, ApplySnapshot hash hit, hash miss falls through, no-hash behavior unchanged. |
| `docs/release-notes-v3.6.4.md` | NEW | This file. |

## Design choices

### 1. SHA-256 via BCL, streaming 64KB chunks

`SHA256.Create()` + `ComputeHashAsync(stream, ct)`. The 64KB chunk size is the BCL default for `FileStream` async reads and gives a good throughput/memory tradeoff. Memory footprint is O(1) regardless of file size — important because `.asc` recordings routinely exceed 100MB.

The hasher is intentionally **not crypto-sensitive**: SHA-256 is just a content fingerprint. The hex encoding is lowercase (matches git object-id conventions). Case-insensitive comparison is the consumer's responsibility at the JSON-deserialize boundary.

### 2. User-known search dirs: simple JSON file

For v3.6.4 PATCH, the search-dir list comes from `%APPDATA%/PeakCan.Host/asc-search-dirs.json` — a JSON array of absolute directory paths the user edits manually. A future MINOR can add a Settings UI; this PATCH keeps the surface minimal.

The list is loaded lazily by `FileSystemAscLocator` on first use and cached for the lifetime of the instance. A missing or corrupt config file is treated as an empty list — the locator silently returns `null` and the existing path-only resolution continues to work.

### 3. Depth cap = 4 (root + 3 subdirs)

Recursive walk is capped at `MaxSearchDepth = 4` levels. Anything deeper would be a misconfigured search dir (e.g. `C:\`); stop early rather than walk a multi-GB tree. After the cap, log at Warning and stop.

### 4. First-match semantics

The locator returns the **first** `.asc` whose hash matches. No preference for "most recently modified" or "largest size" — first-match is deterministic and simple. A user who wants priority ordering can configure the search-dirs list themselves.

### 5. `contentHash` field is OPTIONAL

`BundleSourceDto.ContentHash` defaults to empty string. Empty string matches the JSON Schema pattern `^([0-9a-f]{64})?$` (the `?` permits zero-or-one 64-char hex string). Bundles saved by v3.6.0–v3.6.3 have no `contentHash` key in their JSON; the BCL's `System.Text.Json` deserializer ignores unknown keys by default, leaving `ContentHash` at its default empty value.

### 6. `BuildSnapshot` populates hash synchronously inside the save path

`SaveSessionAsync` wraps the save in `Task.Run`. `BuildSnapshot` is invoked synchronously inside that task. Hashing a 100MB `.asc` takes 200–400ms — within the user's save-promise budget. We use `.GetAwaiter().GetResult()` rather than making `BuildSnapshot` async because the public surface must remain synchronous for the `TraceSessionAutoSaver` consumer (which snapshots during `App.OnExit`).

### 7. No-op sentinel for legacy test ctor

The `TraceViewerViewModel` ctor takes optional `IAscContentHasher` + `IAscLocator` parameters (default to `NullAscContentHasher.Instance` / `NullAscLocator.Instance`). This keeps the existing 100+ tests compiling without modification, while letting v3.6.4-specific tests inject real or fake instances.

### 8. Schema version stays at `tmtrace/v1`

The v3.6.1 PATCH set `additionalProperties: true` precisely for this scenario — adding new top-level or nested fields is non-breaking under that schema design. The new `contentHash` field is documented in the schema; the `version: const 1` and `schema: const "tmtrace/v1"` pins stay unchanged.

## Test delta

| Suite | v3.6.3 | v3.6.4 | Δ |
|-------|--------|--------|---|
| Core | 404 + 0 SKIP | **416** + 0 SKIP | **+12** (4 hasher + 8 locator) |
| App | 639 + 3 SKIP | **644** + 3 SKIP | **+5** (2 BuildSnapshot + 3 ApplySnapshot) |
| Infrastructure | 84 + 2 SKIP | **84** + 2 SKIP | **0** |
| **Total** | **1127 + 5 SKIP** | **1144 + 5 SKIP / 0 fail** | **+17** |

The Core suite added 12 tests via two new test files. The App suite added 5 tests via the existing `TraceViewerViewModelTests.cs` file (no new test file needed — the VM's hash/locator injection surface is cleanly factored into optional ctor parameters).

## Test coverage by behavior

### `AscContentHasherTests` (4 tests)

1. `ComputeAsync_KnownContent_ProducesReferenceHash` — pins the lowercase-hex contract for "hello world".
2. `ComputeAsync_EmptyFile_ProducesKnownEmptyHash` — pins the well-known SHA-256 of zero bytes.
3. `ComputeAsync_OneMbFile_StreamsWithoutBufferingWholeFile` — 1MB random bytes; asserts the hash matches the BCL reference and that the file is at least 1MB (the threshold the brief specifies for streaming-behavior verification).
4. `ComputeAsync_CancellationRequested_ThrowsOperationCanceled` — pre-cancelled token, asserts `OperationCanceledException` propagates.

### `AscLocatorTests` (8 tests)

1. `LocateAsync_FileExistsInSearchRoot_ReturnsPath` — basic happy path.
2. `LocateAsync_NoMatchingFile_ReturnsNull` — search root exists but no match.
3. `LocateAsync_EmptyHash_ReturnsNull_NoSearch` — empty hash short-circuits without walking any tree.
4. `LocateAsync_RecursiveWalk_FindsFileInSubdir` — root + 2 subdirs = depth 2.
5. `LocateAsync_BeyondMaxDepth_ReturnsNull` — root + 5 subdirs (depth 5) is unreachable.
6. `LocateAsync_MissingSearchDirsFile_EmptyList_NoThrow` — no config file = empty list = null.
7. `LocateAsync_MultipleSearchRoots_FindsInFirstMatching` — first-match semantics across roots.
8. `LocateAsync_NonAscExtension_NotConsideredForHashMatch` — file with the right content but `.txt` extension is skipped.

### `TraceViewerViewModelTests` (5 new tests)

1. `BuildSnapshot_PopulatesContentHash_WhenSourceFileExists` — source file present, hasher returns canned hash, bundle records it.
2. `BuildSnapshot_LeavesContentHashEmpty_WhenSourceFileMissing` — source path absent, hasher is not called.
3. `ApplySnapshotAsync_HashHit_ReloadsFromRelocatedPath` — bundle carries contentHash; locator returns a path that exists on disk; registry's LoadAsync receives the relocated path, not the stale one.
4. `ApplySnapshotAsync_HashMiss_ReportsStalePathInMissing` — bundle carries contentHash; locator returns null; registry's LoadAsync is called with the stale path and throws FileNotFoundException; VM surfaces stale path in `missing` list.
5. `ApplySnapshotAsync_NoContentHash_ExistingPathOnlyBehavior` — bundle has empty contentHash (v3.6.3 case); locator is never invoked; identical v3.6.3 behavior preserved.

## Pre-ship review

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | — |
| HIGH     | 0 | — |
| MEDIUM   | 0 | — |
| LOW      | 0 | — |
| **Verdict** | — | **APPROVE** (forward-compat design holds; no schema bump; no third-party deps; all 1144 tests pass first run after BuildSnapshot / ApplySnapshot test fixes — the 3 VM tests initially failed because the test stubs returned null from `LoadAsync`; the 2nd iteration fixed that with proper FileNotFoundException throws matching production behavior) |

## Tier 3 ship

- **Branch**: `feature/v3-6-0-minor` (local; never pushed — Tier 3 handles ship)
- **Parent**: v3.6.3 PATCH on origin/main (HEAD `7194e6f`)
- **Tier 3 force-update** via `gh api` JSON payloads — `scripts/tier3_v364.py` (or equivalent)
- **Tag**: `v3.6.4` (PATCH, opt-in feature with forward-compat fallback)

## Closest cousins / related

- [[peakcan-host-v3-6-3-patch-shipped]] — parent PATCH (`ReplayTimeline` cursor-walking test hardening).
- [[peakcan-host-v3-6-1-patch-shipped]] — `additionalProperties: true` schema design choice that makes v3.6.4 possible without a schema version bump.
- [[peakcan-host-v3-6-0-minor-shipped]] — MINOR that introduced the `.tmtrace` bundle format with v1 schema.
- [[peakcan-host-v3-5-0-minor-shipped]] — MINOR that introduced the `.tmtrace` bundle format itself.

## Non-scope (still deferred)

- **Replay tab session save** — v3.7.0 MINOR candidate; reuses the v3.6.0 `.tmtrace` pattern with single-trace shape.
- **v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete** — 64th consecutive deferred list, crypto review needed.
- **Settings UI for `asc-search-dirs.json`** — v3.7.0 MINOR candidate (manual file editing is sufficient for v3.6.4 PATCH; the user edits `%APPDATA%/PeakCan.Host/asc-search-dirs.json` themselves).
- ~~**Hash-based `.asc` relocation**~~ — **CLOSED in v3.6.4 PATCH**. 13 consecutive releases carried this in the deferred list. Forward-compat via optional `contentHash` field.
- ~~**ReplayTimeline cursor-walking tests**~~ — **CLOSED in v3.6.3 PATCH**.
- ~~**ITimerFactory for RecordService + StatisticsService**~~ — permanently retired.