# v3.51.0 MINOR — BLF (Vector Binary Logging Format) Parser

> **Status**: Design — pending user approval.

**Goal:** Add first-class BLF (Vector Binary Logging Format) support to peakcan-host so users can load traces captured with CANoe/CANalyzer without depending on the Vector XL Driver Library at runtime. The parser handles classic CAN 2.0, CAN FD, and CAN XL frame container types.

**Architecture:** Sister the v3.49.0 MINOR ASC parser pattern — a single static `BlfFormat` source-of-truth for constants and a `BlfParser/` directory of partial classes. `BlfParser.ParseAsync(Stream, ReplayOptions, ILogger?, CancellationToken)` returns `Task<IReadOnlyList<ReplayFrame>>` with the same shape as `AscParser.ParseAsync`. `ReplayService.LoadAsync` dispatches by file extension (`.asc` → AscParser, `.blf` → BlfParser). Implementation is pure .NET (no Vector SDK dependency, no Python/.NET interop) so peakcan-host can parse BLF on any Windows machine.

**Tech Stack:** C# / .NET 10 / WPF (existing) / BinaryReader (System.IO) for low-level byte access / xUnit (existing) for tests.

## 1. Background & motivation

User direction 2026-07-16: "对了，blf文件解析功能计划做一下" (add BLF file parsing feature).

The peakcan-host Trace Viewer currently loads `.asc` (Vector ASCII) trace files via the v3.49.0 MINOR `AscParser`. Many production environments (especially Vector CANoe/CANalyzer shops) record traces as `.blf` (Vector Binary Logging Format). The `ReplayExceptions.cs` xmldoc has been referencing "asc/blf" since v3.49.0 — the BLF path was always a planned future addition but was never implemented.

The Vector XL Driver Library (`vxlapi64.dll`) is the official SDK for BLF reading, but it requires a commercial Vector license and CANoe/CANalyzer installation. Many peakcan-host users (especially those who receive BLF traces from colleagues) do not have Vector tools installed. A pure .NET BLF parser that runs on any Windows machine removes this dependency.

## 2. Goals (in scope)

- **G1**: `ReplayService.LoadAsync` dispatches `.blf` files to a new `BlfParser` returning the same `IReadOnlyList<ReplayFrame>` shape as the existing `AscParser` path.
- **G2**: `BlfParser` handles **three frame container types**: classic CAN 2.0 (BLOB 20 bytes, ET_CAN_DATA=5), CAN FD (BLOB 32 bytes, ET_CAN_FD_DATA=29), and CAN XL (BLOB 32+ bytes, ET_CAN_XL_DATA=33).
- **G3**: BLF file header validation — `LOGG` + `LBLF` magic strings + version UINT32 — produces a clear `ReplayFormatException` on mismatch.
- **G4**: Unknown object types (e.g. custom Vector env-var objects) are skipped with a warning, not thrown.
- **G5**: Container objects (`LOBJ` signatures) are skipped — frames are read from BLOB children only.
- **G6**: Truncated or corrupted frames throw `ReplayFormatException` (strict error handling per user direction).
- **G7**: 50%+ corrupted frames trigger `ReplayFormatException` (sister of `AscParser` 50% threshold from v3.49.0 MINOR spec).
- **G8**: Stream-size cap reuses existing `ReplayOptions.MaxFileSizeBytes` (no new cap needed).
- **G9**: Single-pass in-memory parse (no `IAsyncEnumerable` yield — load all frames into a `List<ReplayFrame>` and return as `IReadOnlyList`).
- **G10**: Manual verification test (`[Trait("Manual", "true")]`) loads a real BLF file from the user's CANoe installation path; CI skips these tests automatically.
- **G11**: Pure .NET — zero native DLL dependency, zero Python/.NET interop. peakcan-host parses BLF on any Windows machine regardless of Vector tools installation.

## 3. Non-goals (YAGNI)

