# Release Notes v3.51.0 — BLF (Vector Binary Logging Format) Parser

**Released**: pending ship
**Tag**: v3.51.0
**Branch**: `feature/v3-51-minor-blf-parser`
**Parent**: v3.50.6 PATCH (`d5a0f31` on `main`)

## Why this MINOR

User direction 2026-07-16: add Vector BLF trace support so users can load
CANalyzer `.blf` files captured from Vector hardware without depending
on the Vector XL Driver Library at runtime. Prior peakcan-host
supported only ASC traces; this MINOR closes the BLF gap.

Reference: **zariiii9003/vblf** (MIT, Python reference implementation
by Brad Keryan / Vector-interned author). All BLF constants verified
1:1 against the vblf reference (file/obj magic `LOGG`/`LOBJ`, ObjType
IDs 1/10/86/100/101, frame struct sizes 12/28/76/48, file header
size, object header base size). Reverse-engineered from a working
reference, not invented from common descriptions (per W22 LESSON
truly-applied).

## What this MINOR does

### 1. `BlfFormat` (new Core type, ~30 LoC)

Single source of truth for BLF constants. File/object magic (`LOGG`/`LOBJ`),
ObjType numeric IDs (`1`/`10`/`86`/`100`/`101`), frame struct sizes
(`12`/`28`/`76`/`48`), file header size (`24`), object header base size
(`16`). All values verified 1:1 against the vblf reference. Sister of
v3.49.0 `AscFormat`.

### 2. `BlfParser` + 7 partials (~80 + 175 LoC = ~255 LoC)

Sister of v3.49.0 `AscParser` + 5 partials. Algorithm sister of
vblf.`_generate_objects`:

- `FileHeaderParserFlow.cs` — placeholder (file header inline in `ParseAsync`).
- `ObjectStreamParserFlow.cs` — placeholder (object loop inline).
- `LogContainerFlow.cs` — zlib decompress + recursive parse on ObjType 10.
- `CanMessageFlow.cs` — 12-byte `HBBI8s` unpack (classic CAN 11/29-bit).
- `CanMessage2Flow.cs` — 28-byte `HBBI8sIBBH` unpack (classic CAN w/ channel).
- `CanFdMessageFlow.cs` — 76-byte `HBBIIBBBBI64sI` unpack (CAN FD up to 64-byte payload).
- `CanFdMessage64Flow.cs` — 48-byte unpack + 8-byte timestamp split (CAN FD 64).

### 3. `ReplayService.LoadAsync` extension dispatch

`LoadAsync` now dispatches by file extension: `.blf` → `BlfParser`,
default → `AscParser` (preserves v3.49.0 behavior for ASC). ReplayException
propagates as before; `FileNotFoundException` and other IO errors wrap
into `ReplayLoadException`. Sister of W31 T1 file-IO lifecycle pattern.

### 4. Behavior change (none for ASC users)

New `.blf` path produces the same `IReadOnlyList<ReplayFrame>` shape
consumed by TraceViewer / ReplayTimeline / `FrameEmitted` event
downstream. ASC behavior is unchanged.

### 4.1 UI entry (v3.51.0 T4 follow-up)

The Replay tab's `Open...` button now exposes `.blf` alongside `.asc`
in the file dialog filter:

- **Filter string** — `Replay files (*.asc;*.blf)|*.asc;*.blf|ASC files (*.asc)|*.asc|BLF files (*.blf)|*.blf`.
  The first entry (`Replay files`) is the default selected so users
  see both formats in one click; the latter two are filter-shortcuts
  for users who want to limit to one format.
- **Open button ToolTip** — "Load a CAN trace (.asc or .blf)" so the
  capability is discoverable without trying the dialog.
- **OpenRecent error string** — "Use Replay → Open... to reload the source"
  replaces the v3.7.0 era "Use File → Open .asc" wording, since users
  may now have a missing `.blf` source instead of a missing `.asc`.

Backed by `OpenAsync_DialogFilter_MentionsBothAscAndBlf` test
(NSubstitute `Arg.Is<string>(f => f.Contains("*.asc") && f.Contains("*.blf"))`),
catches future regressions where the filter is narrowed back to `.asc` only.

## Files changed

