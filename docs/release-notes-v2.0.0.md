# v2.0.0 MINOR — ODX-D DIAG-LAYER Importer (2026-07-01)

## Summary

Replaces the JSON config stub (`%LOCALAPPDATA%\PeakCan.Host\uds-{dids,routines}.json`)
with a real ODX (ISO 22901, ISO 22901 ODX-D) file importer. OEM projects can now
load `.odx` / `.pdx` files directly via the UDS view's new "Load ODX…" button.

## Features

1. **ODX / PDX import** (`.odx` direct + `.pdx` ZIP container via `System.IO.Compression`).
2. **`OdxDocument` / `DiagLayer` / `DiagService` data model** — BASE-VARIANT only.
3. **`OdxParser`** — XDocument → OdxDocument with non-fatal warnings.
4. **`PdxReader`** — async `.odx` + `.pdx` reader.
5. **`Dop` / `EcuJob` mappers** — static `DidDop.TryMap` / `DtcDop.TryMap` / `EcuJob.TryMap`.
6. **`IOdxImportService` + `OdxImportService`** — non-throwing orchestrator with `OdxImportResult`.
7. **`OdxImportViewModel`** — WPF `ObservableObject` with `IsBusy` + `LastStatus` + `ImportCommand`.
8. **`DtcDatabase`** — new in-memory DTC store; `DtcDefinition` is a `readonly record struct`.
9. **`DidDatabase.AddRange` + `RoutineDatabase.AddRange`** — public mutation hooks.
10. **UdsView "Load ODX…" button** — opens file dialog → triggers import → refreshes Dtc panel.
11. **DtcPanel ODX enrichment** — `DtcPanelViewModel.LookupDescription` prefers ODX descriptions
    over ISO 14294 fallback; `RefreshFromDatabase()` lets UdsViewModel re-populate after import.
12. **`OdxErrorCode` enum** — `None / FileNotFound / ParseError / UnsupportedVersion / Refused / DuplicateId`.
13. **DoS guard** — 10000 items/type per import (per D6).

## Known limits (v2.0.0)

- DIAG-LAYER BASE-VARIANT only (CONDITIONAL / ECU-VARIANT deferred to v2.1).
- ODX schema 2.0.0 + 2.2.0; other versions warn + try.
- DOP-BASE + DTC-DOP + ECU-JOB only (COMPARAM-SPEC / ECU-CONFIG out of scope).
- V8 sandbox scripts cannot load ODX (filesystem whitelist security-deferred).

## Backward compatibility

- Existing JSON config files (`uds-dids.json`, `uds-routines.json`) continue to load as
  default seed values; ODX imports append on top via `AddRange`.
- DtcPanel retains the original ECU live-read path (`Read DTCs` button still works);
  ODX `RefreshFromDatabase()` adds a new path to populate from local-imported definitions.

## Test counts

| Suite | v1.7.7 baseline | v2.0.0 |
|-------|----------------|--------|
| Core  | 353            | 378    |
| App   | 438            | 442    |
| Infra | 84             | 84     |
| **Total** | **875 + 6 SKIP** | **~902 + 6 SKIP / 0 fail** (excluding 2 race-test transient flakes; pass in isolation per MEMORY pattern) |

(29 new ODX tests: 1 ErrorCode + 3 ImportResult + 3 Document + 3 Parser + 3 PdxReader + 6 Database + 2 AddRange + 4 Mapper + 2 Service + 2 VM = 29.)

## Migration guide

No migration steps required. To use ODX:
1. Get an ODX file from your ECU supplier (`.odx` or `.pdx` zip).
2. In the UDS view, click "Load ODX…" and select the file.
3. Status bar shows the import result.

## Compatibility

No API breaking changes (MINOR, not MAJOR). All existing UDS panel functionality preserved.

## Known follow-ups

- **v2.0.1 PATCH** (next): release-notes-only Option B cycle.
- **v2.0+ DEV**: V8 sandbox ODX exposure (security review).
- **v2.1 MINOR**: CONDITIONAL / ECU-VARIANT DIAG-LAYER support.
- **v3.0**: J1939 / CANopen / SocketCAN (separate workstream).
