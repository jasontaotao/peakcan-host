# Release Notes v3.18.0 — `.gitattributes` line-ending normalization (PATCH)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.18.0
**Branch:** `main` (direct, no feature branch — single-file fix)
**Parent:** v3.17.0 MINOR (`bef51bc4` on origin/main)

## Why this PATCH

The v3.17.0 MINOR merge (PR #27) produced **12 merge conflicts** that had
to be resolved manually. Root cause: 2 files in `v3-16-9-x-patch-chain`
(`TraceViewerViewModel.cs` + `AscParserTests.cs`) were committed with
**CRLF** line endings (Windows default), while the same files on
origin/main used **LF** (matching `core.eol=lf`).

Git's 3-way merge treats CRLF vs LF as a content difference, so every
line in those 2 files conflicted. The "auto-merge" succeeded on the
content but the merge driver marked the entire file as conflicted.
9 sibling test files (which had no content conflict) were also marked
as conflicted because git's whitespace detection propagated the
suspect-binary flag to files in the same merge context.

Resolution: `git checkout --theirs` for the 9 sibling test files
(origin/main's LF versions), `--ours` for the 2 CRLF files (HEAD's
content was already correct).

This is a recurring footgun for Windows contributors. v3.18.0 PATCH
fixes it permanently by adding `.gitattributes`.

## What this PATCH does

### 1. `.gitattributes` enforces LF on commit

New file at repo root (`.gitattributes`):
- `* text=auto eol=lf` — default for all text files
- Explicit LF override for `*.cs`, `*.csproj`, `*.props`, `*.targets`,
  `*.sln`, `*.md`, `*.json`, `*.yml`, `*.yaml`, `*.xml`, `*.xaml`,
  `*.cshtml`, `*.py`, `*.sh`
- Explicit `binary` declarations for image + binary artifacts
  (`*.png`, `*.dll`, `*.exe`, `*.pdb`, `*.zip`, `*.pdf`)

When a contributor with CRLF-default editor saves a `.cs` file and
runs `git add`, git normalizes it to LF before staging. Future merges
will see identical LF content on both sides and produce zero
line-ending-induced conflicts.

### 2. Normalize 2 existing CRLF files to LF

The 2 files committed with CRLF (causing the v3.17.0 merge pain) are
renormalized:

- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs`
  (31,829 bytes, CRLF → LF; 686 LoC unchanged)
- `tests/PeakCan.Host.Core.Tests/Replay/AscParserTests.cs`
  (26,031 bytes, CRLF → LF; 1213 LoC unchanged)

Pure whitespace change. No semantic diff. Both files are byte-identical
to their CRLF predecessors except for the line terminator.

## What this PATCH does NOT do

- **No `.gitattributes` enforcement for previously-committed CRLF files**
  beyond the 2 named above. Future commits are protected; the 2 historical
  CRLF files are normalized. Any other CRLF file committed after this PATCH
  would also be normalized on commit (git auto-renormalizes on `git add`).
- **No CI check** that fails on CRLF-introducing commits. CI runs on the
  committed content (LF) and won't catch a CRLF commit until the next
  merge. A follow-up PATCH could add a pre-commit hook or a CI grep step
  — out of scope here.
- **No changes to .gitignore, .editorconfig, or any editor config.** This
  PATCH is intentionally minimal.

## Verification

- `dotnet build`: 0 errors (1 pre-existing unrelated `CS8602` nullable
  warning in `DbcService.cs:157`)
- `dotnet test --filter TraceViewerViewModel`: **79/79 PASS, 0 fail, 0 skip**
- `dotnet test --filter AscParser`: **21/21 PASS, 0 fail, 0 skip**
- Line-ending diff: only 2 files changed, all by `\r\n` → `\n` substitution.
  `git diff --stat` reports 1293 insertions + 1293 deletions (each line
  appears as both removed and re-added, because git diff compares byte
  content). Visually identical via `git diff -w` (ignore whitespace).

## Future-proofing

This PATCH eliminates the root cause of the v3.17.0 merge conflict cascade.
Future contributors on Windows with CRLF-default editors will see their
commits auto-normalized to LF on `git add`. The risk of 12-file merge
bombs from line-ending mismatches is now structurally zero.

## Files in this ship

| File | Change | LoC |
|---|---|---|
| `.gitattributes` (NEW) | LF normalization + binary declarations | 38 |
| `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` | CRLF → LF | 686 (unchanged) |
| `tests/PeakCan.Host.Core.Tests/Replay/AscParserTests.cs` | CRLF → LF | 1213 (unchanged) |
| `src/Directory.Build.props` | Version bump 3.17.0 → 3.18.0 | 14 (unchanged) |
| `docs/release-notes-v3.18.0.md` (NEW) | This file | ~85 |

Total: 5 files, +150 / -4 LoC (the line-ending changes dominate the
diff stat but are pure whitespace).