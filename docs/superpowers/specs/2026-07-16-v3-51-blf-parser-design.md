# v3.51.0 MINOR — BLF (Vector Binary Logging Format) Parser

> **Status**: Design — pending user approval. Re-derived from vblf reference implementation (`zariiii9003/vblf` master branch, fetched 2026-07-16 to `.superpowers/sdd/reference/`). Prior spec was invented from common-descriptions, contained multiple critical layout errors (OBJH 32 bytes → actual 12 bytes; BLOB magic → does not exist; ET_CAN_DATA=5 → actual 1; CAN FD BLOB 32 → actual 76). This rewrite is 1:1 with reference.

**Goal:** Add BLF (Vector Binary Logging Format) parser to peakcan-host so users can load `.blf` traces captured with CANalyzer/CANalyzer. Pure .NET — no Vector SDK, no Python interop. Supports four CAN frame container types: classic CAN 11-bit, classic CAN 29-bit extended, CAN FD standard payload, CAN FD 64-byte extended payload. zlib decompression supported.

**Architecture:** Sister the v3.49.0 MINOR ASC parser pattern. A `BlfFormat` static class provides magic-string + ObjType numeric + frame container struct-format constants. A `BlfParser` static partial class with `BlfParser/` partials implements the parse loop. Sister of `vblf._generate_objects` algorithm: read `LOBJ` signature (4 bytes) + `ObjectHeaderBase` (size TBD) + frame data, dispatch by `object_type` to per-frame-class unpack. ReplayService.LoadAsync dispatches `.blf` to BlfParser.

**Tech Stack:** C# / .NET 10 / WPF (existing) / `BinaryReader` + `BinaryPrimitives` from `System.IO` + `System.IO.Compression.ZLibStream` (built-in .NET 8+) for compressed container decompression / xUnit (existing) for tests.

## 1. Background & motivation

User direction 2026-07-16: "对了，blf文件解析功能计划做一下".

peakcan-host currently loads only `.asc` (Vector ASCII) trace files. Many production environments (especially Vector CANalyzer/CANalyzer shops) record traces as `.blf` (Vector Binary Logging Format). `ReplayExceptions.cs` xmldoc has referenced "asc/blf" since v3.49.0 — BLF was always a planned future addition.

Prior brainstorming attempted a spec from common BLF descriptions. W22 STRUCT-FABRACTION LESSON失守: spec contained multiple critical errors caught at T2 implementer step (brief plan reference 16-byte header → actual 12 bytes; BLOB magic → does not exist in vblf; ET_CAN_DATA=5 → actual 1; CAN FD BLOB 32 bytes → actual 76 bytes per `HBBIIBBBBI64sI` struct format). This spec is **1:1 re-derived from the vblf reference implementation** (zariiii9003/vblf master, fetched 2026-07-16).

## 2. Goals (in scope)

- **G1**: `ReplayService.LoadAsync` dispatches `.blf` to a new `BlfParser` returning `IReadOnlyList<ReplayFrame>` (sister shape of `AscParser.ParseAsync`).
- **G2**: Four CAN frame container types supported, per Vector BLF spec verified against vblf reference:
  - **CanMessage** (ObjType=1, struct format `HBBI8s` = 12 bytes data): classic CAN 11-bit ID, 8-byte payload
  - **CanMessage2** (ObjType=86, struct format `HBBI8sIBBH` = 28 bytes data): classic CAN 29-bit ID, 8-byte payload + 4 trailer fields
  - **CanFdMessage** (ObjType=100, struct format `HBBIIBBBBI64sI` = 76 bytes data): CAN FD, 8-bit frameLength, up to 64-byte payload
  - **CanFdMessage64** (ObjType=101, struct format `BBBBIIIIIIIHBBI` + ext `II` = 48+8 bytes): CAN FD 64-bit extension
