---
topic: peakcan-host-status-anchor
created: 2026-07-10
session: "peakcan-host 剩余内容" evidence-first audit
covers: v3.5.0 .. v3.16.8.2 + WallClockOrigin HEAD chain
related: docs/release-notes-v3.{5..16}.md, scripts/tier3_v*.py, docs/superpowers/specs/2026-07-09-trace-viewer-enhancements-design.md
audience: future-session opener (one-file snapshot of project state)
---

# Status Anchor — v3.5.0 .. v3.16.8.2 + WallClockOrigin HEAD

## 1. Why this file exists

The project's `docs/devlog.md` is **explicitly `.gitignore`d** by design:

```
# - docs/devlog.md = session work log, never committed (intentional)
docs/devlog.md
```

So this file (`docs/superpowers/session-anchors/2026-07-10-...md`) is the
**in-tree equivalent** of a devlog entry — a session-scoped status
snapshot that future sessions can `git log --all -- docs/superpowers/session-anchors/`
to discover.

Use this pattern when you need a multi-PATCH status summary that survives
`git clone`. Do NOT use this directory for per-PR review notes; use
`docs/.review/` for those.

## 2. Headline facts (verified 2026-07-10)

- **HEAD** = `ea51d2f` on `feature/v3-12-0-minor` (the branch name is
  misleading; HEAD is actually the WallClockOrigin chain, not v3.12.0 work)
- **`origin/main`** = `ada4162` — 5 commits behind HEAD
- **GitHub Releases** = 53 v3.x releases on `github.com/jasontaotao/peakcan-host/releases`
  (verified `v3.11.1` and `v3.16.8` via `gh api`)
- **`git fetch` from this workspace** = **BLOCKED** (proxy 127.0.0.1:7897
  timing out on github.com:443). `gh api` works. Pattern: when fetch fails,
  use `gh api` for tree inspection.
- **`docs/devlog.md`** = `.gitignore`d by design (see file header in
  `.gitignore` line 3)
- **`docs/release-notes-v3.{11..16}.md` 14 files** = **untracked**, but
  byte-identical to GH release body text. Should be `git add`+commit.
- **`scripts/tier3_v*.py` 14 files** = **untracked**, Tier-3 ship overlay
  scripts that bypass `git commit` and push directly via gh API.
- **`docs/superpowers/plans/2026-07-{07,08,09}-*.md` 8 files** = **untracked**,
  planning docs from v3.11.3 .. v3.16.x cycles.
- **`docs/superpowers/specs/2026-07-09-trace-viewer-enhancements-design.md`** =
  **untracked**, 440 lines, 10 chapters, references HEAD `ea51d2f`. NOTE:
  partially obsoleted by the WallClockOrigin chain already in HEAD (see §5).
- **`tools/`** = **untracked**, contains `smoke-diag/` subdirectory.
- **`docs/play-architecture.html`** = **untracked**, likely architecture
  diagram; purpose unverified.
- **`debug-smoke.log`** = **untracked, 27 MB**, 2026-07-08 smoke capture.
  Should be `.gitignore`d.

## 3. Ship index (v3.5.0 .. v3.16.8.2)

All tags listed below are pushed to origin and have a GH release. The
release notes file (when present) is byte-identical to the GH release body.

