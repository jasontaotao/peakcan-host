# Release Notes v3.11.5 — CANoe Vector ASC v1.3 parser compatibility (PATCH)

**Released:** 2026-07-07
**Parent:** v3.11.4 PATCH (`ac48993`)
**Tag:** v3.11.5
**Branch:** `feature/v3-11-1-patch`

## Highlights

This PATCH fixes the AscParser to correctly parse CANoe-exported Vector ASC v1.3 files — the dominant CAN tool vendor's format. User reported: `C:\Users\13777\Desktop\Logging.asc` (CANoe v13 export, 11.5 MB) produced "ASC file has no parseable frames" because the parser rejected all 4 format tokens unique to Vector's export.

| Commit | Fix | Tests |
|--------|-----|-------|
| `9a5b253` | AscParser supports Vector ASC v1.3 (4 format gaps) | +6 |

**Test delta:** 1276 + 5 SKIP / 0 fail → **1282 + 5 SKIP / 0 fail** (+6 active tests)
**Code stats:** +80 / -10 (net +70 LoC in AscParser.cs: 4 gap fixes + scan-forward loop + `goto EndDataBytes`)

## 4 parser gaps closed

### Gap #1 — Trailing `x` on CAN ID
Vector convention: hex ID + `x` = extended-frame marker. CANoe writes `18FF60A2x` for 29-bit ID 0x18FF60A2.
Parser fix: strip trailing `x` from `tokens[2]` before hex-parse at `AscParser.cs:194`.

### Gap #2 — `d N` / `l N` DLC tokens
Vector convention: classic CAN DLC is `d 8` (two tokens), CAN FD DLC is `l 8` (two tokens + FD flag).
Parser fix: detect `d` / `l` at `tokens[3]` (with optional preceding `Rx`/`Tx` direction tokens), parse `tokens[N+1]` as the DLC, shift data-byte start to `tokens[N+2]`, set `FrameFlags.Fd` from `l`.

### Gap #3 — `Rx` / `Tx` direction tokens
Vector convention: direction markers appear between ID + DLC.
Parser fix: scan-forward loop at `AscParser.cs:212-241` skips `Rx`/`Tx` before parsing DLC. Plus `case "rx"` / `case "tx"` added to the flag-classification switch as defense-in-depth. Direction tracking is not yet surfaced in `ReplayFrame` (future PATCH); for now they prevent malformed-line rejection.

### Gap #4 — Trailing `Length = N BitCount = N ID = N` metadata
Vector convention: per-frame tail with bit-length + bit-count + decimal-ID appended after data bytes.
Parser fix: stop reading data bytes at the `Length` / `BitCount` / `ID` keyword OR any token containing `=`. The `Length=` marker is the natural end of the data section. Uses `goto EndDataBytes;` to break the data-byte for-loop cleanly.

## Tests

| Test | Asserts |
|------|---------|
| `Parse_CanoeExtendedFrameId_StripsTrailingX` (NEW, +1) | `18FF60A2x` → `id = 0x18FF60A2` |
| `Parse_CanoeClassicDlc_DToken` (NEW, +1) | `d 8` → `dlc = 8`, `FrameFlags.Fd` NOT set |
| `Parse_CanoeFdDlc_LToken_SetsFdFlag` (NEW, +1) | `l 8` → `dlc = 8`, `FrameFlags.Fd` IS set |
| `Parse_CanoeRxTx_DirectionToken_NotMalformed` (NEW, +1) | `Rx` / `Tx` not parsed as data bytes |
| `Parse_CanoeTrailingMetadata_LengthBitCountId_NotMalformed` (NEW, +1) | `Length = 270000 BitCount = 139 ID = 419389602x` tail ignored |
| `Parse_CanoeFormat_FullLine_All4Gaps_HandlesCleanly` (NEW, +1) | 4-gap combined line from real Logging.asc parses; 29-bit IDs padded correctly |

## Upgrade notes

No breaking changes:
- `AscParser.ParseAsync` signature unchanged.
- `ReplayFrame` fields unchanged (no Rx/Tx direction surfaced in this PATCH).
- Existing `RecordService.Convert.ToHexString` format (used in `RecordService.cs:307-313`) still parses identically — the 4 Vector-format extensions are additive.
- All 13 existing `AscParserTests` continue to pass (verified: 19/19 AscParser tests green, 1282/5/0 full suite).

## NEXT

- v3.11.6 PATCH — surface `Rx` / `Tx` direction in `ReplayFrame` if needed (deferred — no user demand yet)
- v3.12.0 MINOR — C2 ReplayViewModel god class split + H3/H6/M1-M13 review backlog closure