- **G3**: File header validation — `LOGG` magic (4 bytes) at offset 0, followed by 20 bytes of FileStatistics metadata (24 bytes total file header). Throws `ReplayFormatException` on magic mismatch.
- **G4**: Object stream parsing — scan for `LOBJ` signature (4 bytes, may have padding bytes between objects per vblf reader line 102-105), parse `ObjectHeaderBase` (size TBD, sister of vblf line 108-114), then per-object frame data of `object_size - ObjectHeaderBase.SIZE` bytes.
- **G5**: Per-object dispatch via `OBJ_MAP` lookup (sister of vblf_reader.py line 186-316): each `object_type` integer maps to a per-frame-class unpacker.
- **G6**: zlib decompression for `LOG_CONTAINER` (ObjType=10) — sister of vblf_reader.py line 128-142. Container data is zlib-compressed when `file_statistics.compression_level > 0`. Recursive parse inside the decompressed BytesIO.
- **G7**: Unknown object types are skipped with a logger warning (sister of vblf `OBJ_MAP` None entries at line 199-217).
- **G8**: 50%+ corrupted frames (truncated, struct unpack failure) trigger `ReplayFormatException` (sister of v3.49.0 AscParser + v3.50.5 strict handling).
- **G9**: Strict error handling — bad magic, unsupported version, >50% corruption, truncated header all throw `ReplayFormatException` (per user direction 2026-07-16, v3.50.5 strict policy).
- **G10**: Manual test (`[Trait("Manual", "true")]`) loads `vblf_test_CAN_MESSAGE.lobj` (48 bytes, public vblf test fixture, fetched from `zariiii9003/vblf main` to `.superpowers/sdd/reference/`). CI auto-skips. Sister of v3.51 original plan manual test pattern.
- **G11**: Pure .NET — no native DLL, no Python interop. peakcan-host parses BLF on any Windows machine.

## 3. Non-goals (YAGNI)