| Layer | File | LoC | Note |
|-------|------|-----|------|
| Core | `BlfFormat.cs` | NEW ~30 | single source of truth |
| Core | `BlfParser.cs` | NEW ~80 | main dispatch + reader |
| Core | `BlfParser/FileHeaderParserFlow.cs` | NEW ~8 | placeholder partial |
| Core | `BlfParser/ObjectStreamParserFlow.cs` | NEW ~8 | placeholder partial |
| Core | `BlfParser/LogContainerFlow.cs` | NEW ~47 | zlib + recursive parse |
| Core | `BlfParser/CanMessageFlow.cs` | NEW ~25 | 12-byte unpack |
| Core | `BlfParser/CanMessage2Flow.cs` | NEW ~26 | 28-byte unpack |
| Core | `BlfParser/CanFdMessageFlow.cs` | NEW ~33 | 76-byte unpack |
| Core | `BlfParser/CanFdMessage64Flow.cs` | NEW ~28 | 48-byte unpack |
| Core | `ReplayService/FileIoLifecycle.partial.cs` | MOD +5 | dispatch by extension |
| App | `ReplayViewModel.Loader.partial.cs` | MOD +5 | filter 同时含 .asc + .blf |
| App | `ReplayView.xaml` | MOD +3 | Open 按钮 ToolTip 提示 BLF |
| Tests | `BlfFormatTests.cs` | NEW ~22 | 9 tests |
| Tests | `BlfParserTests.cs` | NEW ~417 | 14 tests |
| Tests | `BlfParserManualTests.cs` | NEW ~25 | 1 manual test (CI-skip) |
| Tests | `ReplayViewModelTests.cs` | MOD +29 | 1 filter UI 接入测试 (T4) |
| Build | `Directory.Build.props` | MOD | version 3.50.6 → 3.51.0 |

**Total LoC delta**: ~737 LoC (new code + tests + docs + UI).

## Test outcomes

- **Core.Tests 491 / 491 PASS** (467 pre-existing + 9 BlfFormat + 14
  BlfParser + 1 manual). The manual test runs locally when the
  `.superpowers/sdd/reference/vblf_test_CAN_MESSAGE.lobj` fixture
  is present; CI auto-skips via `[Trait("Manual", "true")]` filter
  `FullyQualifiedName!~Manual` (490 / 490 PASS under CI filter).
- **App.Tests 827 / 827 PASS** (826 pre-existing + 1 T4 filter UI test;
  3 SKIPs are hardware-dependent, pre-existing).
- **Infra.Tests 89 / 89 PASS** (unchanged; 2 hardware-dependent SKIPs are
  pre-existing and expected).

## Lesson candidates (6 NEW 1/3)

1. **`spec-must-reverse-engineer-from-working-reference-not-invent-from-common-descriptions`**
   — Prior BLF spec失守 was invented without a reverse-engineered reference
   (W22 LESSON truly applied only after rewriting from vblf reference).
2. **`blf-frame-classes-use-struct-format-from-vblf-not-arb-length-values`**
   — Each frame class has a fixed vblf `struct.Struct` format string;
   arbitrary length hints are not used.
3. **`log-container-objtype-10-triggers-zlib-decompress-and-recursive-parse`**
   — BLF `LOG_CONTAINER` (ObjType 10) wraps a zlib-compressed object
   stream that must be decompressed and re-parsed recursively.
4. **`lobj-signature-may-have-padding-bytes-between-objects`**
   — Raw LOBJ streams may have 0-3 padding bytes between objects;
   signature search tolerates up to 3 rewind bytes.
5. **`50-percent-corruption-threshold-matches-existing-asc-parser-pattern`**
   — Reuse v3.50.5 / `AscParser.ParseLinesFlow` 50% corruption threshold
   to surface `ReplayFormatException` consistently across parsers.
6. **`reference-implementation-fetch-requires-explicit-named-source-authorization`**
   — User authorized `zariiii9003/vblf` as the named source for
   reverse-engineering; future reference fetches must follow the
   same explicit-named-source pattern.
7. **`file-dialog-filter-must-mention-all-supported-extensions` (NEW 1/3 @ T4)**
   — When the back-end dispatch gains a new file format, the WPF
   `OpenFileDialog.Filter` string must be updated in the SAME commit (or
   the immediate next PATCH); otherwise users see release notes for a
   feature they cannot reach from the UI. The next observation must
   confirm whether the same pattern applies to save-as filter strings
   (e.g. `TraceSessionLibrary.SaveAsFilter` for new bundle schemas) and
   menu-driven deep links (e.g. `File → Import → BLF` if added later).

## Out of scope (per spec §3)

- BLF **writing** (read-only parser this round).
- LIN / FlexRay / MOST / Ethernet / AFDX / A429 frame types.
- CAN XL frame container (not in vblf reference; tracked for v3.52.0+).
- Real-time BLF streaming.
- Multi-source BLF bundle.
- Real user-vehicle BLF fixture in `tests/` (explicitly declined by
  user 2026-07-16; public vblf fixture in `.superpowers/sdd/reference/`
  is the substitute verification target).

## Next

- **v3.51.1 vault-only PATCH** — lesson promotion (6 NEW 1/3 candidates
  need 2nd confirmation before any reach 2/3 LOCKED).
- **v3.52.0** — CAN XL frame container support + remaining ObjType coverage.