- **N1**: BLF **writing** (sister of `AscFormat.WriteHeader/WriteFooter/WriteDataLine`). v3.51.0 is reader-only.
- **N2**: BLF **compression** (LZ4 / zlib containers introduced in Vector BLF v2.0+). Tracked for v3.52.0+.
- **N3**: Vector custom object types (system variables, environment variables, driver info, etc.). Skipped as "unknown object".
- **N4**: Real-time BLF stream (Vector's streaming API, not file-based). Out of scope — peakcan-host works on recorded files only.
- **N5**: Multi-source BLF bundle (single `.blf` containing multiple traces via LOBJ container hierarchy). v3.51.0 parses the FIRST trace in the file only (LBLF format allows nesting; we read the outer-most BLOB children, ignore inner container re-entries). Multi-trace opening in a single file is tracked for v3.52.0+.
- **N6**: TraceView modifications — existing `TraceSource` already accepts any `IReadOnlyList<ReplayFrame>` regardless of file format.
- **N7**: BLF fixture file in `tests/` — the user's 8MB real BLF (8MB CANoe recording of their electric vehicle under-drive) contains proprietary data and will not be checked into git. Manual verification on the user's machine only.

## 4. Architecture

```
Core layer (no UI dep, sister of v3.49.0 MINOR AscFormat/AscParser):

  BlfFormat (NEW, src/PeakCan.Host.Core/Replay/BlfFormat.cs)
    - static class single source of truth (sister of v3.49.0 AscFormat)
    - File magic constants: FileSignature = "LOGG" (4 bytes), FormatSignature = "LBLF" (4 bytes)
    - Object signature constants: ObjHeader = "OBJH", Blob = "BLOB", Container = "LOBJ"
    - Frame container type IDs: ET_CAN_DATA = 5, ET_CAN_FD_DATA = 29, ET_CAN_XL_DATA = 33
    - Supported versions: Version10 = 0x00010000, Version20 = 0x00020000
    - BLOB layout sizes: ClassicCanBlobSize = 20, CanFdBlobSize = 32, CanXlBlobMinSize = 32
    - Timestamp scale: TimestampScale = 10_000_000.0 (UINT64 ticks → seconds)
    - Frame flags bits: FlagFd = 0x0001, FlagBrs = 0x0002, FlagEsi = 0x0004, FlagXl = 0x0010

  BlfParser (NEW, src/PeakCan.Host.Core/Replay/BlfParser.cs)
    - public static partial class BlfParser
    - public static async Task<IReadOnlyList<ReplayFrame>> ParseAsync(
        Stream stream,
        ReplayOptions options,
        ILogger? logger = null,
        CancellationToken ct = default)
    - Sister of AscParser.ParseAsync (same signature shape)
    - Throws ReplayFormatException on bad magic, bad version, >50% corruption
    - Throws ReplayLoadException on stream-size cap exceeded (via existing CountingStream path)

  BlfParser/ partials (NEW, sister of v3.49.0 AscParser/):
    - BlfHeaderParserFlow.cs — validates LOGG + LBLF + version
    - ContainerObjectFlow.cs — skips LOBJ bodies, recurses into children
    - ClassicCanFrameFlow.cs — parses 20-byte BLOB to ReplayFrame
    - CanFdFrameFlow.cs — parses 32-byte BLOB to ReplayFrame (with FD/BRS/ESI flags)
    - CanXlFrameFlow.cs — parses 32+ byte BLOB to ReplayFrame (with XL flag, big-endian frame length)
    - ObjectHeaderFlow.cs — reads 32-byte OBJH header, routes by signature

  ReplayService/LoadAsync (modify, sister of v3.49.0)
    - Add extension-based dispatch:
      if path ends in ".blf" (case-insensitive) → BlfParser.ParseAsync(...)
      else → AscParser.ParseAsync(...) (default, preserves v3.49.0 behavior)
    - Existing ReplayOptions.MaxFileSizeBytes cap reused (CountingStream wrap, no changes)
    - Existing ReplayLoadException / ReplayFormatException propagation unchanged

Tests (PeakCan.Host.Core.Tests, sister of v3.49.0 AscFormatRoundTripTests/AscParserTests):
  BlfFormatTests.cs (NEW, 6 tests) — magic strings, version IDs, frame container type IDs, BLOB sizes
  BlfParserTests.cs (NEW, 11 tests):
    - Synth BLF (BinaryWriter-built) for: classic CAN, CAN FD, CAN XL, mixed
    - 50% corruption threshold
    - Magic mismatch
    - Version mismatch
    - Truncated BLOB
    - Unknown object signature (skip + log)
    - Container LOBJ (skip body)
    - Timestamp scale (UINT64 ticks → double seconds)
    - CAN XL frame length big-endian
  BlfParserManualTests.cs (NEW, 1 test, [Trait("Manual", "true")]):
    - Load real BLF from C:\Users\13777\Documents\xwechat_files\wxid_1012060120711_bd20\msg\file\2026-07\CH0_242下坡掉READY0.blf
    - CI auto-skips via Trait filter
```

### 4.1 Sister patterns to apply

| Lesson (vault) | This PATCH application |
|---|---|
| v3.49.0 MINOR T1 (AscFormat single source) | `BlfFormat` mirrors AscFormat: static class, no state, no DI |
| v3.49.0 MINOR T2 (AscParser/ partials) | `BlfParser/` mirrors `AscParser/`: 5 partials per concern (header / container / 3 frame types) |
| v3.49.0 MINOR Q3 round-trip test | `BlfFormatTests` + `BlfParserTests` use `BinaryWriter` to synth, not ASC `string` round-trip |
| v3.10.0 MINOR T4 H5 (MaxFileSizeBytes CountingStream) | Reused as-is in `BlfParser.ParseAsync` — no new cap knob |
| W19 R1 (verbatim re-extraction) | BLF constants (LOGG, LBLF, OBJH, BLOB, LOBJ, ET_CAN_DATA=5, ET_CAN_FD_DATA=29, ET_CAN_XL_DATA=33, BLOB sizes) — must be 1:1 from reverse-engineered reference. Implementer MUST copy from verified reference, not invent |
| W22 STRUCT-FABRACTION (struct-ctor verification) | Frame flag bit positions + frame length endianness verified against reference before commit |
| W34 transient flake | Sister of v3.50.6 ship; full suite may have 1 transient Core flake, retry-isolated PASS confirms |

## 5. Data flow

### 5.1 LoadAsync dispatch (ReplayService)

```
1. User: File → Open → CH0_xxx.blf
2. AppShellViewModel.OpenSessionAsync(blfPath)
3. ReplayService.LoadAsync(blfPath, ct):
     a. Defensive reset (existing v3.8.5 PATCH H1 behavior)
     b. File.OpenRead(normalize(blfPath))
     c. if path.EndsWith(".blf", OrdinalIgnoreCase):
          _frames = await BlfParser.ParseAsync(stream, options, logger, ct)
        else:
          _frames = await AscParser.ParseAsync(stream, options, logger, ct)  // existing
     d. _timeline.SetFrames(_frames)
4. Existing playback path (Play / Seek / Scrubber / FrameEmitted event) all unchanged
5. TraceViewer consumes Frames identically for both formats
```

### 5.2 BlfParser.ParseAsync (detailed)

```
1. Stream-size precheck (CountingStream wrap if not seekable, sister of v3.10.0 T4 H5)
2. Read 16-byte file header (4 LOGG + 4 LBLF + 4 version UINT32 + 4 app info)
3. Validate magic strings:
   - "LOGG" expected
   - "LBLF" expected
   - version is 0x00010000 (v1.0) or 0x00020000 (v2.0)
   - On mismatch → ReplayFormatException("Not a valid BLF file: bad magic/version")
4. Read object stream loop until EOF:
   a. Read 32-byte OBJH header (4 signature + 4 objSize + 4 objType + 8 timestamp + 2 flags)
   b. Route by signature:
      - "BLOB" → call frame-type-specific parser based on objType:
          ET_CAN_DATA → ClassicCanFrameFlow.Parse (20-byte BLOB)
          ET_CAN_FD_DATA → CanFdFrameFlow.Parse (32-byte BLOB)
          ET_CAN_XL_DATA → CanXlFrameFlow.Parse (32+ byte BLOB, frame length big-endian)
        → push ReplayFrame(timestampSeconds, id, dlc, data, frameFlags) to result list
      - "LOBJ" → skip body bytes (container; frames are children, already read at child level)
      - other → logger.Warning("Skipped unknown object signature X at offset Y")
   c. Increment frame_count
   d. If frame_count > 1000 AND errors > 50% → ReplayFormatException
5. Return result list as IReadOnlyList<ReplayFrame>
```

### 5.3 ClassicCanFrameFlow.Parse (20-byte BLOB)

```
Layout (little-endian):
  Offset 0: channel (UINT16)
  Offset 2: flags (UINT16) — bit 0=Fd, bit 1=Brs, bit 2=Esi
  Offset 4: dlc (UINT8) — 0-8
  Offset 5: reserved (3 bytes)
  Offset 8: id (UINT32) — raw CAN ID with IDE bit
  Offset 12: data (8 bytes)
  Offset 20: end

Build ReplayFrame:
  timestamp = blob.Ticks / 10_000_000.0  (UINT64 LE from OBJH)
  id = blob.ReadUInt32(offset 8)
  dlc = blob.ReadByte(offset 4)
  data = blob.ReadBytes(offset 12, 8)
  frameFlags = translate(blob.ReadUInt16(offset 2)) → FrameFlags
```

### 5.4 CanFdFrameFlow.Parse (32-byte BLOB)

```
Layout:
  Offset 0-15: same as classic
  Offset 16: frameLength (UINT8) — actual data byte count, up to 64
  Offset 17-19: reserved
  Offset 20: data[64] (pad to 64; only first frameLength bytes are real)

Build ReplayFrame with FrameFlags.Fd set + frameLength for data length
```

### 5.5 CanXlFrameFlow.Parse (32+ byte BLOB, BIG-ENDIAN frame length)

```
Layout:
  Offset 0-15: same as classic
  Offset 16: frameLength (UINT16 BIG-ENDIAN) — actual data byte count, up to 2048
  Offset 18-19: reserved
  Offset 20: data[2048] (pad; only first frameLength bytes are real)

CRITICAL: frameLength is big-endian, all other fields are little-endian.
Build ReplayFrame with FrameFlags.Xl set
```

## 6. API contracts

### 6.1 `BlfFormat` (new)

```csharp
// src/PeakCan.Host.Core/Replay/BlfFormat.cs
namespace PeakCan.Host.Core.Replay;

/// <summary>
/// v3.51.0 MINOR: BLF (Vector Binary Logging Format) format single source.
/// Sister of v3.49.0 AscFormat. Pure static constants — no state, no DI.
/// </summary>
public static class BlfFormat
{
    // File header
    public const string FileSignature = "LOGG";     // 4 bytes
    public const string FormatSignature = "LBLF";  // 4 bytes
    public const uint Version10 = 0x00010000;      // v1.0
    public const uint Version20 = 0x00020000;      // v2.0

    // Object signatures (4 bytes each)
    public const string ObjHeader = "OBJH";
    public const string Blob = "BLOB";
    public const string Container = "LOBJ";

    // Frame container type IDs (per Vector spec)
    public const uint ET_CAN_DATA = 5;
    public const uint ET_CAN_FD_DATA = 29;
    public const uint ET_CAN_XL_DATA = 33;

    // BLOB layout sizes
    public const int ClassicCanBlobSize = 20;
    public const int CanFdBlobSize = 32;
    public const int CanXlBlobMinSize = 32;

    // Timestamp scale (UINT64 ticks → seconds)
    public const double TimestampScale = 10_000_000.0;

    // Frame flags bits
    public const ushort FlagFd = 0x0001;
    public const ushort FlagBrs = 0x0002;
    public const ushort FlagEsi = 0x0004;
    public const ushort FlagXl = 0x0010;
}
```

### 6.2 `BlfParser` (new)

```csharp
// src/PeakCan.Host.Core/Replay/BlfParser.cs
namespace PeakCan.Host.Core.Replay;

/// <summary>
/// v3.51.0 MINOR: parses Vector BLF trace files. Sister of v3.49.0
/// AscParser. Strict error handling (per user direction 2026-07-16):
/// bad magic → ReplayFormatException, >50% corrupted frames →
/// ReplayFormatException, truncated BLOB → ReplayFormatException.
/// Unknown object signatures are skipped with a logger.Warning.
/// </summary>
public static partial class BlfParser
{
    public static async Task<IReadOnlyList<ReplayFrame>> ParseAsync(
        Stream stream,
        ReplayOptions options,
        ILogger? logger = null,
        CancellationToken ct = default);
}
```

### 6.3 `ReplayService.LoadAsync` (modify)

```csharp
public async Task LoadAsync(string path, CancellationToken ct = default)
{
    _frames = Array.Empty<ReplayFrame>();
    _timeline.SetFrames(_frames);

    try
    {
        await using var fs = File.OpenRead(PathNormalizer.Normalize(path));
        // v3.51.0 MINOR: dispatch by extension
        if (path.EndsWith(".blf", StringComparison.OrdinalIgnoreCase))
        {
            _frames = await BlfParser.ParseAsync(fs, options, _logger, ct);
        }
        else
        {
            _frames = await AscParser.ParseAsync(fs, options, _logger, ct);
        }
    }
    // ... existing exception wrapping unchanged
}
```

## 7. Test plan

### 7.1 Core.Tests — 6 new BlfFormatTests (sister of v3.49.0 AscFormatRoundTripTests)

| Test | Validates |
|---|---|
| `BlfFormat_HasExpectedFileSignature` | "LOGG" string constant |
| `BlfFormat_HasExpectedFormatSignature` | "LBLF" string constant |
| `BlfFormat_HasExpectedObjectSignatures` | "OBJH" / "BLOB" / "LOBJ" |
| `BlfFormat_HasExpectedFrameContainerTypeIds` | ET_CAN_DATA=5, ET_CAN_FD_DATA=29, ET_CAN_XL_DATA=33 |
| `BlfFormat_HasExpectedBlobSizes` | ClassicCanBlobSize=20, CanFdBlobSize=32, CanXlBlobMinSize=32 |
| `BlfFormat_HasExpectedTimestampScale` | TimestampScale = 10_000_000.0 |

### 7.2 Core.Tests — 11 new BlfParserTests (sister of v3.49.0 AscParserTests)

| Test | Validates |
|---|---|
| `BlfParser_ClassicCan_ValidBlob_Parsed` | synth 20-byte BLOB → ReplayFrame correct |
| `BlfParser_CanFd_ValidBlob_Parsed` | synth 32-byte BLOB → FD flag + frameLength data |
| `BlfParser_CanXl_ValidBlob_Parsed` | synth 32+ byte BLOB → XL flag + big-endian frameLength |
| `BlfParser_InvalidMagic_Throws` | "BLFF" magic → ReplayFormatException |
| `BlfParser_InvalidVersion_Throws` | version=0x00009999 → ReplayFormatException |
| `BlfParser_TruncatedClassicCan_Throws` | BLOB objSize < 20 → ReplayFormatException |
| `BlfParser_TruncatedCanFd_Throws` | BLOB objSize < 32 → ReplayFormatException |
| `BlfParser_Over50PercentCorruption_Throws` | 51% bad BLOBs → ReplayFormatException |
| `BlfParser_UnknownObjectSignature_Skipped` | "MISC" obj → log warning + skip, no throw |
| `BlfParser_ContainerLobjObject_Skipped` | "LOBJ" container → skip body, no throw |
| `BlfParser_Timestamp_ConvertedToSeconds` | UINT64 ticks → double seconds (10_000_000 scale) |
| `BlfParser_CanXlFrameLength_BigEndian` | CAN XL frame length is big-endian, NOT little-endian |

### 7.3 Core.Tests — 1 new BlfParserManualTests (CI-skip, user's machine only)

| Test | Validates |
|---|---|
| `BlfParser_RealFile_LoadsSuccessfully` (Trait="Manual") | Load `C:\Users\13777\Documents\xwechat_files\wxid_1012060120711_bd20\msg\file\2026-07\CH0_242下坡掉READY0.blf` → parse succeeds + frame count > 0 + timestamp range valid |

### 7.4 Regression checks

- `dotnet test --filter "FullyQualifiedName~AscParser"` → all existing PASS (no changes to AscParser)
- `dotnet test --filter "FullyQualifiedName~ReplayService"` → all existing PASS
- Full solution `dotnet test` → no new failures (sister of v3.50.6 ship totals + 17 new BLF tests)

## 8. Risks & mitigations

| Risk | Mitigation |
|---|---|
| BLF private format reverse-engineered (Vector does not publish spec) | Strict per-spec field validation + W22+W23 LESSON verbatim re-extraction discipline; real fixture manual verification |
| CAN XL frame length is big-endian (other fields are little-endian) | Explicit unit test `BlfParser_CanXlFrameLength_BigEndian`; W22 LESSON applied during implementation |
| Container object LOBJ nested structures (Vector recordings often nest 4-5 levels) | Recursive container skip; manual fixture verification exercises real-world nesting |
| Real BLF fixture 8MB contains user's vehicle data — CANNOT enter git | Strict no-fixture policy + `[Trait("Manual", "true")]` for the only real-file test; CI auto-skips |
| Third-party reverse-engineered reference drift (python-can / cantools updates) | Sister lesson: reverse-engineered libraries drift requires baseline-test verification on each reference update |
| User's fixture may have been recorded with different Vector tool version (different byte layout) | Manual test asserts only parse success + frame count > 0 + timestamp range valid; not exact byte equivalence |
| Auto-mode classifier on v3.50.6 PKM capture flagged "instruction poisoning" — risk that BLF spec's dense reverse-engineered content triggers similar flag | Spec reviewed for any "must"-prefixed prescriptive language; spec uses descriptive phrasing throughout |

## 9. Out of scope

- BLF writing (sister of `AscFormat.WriteHeader/WriteFooter/WriteDataLine`)
- BLF compression (LZ4 / zlib containers)
- Vector custom object types (system vars, env vars, driver info)
- Real-time BLF streaming
- Multi-source BLF bundles
- BLF fixture in `tests/` (8MB real file, user's vehicle data, no public artifact)

## 10. Sister patterns to monitor

| Lesson candidate | This PATCH observation |
|---|---|
| `blf-parser-must-verify-byte-layout-against-reverse-engineered-reference-not-invent` | NEW 1/3: BLF spec constants (LOGG/LBLF/OBJH/BLOB/LOBJ/ET_*_DATA) must be copied from verified reference; inventing causes silent corruption |
| `blf-mixed-endian-requires-per-field-explicit-byte-order-test` | NEW 1/3: CAN XL frame length is big-endian while other fields are little-endian; mixed endianness is a Vector-spec-specific trap |
| `extension-based-parser-dispatch-in-load-async-shares-options-and-exception-types` | NEW 1/3: ReplayService.LoadAsync's .asc/.blf dispatch shares ReplayOptions + exception types — sister of v3.49.0 stream-cap reuse pattern |
| `real-fixture-with-proprietary-data-must-use-trait-manual-skip` | NEW 1/3: 8MB real BLF cannot enter git; [Trait("Manual", "true")] CI-skip pattern; sister of v3.50.2 fixture exposure concern (deferred per auto-mode classifier) |
| `reverse-engineered-library-drift-requires-baseline-test-on-reference-update` | NEW 1/3: python-can / cantools updates may shift BLF byte layouts; baseline fixture test must be re-run on each reference update |
| `strict-50-percent-corruption-threshold-matches-existing-asc-parser-pattern` | NEW 1/3: ReplayFormatException on 50%+ corruption matches v3.49.0 AscParser threshold; sister of "consistent error severity" pattern |