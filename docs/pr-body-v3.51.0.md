# v3.51.0 MINOR — BLF (Vector Binary Logging Format) Parser

## Summary

Pure .NET BLF parser supporting classic CAN 11-bit + classic CAN 29-bit +
CAN FD + CAN FD 64. peakcan-host now loads `.blf` traces captured with
Vector CANalyzer without depending on the Vector XL Driver Library at runtime.
zlib decompression of LOG_CONTAINER supported.

## Changes

**Core** (PeakCan.Host.Core):
- `BlfFormat` (NEW, ~30 LoC) — single source of truth: file/obj magic
  (LOGG/LOBJ), ObjType numeric IDs (1/10/86/100/101), frame struct
  sizes (12/28/76/48), file header size (24), object header base
  size (16). All values verified 1:1 against vblf reference
  (zariiii9003/vblf master, fetched 2026-07-16 to
  .superpowers/sdd/reference/).
- `BlfParser` (NEW, ~80 LoC) + 7 partials:
  - `FileHeaderParserFlow.cs` — placeholder (file header inline in ParseAsync)
  - `ObjectStreamParserFlow.cs` — placeholder (object loop inline)
  - `LogContainerFlow.cs` — zlib decompress + recursive parse
  - `CanMessageFlow.cs` — 12-byte HBBI8s unpack
  - `CanMessage2Flow.cs` — 28-byte HBBI8sIBBH unpack
  - `CanFdMessageFlow.cs` — 76-byte HBBIIBBBBI64sI unpack
  - `CanFdMessage64Flow.cs` — 48+8-byte unpack
- `ReplayService.LoadAsync` (modify) — extension-based dispatch:
  `.blf → BlfParser`, default → AscParser.

**Tests** (+23 new):
- `BlfFormatTests` — 9 tests (file/obj magic, 4 frame type IDs, 4
  format sizes, file header size, timestamp scale, vblf fixture
  size verification)
- `BlfParserTests` — 14 tests (4 happy-path frame classes, 4 negative
  tests, 2 LOG_CONTAINER tests, 2 mixed/padding tests, 1 vblf
  fixture round-trip)
- `BlfParserManualTests` — 1 test `[Trait("Manual", "true")]` —
  public vblf fixture verification (CI-skip)

## Behavior changes

None for `.asc` users. New `.blf` path produces the same
`IReadOnlyList<ReplayFrame>` shape consumed by TraceViewer /
ReplayTimeline / FrameEmitted event downstream.

## Test plan

- [x] `dotnet test --filter "FullyQualifiedName~BlfFormatTests"` — 9/9 PASS
- [x] `dotnet test --filter "FullyQualifiedName~BlfParserTests"` — 14/14 PASS
- [ ] `dotnet test --filter "FullyQualifiedName~BlfParserManualTests"` — user runs locally
- [x] `dotnet test` (full suite, excluding manual trait) — App.Tests 826, Core.Tests 489, Infra.Tests 89 PASS
- [x] `dotnet build src/` — 0 errors, 0 new warnings

## Sister patterns

- v3.49.0 MINOR AscFormat + AscParser/ partials (single source + 5 partials)
- v3.10.0 MINOR T4 H5 `ReplayOptions.MaxFileSizeBytes` CountingStream wrap (reused in ReplayService)
- v3.50.5 PATCH strict error handling (50% corruption threshold, magic/version validation)
- W19 R1 LESSON (verbatim re-extraction) — BLF constants must be 1:1 from vblf reference
- W22 STRUCT-FABRACTION (struct-ctor verification) — applied to BLF frame struct format sizes
- v3.51 original plan manual test pattern — [Trait("Manual", "true")] CI-skip

## NEW 1/3 lesson candidates (6 total)

1. `spec-must-reverse-engineer-from-working-reference-not-invent-from-common-descriptions`
2. `blf-frame-classes-use-struct-format-from-vblf-not-arb-length-values`
3. `log-container-objtype-10-triggers-zlib-decompress-and-recursive-parse`
4. `lobj-signature-may-have-padding-bytes-between-objects`
5. `50-percent-corruption-threshold-matches-existing-asc-parser-pattern`
6. `reference-implementation-fetch-requires-explicit-named-source-authorization`

## Out of scope

See [release notes](docs/release-notes-v3.51.0.md) §Out of scope:
- BLF writing
- LIN / FlexRay / MOST / Ethernet / AFDX / A429 frame types
- CAN XL frame container (not in vblf reference; tracked for v3.52.0+)
- Real-time BLF streaming
- Multi-source BLF bundle
- Real user-vehicle BLF fixture in tests/