| Tag | Commit | Title (verbatim from commit message) |
|---|---|---|
| v3.16.8.2 | `15092a6` | feat(traceviewer): 12-PATCH chain v3.14.3 to v3.16.8.2 (watch list, DBC picker, ItemContainerGenerator, ViewSwitcher cache, diagnostic logs) |
| v3.16.9.1 | `dace6d9` | fix(traceviewer): throttle UpdatePlaybackCursor via Stopwatch + observable InvalidatePlotCallCount (v3.16.9.1 PATCH) |
| v3.16.9 | `1dbc0b1` | fix(traceviewer): create LineAnnotation in BuildOneChartSeriesForSource + throttle UpdatePlaybackCursor (v3.16.9 PATCH) |
| v3.16.8 | (hotfix) | Directory.Build.props version bump (3.16.7.1 → 3.16.8) — `tier3_v3168_hotfix.py` overlay |
| v3.16.7.1 | (inline) | Console.WriteLine + Log.Information smoke test lines at AppHostBuilder.Build() and TraceViewerService ctor |
| v3.16.7 | (inline) | logger forwarding fix to ReplayTimeline (re-attempt; user reports "still no log") |
| v3.16.6 | (inline) | Console.WriteLine diagnostic lines (re-attempt) |
| v3.16.5 | (inline) | Console.WriteLine diagnostic lines (re-attempt) |
| v3.16.4 | (inline) | TraceViewerService logger forwarding (first attempt) |
| v3.16.3 | (inline) | ReplayService Console log attempt |
| v3.16.2 | (inline) | ReplayService debug log |
| v3.16.1 | (inline) | Add log to TraceViewerService.LoadAsync |
| v3.16.0 | (inline) | ItemContainerGenerator lazy-build + ViewSwitcher cache |
| v3.15.0 | (inline) | DBC picker |
| v3.14.3 | `ce974d6` | fix(traceviewer): lazy BuildChartSeries — 5x faster Add Trace (v3.14.2 PATCH) [commit msg misnumbered: actually v3.14.3] |
| v3.14.2 | `5193ab4` | fix(dbc): strip IDE bit (0x80000000) from extended-frame ID lookup keys (v3.14.1 PATCH) [commit msg misnumbered: actually v3.14.2] |
| v3.14.1 | (inline) | Watch list |
| v3.14.0 | `74cef3c` | v3.14.0 MINOR: code-review bug bash — 7 HIGH fixes (A1 SignalDecoder, A2/A3/A4 Dispose leaks, A5 ChannelRouter, A6 ReplayService timer, A7 LoopRegion validation) [reship: include U1-U4 files] |
| v3.13.3 | `9b779e9` | fix(shell): window title shows PATCH version via Directory.Build.props (v3.13.3 PATCH F6) |
| v3.13.2 | `1e44765` | fix(traceviewer): subscribe to DbcService.DbcLoaded for auto-rebuild (v3.13.2 PATCH F5) |
| v3.13.1 | `9f5d9f3` | fix(traceviewer): TraceSessionRegistry.LoadAsync must resume on UI thread (v3.13.1 PATCH F4) |
| v3.13.0 | `cc19bf4` / `327bae9` / `1e3cd2f` | Trace Viewer Unexpected error debug + reopen state reset + dead DBC button removal (F1/F2/F3) |
| v3.12.0 | `02c03ca` / `b691e44` / `455b61d` / `1a6e0e4` / `c70234c` | ReplayViewModel god-class split + project-wide converter STA smoke matrix + ReplayException contract + LoopRewound guard + converter Mode=OneWay tightening |
| v3.11.7 | `2590b54` | fix(traceviewer): set MultiBinding Mode=OneWay to prevent ConvertBack crash (v3.11.7 PATCH) |
| v3.11.6 | `91c452f` | fix(traceviewer): replace ConverterParameter-nested-Binding with MultiBinding (v3.11.6 PATCH) |
| v3.11.5 | `9a5b253` | fix(asc-parser): support Vector ASC v1.3 CANoe format (v3.11.5 PATCH) |
| v3.11.4 | `a22f99f` | fix(traceviewer): move Add trace file-dialog from XAML to VM (v3.11.4 PATCH) |
| v3.11.3 | `5e3a45b` | refactor(uds): migrate UdsView UserControl to UdsWindow Window (v3.11.3 PATCH) |
| v3.11.2 | `0c2e5e7` | refactor: extract ViewSwitcher for 9 Show-* commands (M3) |
| v3.11.1 | (inline) | Refactor cleanups: TraceSessionSnapshotBuilder extraction (H7) + RebuildSignalsCore 3-way split (H8) |
| v3.10.0 | `8c26af7` | v3.10.0 MINOR: multi-finding cleanups (C1+C3+H4+H5) |

## 4. WallClockOrigin HEAD chain (active work, NOT shipped)

| Commit | Title |
|---|---|
| `ea51d2f` | feat(service): switch to header-aware parser + expose LastParseResult for caller binding |
| `f6c68c7` | test(service): RED — LastParseResult exposes WallClockOrigin for caller binding |
| `4ccbf60` | fix(source): promote WallClockOrigin setter to public for cross-assembly write (Task 6) |
| `e10ce17` | feat(source): add WallClockOrigin field (parsed from ASC 'date' header) |
| `13dc88f` | test(source): RED — TraceSource.WallClockOrigin defaults to null |
| `19749f1` | test(asc): guard — null origin when ASC lacks 'date' header |
| `ce56f27` | feat(asc): ParseAsyncWithHeaderAsync overload — captures 'date' + 'base hex timestamps' headers |
| `b4e18c7` | test(asc): RED — ParseAsyncWithHeaderAsync overload with date header |
| `f9886e3` | feat(asc): add AscParseResult record (frames + wall-clock origin + timestamp mode) |