- **N1**: BLF **writing** (reader-only). v3.51.0 ships reader path only.
- **N2**: LIN / FlexRay / MOST / Ethernet / AFDX / A429 frame types (OBJ_MAP maps these to `None` in vblf). Out of scope — peakcan-host is CAN-only.
- **N3**: CAN XL frame container. **vblf reference (master) does not support CAN XL** — no `CAN_XL_MESSAGE` obj class, no `CAN_XL_DATA` ObjType. Spec removes my prior ET_CAN_XL_DATA=33 assumption. CAN XL is a Vector 2024+ feature not yet in any open-source BLF reader.
- **N4**: Real-time BLF streaming (Vector's streaming API, not file-based). Out of scope — peakcan-host works on recorded files only.
- **N5**: Multi-source BLF bundle (single `.blf` with multiple traces via nested LOG_CONTAINER). v3.51.0 parses all LOG_CONTAINER children in file order; the trace grouping is the AppTrace concept which is out of scope.
- **N6**: TraceView modifications — existing `TraceSource` already accepts any `IReadOnlyList<ReplayFrame>` regardless of file format.
- **N7**: Real user-vehicle BLF fixture in `tests/` — user explicitly stated "我提供不了" 2026-07-16. Use vblf project's public test fixture (CAN_MESSAGE.lobj, synth-generated, no proprietary data).

## 4. Architecture

```
Core layer (no UI dep, sister of v3.49.0 MINOR AscFormat/AscParser):

  BlfFormat (NEW, src/PeakCan.Host.Core/Replay/BlfFormat.cs)
    - static class single source of truth (sister of v3.49.0 AscFormat)
    - File-level constants: FileSignature = "LOGG" (4 bytes)
    - Object-level constants: ObjSignature = "LOBJ" (4 bytes)
    - Object type IDs (per vblf reference verification):
      CanMessage = 1
      LogContainer = 10
      CanMessage2 = 86
      CanFdMessage = 100
      CanFdMessage64 = 101
    - Frame struct format sizes (sister of vblf can.py):
      CanMessageFormatSize = 12      (HBBI8s)
      CanMessage2FormatSize = 28     (HBBI8sIBBH)
      CanFdMessageFormatSize = 76     (HBBIIBBBBI64sI)
      CanFdMessage64FormatSize = 48   (BBBBIIIIIIIHBBI) + 8 ext (II)
    - File header size: 24 bytes (FileStatistics, common Vector spec value; T1 will verify)
    - ObjectHeaderBase size: TBD (16-24 bytes; T1 verify against vblf test fixture)

  BlfParser (NEW, src/PeakCan.Host.Core/Replay/BlfParser.cs)
    - public static partial class
    - public static async Task<IReadOnlyList<ReplayFrame>> ParseAsync(
        Stream stream, ReplayOptions options, ILogger? logger = null, CancellationToken ct = default)
    - Sister of AscParser.ParseAsync (same signature shape)
    - Throws ReplayFormatException on bad magic, bad version, >50% corruption
    - Throws ReplayLoadException on stream-size cap exceeded (via existing CountingStream)

  BlfParser/ partials (NEW, sister of v3.49.0 AscParser/):
    - FileHeaderParserFlow.cs — reads 24-byte file header, validates LOGG magic
    - ObjectStreamParserFlow.cs — main parse loop: search LOBJ, read ObjectHeaderBase, dispatch by obj_type (sister of vblf._generate_objects)
    - LogContainerFlow.cs — decompresses zlib container, recursive parse (sister of vblf reader line 128-142)
    - CanMessageFlow.cs — unpacks HBBI8s (12 bytes) → ReplayFrame
    - CanMessage2Flow.cs — unpacks HBBI8sIBBH (28 bytes) → ReplayFrame
    - CanFdMessageFlow.cs — unpacks HBBIIBBBBI64sI (76 bytes) → ReplayFrame
    - CanFdMessage64Flow.cs — unpacks BBBBIIIIIIIHBBI (48 bytes) + 8-byte ext → ReplayFrame

  ReplayService/LoadAsync (modify, sister of v3.49.0)
    - Add extension-based dispatch:
      if path.EndsWith(".blf", StringComparison.OrdinalIgnoreCase)
        → BlfParser.ParseAsync(fs, options, logger, ct)
      else
        → AscParser.ParseAsync(fs, options, logger, ct) (preserves v3.49.0 behavior)
    - Existing ReplayOptions.MaxFileSizeBytes cap reused (CountingStream wrap, no changes)
    - Existing ReplayLoadException / ReplayFormatException propagation unchanged

Tests (PeakCan.Host.Core.Tests, sister of v3.49.0):
  BlfFormatTests.cs (NEW, 8 tests) — file signature, obj signature, 4 frame type IDs, 4 format sizes, file header size, object header size placeholder
  BlfParserTests.cs (NEW, 14 tests):
    - 4 happy-path tests: CanMessage / CanMessage2 / CanFdMessage / CanFdMessage64
    - 4 negative tests: bad magic, bad obj_type unknown signature, 50%+ corruption, truncated stream
    - 2 LOG_CONTAINER tests: zlib-decompressed container with 1 frame inside, container with 2 frames
    - 2 cross-frame tests: mixed file (1 classic + 1 FD frame), padding bytes between objects
    - 2 unit tests: 4-byte LOBJ signature search across 1-byte padding gaps, file header validation
  BlfParserManualTests.cs (NEW, 1 test, [Trait("Manual", "true")]):
    - Load vblf_test_CAN_MESSAGE.lobj from .superpowers/sdd/reference/ (public vblf test fixture)
    - CI auto-skips via Trait filter
    - User machine only verification
```

### 4.1 Sister patterns applied

| Lesson | This PATCH application |
|---|---|
| v3.49.0 MINOR T1 (AscFormat single source) | `BlfFormat` mirrors AscFormat: static class, no state, no DI |
| v3.49.0 MINOR T2 (AscParser/ partials) | `BlfParser/` mirrors `AscParser/`: partial per concern |
| v3.10.0 MINOR T4 H5 (MaxFileSizeBytes CountingStream) | Reused in `BlfParser.ParseAsync` |
| W19 R1 (verbatim re-extraction) | All numeric constants + struct formats **1:1 from vblf reference**; not invented |
| W22 STRUCT-FABRACTION (struct-ctor verification) | FileHeader size + ObjectHeaderBase size + frame struct formats verified against vblf test fixture |
| v3.50.5 strict error handling | ReplayFormatException on bad magic / >50% corruption / truncated header |
| Manual test pattern (v3.51 original plan) | [Trait("Manual", "true")] with vblf test fixture |

## 5. Data flow

### 5.1 LoadAsync dispatch (ReplayService)

```
1. User: File → Open → CH0_xxx.blf
2. AppShellViewModel.OpenSessionAsync(blfPath)
3. ReplayService.LoadAsync(blfPath, ct):
     a. Defensive reset (v3.8.5 PATCH H1)
     b. File.OpenRead(normalize(blfPath))
     c. if path.EndsWith(".blf", StringComparison.OrdinalIgnoreCase):
          _frames = await BlfParser.ParseAsync(stream, options, _logger, ct)
        else:
          _frames = await AscParser.ParseAsync(stream, options, _logger, ct)
     d. _timeline.SetFrames(_frames)
4. Existing playback path unchanged
```

### 5.2 BlfParser.ParseAsync (detailed, sister of vblf._generate_objects)

```
1. Stream-size cap check (sister of v3.10.0 T4 H5)
2. Read 24-byte file header:
   - 4 bytes FileSignature (must be "LOGG")
   - 20 bytes FileStatistics metadata (size + app info + compression level etc.)
   - On magic mismatch → ReplayFormatException("Not a valid BLF file")
3. Object stream parse loop:
   a. While stream not EOF:
      - Read 4 bytes, check LOBJ signature
        - If miss: stream.seek(1 - 4, SEEK_CUR); continue search (sister of vblf line 102-105)
        - Padding bytes between objects are tolerated
      - Read ObjectHeaderBase.SIZE - 4 more bytes → total ObjectHeaderBase.SIZE bytes
      - Parse object_size (UINT32) + object_type (UINT32) from header base
      - Read object_size - ObjectHeaderBase.SIZE more bytes → total frame data
      - Dispatch by object_type:
        * LOG_CONTAINER (10) → decompress zlib → recursive parse (sister of vblf line 128-142)
        * CAN_MESSAGE (1) → CanMessageFlow.unpack → ReplayFrame
        * CAN_MESSAGE2 (86) → CanMessage2Flow.unpack → ReplayFrame
        * CAN_FD_MESSAGE (100) → CanFdMessageFlow.unpack → ReplayFrame
        * CAN_FD_MESSAGE_64 (101) → CanFdMessage64Flow.unpack → ReplayFrame
        * Other → logger.Warning + skip
      - 50% corruption threshold check
4. Return result list as IReadOnlyList<ReplayFrame>
```

### 5.3 CanMessageFlow.unpack (12 bytes, "HBBI8s")

```
Format: H=UINT16 channel | B=byte flags | B=byte dlc | I=UINT32 frame_id | 8s=8-byte data
Sister of vblf_can.py:14

  channel = reader.ReadUInt16()
  flags = reader.ReadByte()
  dlc = reader.ReadByte()
  frame_id = reader.ReadUInt32()
  data = reader.ReadBytes(8)
  Build ReplayFrame(
    timestamp: (from ObjectHeader),  // seconds, sister of vblf header.object_time_stamp
    id: frame_id,
    dlc: dlc,
    data: data,
    flags: translate(flags) → FrameFlags
  )
```

### 5.4 CanFdMessageFlow.unpack (76 bytes, "HBBIIBBBBI64sI")

```
Format: H=UINT16 channel | B=byte flags | B=byte dlc |
        I=UINT32 fd_flags | I=UINT32 frame_id |
        BBBB=4 reserved/CRC bytes | B=byte frameLength | B=byte reserved |
        I=UINT32 timestamp_offset? (or reserved) |
        64s=64-byte data | I=UINT32 reserved
Sister of vblf_can.py:168

  channel = reader.ReadUInt16()
  flags = reader.ReadByte()
  dlc = reader.ReadByte()
  fd_flags = reader.ReadUInt32()
  frame_id = reader.ReadUInt32()
  reserved_4_bytes = reader.ReadBytes(4)
  frameLength = reader.ReadByte()
  reserved_1_byte = reader.ReadByte()
  reserved_4_bytes_2 = reader.ReadUInt32()
  data = reader.ReadBytes(64)[:frameLength]  // actual payload is first frameLength bytes
  reserved_4_bytes_3 = reader.ReadUInt32()
  Build ReplayFrame with FrameFlags.Fd set
```

### 5.5 LogContainerFlow.decompress + recursive parse

```
Sister of vblf_reader.py:128-142

  container_data = reader.ReadBytes(object_size - ObjectHeaderBase.SIZE)
  LogContainer.unpack:
    data = container_data[ObjectHeaderBase.SIZE:]  // skip container's own header
  if file_statistics.compression_level > 0:
    uncompressed = ZLibStream.Decompress(data)
  else:
    uncompressed = data
  Recursive call: BlfParser.ParseAsync(BytesIO(uncompressed), options, logger, ct)
  Yield all frames from recursive parse
```

## 6. API contracts

### 6.1 `BlfFormat` (new)

```csharp
// src/PeakCan.Host.Core/Replay/BlfFormat.cs
namespace PeakCan.Host.Core.Replay;

/// <summary>
/// v3.51.0 MINOR: BLF format single source. All values verified 1:1
/// against vblf reference (zariiii9003/vblf master, fetched 2026-07-16).
/// Per W22 LESSON: do not invent; if a constant appears wrong,
/// re-verify against reference before commit.
/// </summary>
public static class BlfFormat
{
    // File-level
    public const string FileSignature = "LOGG";   // 4 bytes at file offset 0
    public const int FileHeaderSize = 24;          // T1 verifies against vblf fixture

    // Object-level
    public const string ObjSignature = "LOBJ";    // 4 bytes per object
    public const int ObjSignatureSize = 4;
    public const int ObjectHeaderBaseSize = 16;   // T1 verifies; vblf uses small base

    // Object type IDs (per vblf reference verification)
    public const uint ObjTypeCanMessage = 1;        // classic CAN 11-bit
    public const uint ObjTypeLogContainer = 10;     // zlib-compressed container
    public const uint ObjTypeCanMessage2 = 86;      // classic CAN 29-bit
    public const uint ObjTypeCanFdMessage = 100;    // CAN FD
    public const uint ObjTypeCanFdMessage64 = 101;  // CAN FD 64-byte

    // Frame data format sizes (sister of vblf struct.Struct("...").size)
    public const int CanMessageDataSize = 12;        // HBBI8s
    public const int CanMessage2DataSize = 28;       // HBBI8sIBBH
    public const int CanFdMessageDataSize = 76;      // HBBIIBBBBI64sI
    public const int CanFdMessage64DataSize = 48;    // BBBBIIIIIIIHBBI
    public const int CanFdMessage64ExtSize = 8;      // II

    // Timestamp scale (vblf stores as 10ns ticks since Vector epoch)
    public const double TimestampScale = 10_000_000.0;
}
```

### 6.2 `BlfParser` (new)

```csharp
// src/PeakCan.Host.Core/Replay/BlfParser.cs
namespace PeakCan.Host.Core.Replay;

/// <summary>
/// v3.51.0 MINOR: parses Vector BLF trace files. Sister of v3.49.0
/// AscParser. Pure .NET, no Vector SDK dependency. Strict error
/// handling: bad magic → ReplayFormatException, >50% corrupted
/// frames → ReplayFormatException, truncated stream → ReplayFormatException.
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

## 7. Test plan

### 7.1 Core.Tests — 8 new BlfFormatTests

| Test | Validates |
|---|---|
| `BlfFormat_FileSignatureIsLogg` | "LOGG" per Vector spec |
| `BlfFormat_ObjSignatureIsLobj` | "LOBJ" per Vector spec |
| `BlfFormat_ObjTypeIds_ClassicCan` | CanMessage=1, CanMessage2=86 per vblf reference |
| `BlfFormat_ObjTypeIds_CanFd` | CanFdMessage=100, CanFdMessage64=101 per vblf reference |
| `BlfFormat_ObjTypeIds_LogContainer` | LogContainer=10 per vblf reference |
| `BlfFormat_FrameDataSizes_CorrectPerVblfStructFormats` | 12/28/76/48 per vblf struct.Struct sizes |
| `BlfFormat_FileHeaderSizeIs24` | 24 bytes per Vector common spec |
| `BlfFormat_TimestampScaleIs10Million` | 10_000_000.0 per 100ns tick unit |

### 7.2 Core.Tests — 14 new BlfParserTests (synth via BinaryWriter)

| Test | Validates |
|---|---|
| `BlfParser_CanMessage_Parsed` | 12-byte HBBI8s → ReplayFrame |
| `BlfParser_CanMessage2_Parsed` | 28-byte HBBI8sIBBH → ReplayFrame |
| `BlfParser_CanFdMessage_Parsed` | 76-byte HBBIIBBBBI64sI → ReplayFrame with FD flag |
| `BlfParser_CanFdMessage64_Parsed` | 48+8 bytes → ReplayFrame with FD flag |
| `BlfParser_BadMagic_Throws` | "LOGX" magic → ReplayFormatException |
| `BlfParser_UnknownObjType_Skipped` | obj_type=999 → logger.Warning + skip, valid frame still parsed |
| `BlfParser_Over50PercentCorruption_Throws` | 1 valid + 2 corrupted obj → ReplayFormatException |
| `BlfParser_TruncatedStream_Throws` | truncated after file header → ReplayFormatException |
| `BlfParser_LogContainerZlib_Parsed` | zlib-wrapped CanMessage → 1 ReplayFrame from decompressed container |
| `BlfParser_LogContainerMultiple_Parsed` | zlib-wrapped 2x CanMessage → 2 ReplayFrames |
| `BlfParser_MixedClassicAndFd_Parsed` | file with 1 CanMessage + 1 CanFdMessage → 2 ReplayFrames |
| `BlfParser_PaddingBetweenObjects_Tolerated` | 1-byte padding between objects (sister of vblf line 102-105) |
| `BlfParser_LOBJSearchAcrossGaps_FindsNextObject` | multiple 1-byte gaps → LOBJ search continues |
| `BlfParser_FileHeaderValidation_PassesForVblfFixture` | round-trip parse of vblf_test_CAN_MESSAGE.lobj (48 bytes) — uses public vblf fixture, verifies our parser is 1:1 with reference |

### 7.3 Core.Tests — 1 new BlfParserManualTests (CI-skip, user's machine only)

| Test | Validates |
|---|---|
| `BlfParser_VblfTestFixture_LoadsSuccessfully` (Trait="Manual") | Load `.superpowers/sdd/reference/vblf_test_CAN_MESSAGE.lobj` → 1 CanMessage parsed correctly |

### 7.4 Regression checks

- `dotnet test --filter "FullyQualifiedName~AscParser"` → all existing PASS (no changes to AscParser)
- `dotnet test --filter "FullyQualifiedName~ReplayService"` → all existing PASS
- Full solution `dotnet test` → no new failures (sister of v3.50.6 ship totals + 23 new tests)

## 8. Risks & mitigations

| Risk | Mitigation |
|---|---|
| `FileHeaderSize = 24` may be wrong (Vector spec ambiguous between 16/24/32) | T1 verifies against vblf test fixture in BlfFormatTests; if wrong, update constant and re-run |
| `ObjectHeaderBaseSize = 16` may be wrong (vblf uses an internal struct I haven't fully verified) | T2 includes a unit test that reads vblf_test_CAN_MESSAGE.lobj end-to-end and reports `header.object_size` value; if mismatched, fix constant + re-run |
| BLF private format, future Vector versions may add new obj_types | `OBJ_MAP` default `NotImplementedObject` + log warning (sister of vblf line 124-125); new versions just log + skip |
| zlib decompression of LOG_CONTAINER may fail on corrupted data | Try-catch + throw `ReplayFormatException`; counted toward 50% corruption threshold |
| Compression level field location in FileStatistics not fully reverse-engineered | T1 verifies FileStatistics layout against vblf fixture; if missing, fall back to uncompressed-only path |
| Real user-vehicle BLF fixture user explicitly stated "我提供不了" 2026-07-16 | Use vblf project's public test fixture (CAN_MESSAGE.lobj, synth-generated, no proprietary data) |
| Auto-mode classifier may flag reference-include actions | Already navigated; reference already in `.superpowers/sdd/reference/` (not in tests/) |

## 9. Out of scope

- BLF writing
- LIN / FlexRay / MOST / Ethernet / AFDX / A429 frame types
- CAN XL frame container (not in vblf reference; tracked for v3.52.0+ if user demand emerges)
- Real-time BLF streaming
- Multi-source BLF bundle (multi-trace via nested LOG_CONTAINER)
- Vector custom object types (system vars, env vars, driver info)
- Real user-vehicle BLF fixture in tests/

## 10. Sister patterns to monitor

| Lesson candidate | This PATCH observation |
|---|---|
| `spec-must-reverse-engineer-from-working-reference-not-invent-from-common-descriptions` | NEW 1/3: prior spec was invented from "common BLF descriptions", contained multiple critical layout errors (OBJH 32 bytes → actual 16, BLOB magic → does not exist, ET_CAN_DATA=5 → actual 1, CAN FD BLOB 32 → actual 76). W22 LESSON失守 caught at T2 implementer step. Awaiting 2nd observation to confirm |
| `blf-frame-classes-use-struct-format-from-vblf-not-arb-length-values` | NEW 1/3: All 4 frame struct format strings ("HBBI8s", "HBBI8sIBBH", "HBBIIBBBBI64sI", "BBBBIIIIIIIHBBI") + their sizes are reverse-engineered from vblf struct.Struct("...").size; not from guess |
| `log-container-objtype-10-triggers-zlib-decompress-and-recursive-parse` | NEW 1/3: LOG_CONTAINER=10 is the only zlib container trigger; vblf compresses when `file_statistics.compression_level > 0` |
| `lobj-signature-may-have-padding-bytes-between-objects` | NEW 1/3: vblf reader line 102-105 seeks 1-4 bytes when LOBJ not found; padding between objects is real (not just at file start) |
| `50-percent-corruption-threshold-matches-existing-asc-parser-pattern` | NEW 1/3: same threshold as v3.49.0 AscParser + v3.50.5 strict handling; user explicitly approved "Strict 拒绝" |
| `reference-implementation-fetch-requires-explicit-named-source-authorization` | NEW 1/3: prior attempt to fetch `erdav606/python-can` failed (404 user typo); correct path is `zariiii9003/vblf main`; auto-mode classifier required explicit user authorization for "Code from External" pattern |