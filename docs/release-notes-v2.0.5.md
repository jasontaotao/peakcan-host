# v2.0.5 PATCH — DID LengthBytes from POS-RESPONSE chain (2026-07-02)

## Summary

Closes v2.0.4 PATCH known limit: DID `LengthBytes=0` for all ODX imports.
v2.0.5 walks the **POS-RESPONSE chain** to resolve each DID's actual data
length from its DATA-OBJECT-PROP / DOP `DIAG-CODED-TYPE BIT-LENGTH`.
Real file check: CellVolt_JG_Read (DID 0x0102) now correctly reports
400 bytes (was 0 in v2.0.4).

## Resolution chain

```
DID id (0x0102)
  ← REQUEST id (_444)        [via DIAG-SERVICE _447.REQUEST-REF]
  → POS-RESPONSE id (_445)   [via DIAG-SERVICE _447.POS-RESPONSE-REF]
  → POS-RESPONSE.PARAMS       [SEMANTIC="DATA" PARAMs, 8 in this case]
  → PARAM.DOP-REF (_210, _141)
  → DATA-OBJECT-PROP / DOP
  → DIAG-CODED-TYPE > BIT-LENGTH
  → sum bits, round up to bytes
```

## Items (1)

1. **`RequestBasedMappers.ExtractDidLengths` (new method)** (RequestBasedMappers.cs)
   - Index DATA-OBJECT-PROP / DOP id → BIT-LENGTH
   - Index REQUEST id → DID id (filter 0x22/0x2E)
   - Index POS-RESPONSE id → element
   - Walk DIAG-SERVICE: REQUEST-REF → DID, POS-RESPONSE-REF → POS-RESPONSE,
     sum SEMANTIC="DATA" PARAM DOP bit-lengths → bytes
   - Take max-bytes across all POS-RESPONSE-REFs for the same DID
   - **Returns the full response body length** (after SID echo + DID
     id echo, which are NOT counted because they're SERVICE-ID + ID
     SEMANTIC, not DATA SEMANTIC).

2. **OdxImportService threads lengths into DidDefinition** (OdxImportService.cs)
   - Calls `ExtractDidLengths(xdoc, ns)` once per document
   - Includes length in Description field: "DID 0x0102 (R, 400B)"

## Real file verification

| DID (id) | SHORT-NAME | v2.0.4 | v2.0.5 |
|----------|------------|--------|--------|
| 0x0102 (258) | CellVolt_JG_Read | 0 bytes | **400 bytes** (2×1456 + 6×48 bits) |

## Test counts

| Suite | v2.0.4 | v2.0.5 | Δ |
|-------|--------|--------|---|
| Core  | 384    | 384    | 0 |
| App   | 444    | 445    | +1 (`RealFile_ImportAsync_PopulatesDidLengthBytes`) |
| Infra | 84     | 84     | 0 |
| **Total** | **912 + 6 SKIP** | **913 + 6 SKIP** | +1 |

## Files changed (3)

- `src/PeakCan.Host.Core/Uds/Odx/RequestBasedMappers.cs` (M: +100 lines for ExtractDidLengths + ReadBitLength)
- `src/PeakCan.Host.App/Services/OdxImportService.cs` (M: +5 lines for length threading)
- `tests/.../ViewModels/Uds/OdxImportServiceRealFileTests.cs` (M: +30 lines for new test)
- `docs/user-manual.html` §11.5 (M: removed "LengthBytes=0" caveat)
- `docs/release-notes-v2.0.5.md` (+)

## Pre-ship review

- 0C / 0H / 0M / 0L (self-review + 1 new real-file test; no code-reviewer
  per skip-trivial-fixes pattern).

## Ship method

Tier 3 fallback 10-of-10 (continued network outage).