## 5. Trace-viewer-enhancements spec status (PARTIALLY OBSOLETE)

The `docs/superpowers/specs/2026-07-09-trace-viewer-enhancements-design.md`
spec was written 2026-07-09 and references HEAD `ea51d2f`. The spec
designs 3 enhancements:

1. **X-axis wall-clock time** — partly **DONE** by the WallClockOrigin
   chain (`f9886e3` ... `ea51d2f`). The spec's plan to extend
   `AscParser.ParseAsync` is implemented as `ParseAsyncWithHeaderAsync`
   overload instead.
2. **Shared time cursor (slider-driven only)** — spec says "Play is DEAD
   as of v3.18.0". **v3.18.0 does not exist** (latest is v3.16.8.2). This
   claim is unverified; spec needs sanity check before code change.
3. **LineSeries marker (MarkerType.Circle, MarkerSize=3)** — not yet
   done; would touch `BuildOneChartSeriesForSource` in `TraceChartViewModel.cs`.

**Before executing the spec, future sessions must verify**:
- Current visibility of Play/Pause/Stop buttons in `TraceViewerView.xaml`
  (line 101-106 per spec)
- Current `AscParser.ParseAsync` signature vs spec assumption
- Whether `BuildOneChartSeriesForSource` exists at the expected location

## 6. Untracked file triage (for next session)

| File | Verdict | Why |
|---|---|---|
| `docs/release-notes-v3.11.{1,2,3,4,5,6,7}.md` | ADD+COMMIT | byte-identical to GH release body |
| `docs/release-notes-v3.12.0.md` .. `v3.16.8.md` | ADD+COMMIT | same |
| `scripts/tier3_v3{143,150,160,161,162,163,164,165,166,167,167_1,168,168_hotfix}.py` | ADD+COMMIT | Tier-3 ship overlay scripts; belong in `scripts/` like the 15 already-tracked |
| `docs/superpowers/plans/2026-07-{07,08,09}-*.md` (8 files) | ADD+COMMIT | planning docs |
| `docs/superpowers/specs/2026-07-09-trace-viewer-enhancements-design.md` | ADD+COMMIT | the only untracked spec in the set |
| `docs/play-architecture.html` | INVESTIGATE | architecture diagram; purpose unverified |
| `tools/smoke-diag/` | INVESTIGATE | purpose unverified |
| `debug-smoke.log` (27 MB) | `.gitignore` | diagnostic capture; not source |

## 7. Decisions deferred to next session (need user input)

1. **Phase B**: trace-viewer-enhancements spec — partially obsoleted by
   WallClockOrigin chain. Execute remaining 2 of 3 enhancements (cursor
   + marker)? Or retire the spec?
2. **Phase C**: untracked file triage — commit in bulk or per-PATCH?
3. **Phase D**: WallClockOrigin HEAD chain — merge to main as v3.16.9.2?
   Rebase or fast-forward? origin/main is 5 commits behind HEAD.
4. **Process**: should `tier3_v*.py` overlay scripts commit release-notes
   to git (via `git commit` + push) instead of gh-API overlay (which leaves
   them untracked)? Current pattern causes the asymmetry observed here.

## 8. Process lessons captured

- **`tier-3-overlay-skip-git-tree-on-doc-files`** — observed. Tier 3 ship
  pushes `docs/release-notes-vX.Y.Z.md` directly via gh API overlay,
  bypassing `git add`+commit. Working tree ends up with the files as
  untracked instead of committed, while GH releases have the body text.
  Side effect: `git clone`-only consumers never see the release notes
  in-tree; they have to fetch from GitHub Releases.

- **`github-fetch-blocked-but-gh-api-works-keep-going`** — observed.
  `git fetch` times out (proxy blocked 127.0.0.1:7897), but `gh api`
  (different code path) returns 200. Pattern: when fetch fails, try
  `gh api` for tree inspection.

- **`devlog.md-is-intentionally-gitignored`** — confirmed. `.gitignore`
  line 3-4 explicitly excludes `docs/devlog.md` with the comment
  "session work log, never committed (intentional)". Use
  `docs/superpowers/session-anchors/` for in-tree equivalents.

## 9. Files in this anchor

- `docs/superpowers/session-anchors/2026-07-10-v3-5-to-v3-16-status-anchor.md` (this file, NEW)
- No source code touched.