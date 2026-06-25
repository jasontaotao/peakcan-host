# Release Notes — PeakCan Host v1.1.0

**Date:** 2026-06-25

## Summary

v1.1.0 closes two gaps from the v0.10.1 + merged UDS work: (1) the
`UdsViewModel.SecurityAccessAsync` `NotImplementedException` is replaced with
a DI-injected `IKeyDerivationAlgorithm` (default `PlaceholderKeyAlgorithm`
that emits a clear configuration hint), and (2) DID and Routine definitions
gain JSON-loadable databases (`uds-dids.json` / `uds-routines.json`) under
`%APPDATA%\PeakCan.Host\` with graceful fallback to built-in defaults on
missing or malformed files.

## New Features

- **`IKeyDerivationAlgorithm` abstraction** — `UdsClient` gains a 3-arg
  constructor accepting an `IKeyDerivationAlgorithm`. A new public overload
  `SecurityAccessAsync(byte requestLevel, CancellationToken ct = default)`
  performs the full RequestSeed → ComputeKey → SendKey handshake. Default
  registration is `PlaceholderKeyAlgorithm`, which throws
  `KeyAlgorithmNotConfiguredException(securityLevel)` with an actionable
  message and never logs seed bytes (C-2 fix preserved).
- **`DidDatabase`** — 5 built-in defaults (VIN 0xF190, ECU SW version
  0xF184, ECU HW version 0xF191, Part Number 0xF187, Supplier ID 0xF18A)
  + JSON load from `%APPDATA%\PeakCan.Host\uds-dids.json`. User entries
  with matching IDs override built-ins; non-matching entries are appended.
- **`RoutineDatabase`** — 100% OEM-defined; loads from
  `%APPDATA%\PeakCan.Host\uds-routines.json`. Empty list when no file.
- **`HexUshortJsonConverter`** — shared JSON converter accepting UDS
  16-bit ids in decimal, `"0x..."`, or `"..."` (bare hex) forms.
- **Graceful JSON fallback** — missing or malformed JSON does NOT throw;
  the UI remains usable and the logger records an Information / Warning.

## Bug Fixes

- **`UdsViewModel.SecurityAccessAsync` `NotImplementedException`** —
  removed. The new path uses the KeyProvider-aware overload and surfaces a
  useful hint when no OEM algorithm is registered. Fail-fast: the new
  overload throws `InvalidOperationException` synchronously (before any
  ECU frame is sent) when `UdsClient` was constructed without an
  `IKeyDerivationAlgorithm`, eliminating the previous "RequestSeed before
  throw" half-handshake.
- **`UdsViewModel.SecurityAccessAsync` seed-byte logging** — strengthened.
  Previously the seed length was logged in plaintext-friendly form
  ("Received seed (4 bytes) — redacted"). The new overload encapsulates
  the RequestSeed leg internally and never exposes seed bytes to the VM,
  so no log entry mentions the seed at all.

## Test Results

- **477 pass + 6 SKIP + 0 fail** (Core 207 + App 196 + Infrastructure 74)
- 6 SKIP: 2 hardware-dependent, 1 flaky background service, 3 hardware-dependent App tests
- Test count delta from v0.10.1 (~407): +70 new tests covering KeyProvider
  (Tasks A–D) + DID/Routine databases (Tasks E–F) + UdsViewModel fix (Task G)
  + DI factory (Task H).

## Commits Since v0.10.1

```
e5692e7 feat(core): add KeyAlgorithmNotConfiguredException for OEM key algo config
2de7426 feat(core): add IKeyDerivationAlgorithm interface + FakeKeyDerivationAlgorithm test double
3edeb88 feat(core): add PlaceholderKeyAlgorithm default DI implementation
d828ae2 feat(core): add UdsClient ctor + SecurityAccessAsync(byte, CancellationToken) overload
c6749b3 feat(core): add DidDefinition + DidDatabase with built-in defaults + JSON load
9027f52 feat(core): add RoutineDefinition + RoutineDatabase with JSON load
c155a0a fix(uds): replace NotImplementedException in SecurityAccess with KeyProvider call
1ad7687 feat(app): register KeyProvider + DID/Routine databases; switch UdsClient to factory
f21988c docs(plan): add v1.1.0 UDS UI + SecurityAccess KeyProvider implementation plan
8d8a98d docs(spec): add v1.1.0 UDS UI + SecurityAccess KeyProvider design
```

## Known Limitations / v1.2 Backlog

- 4-panel orchestrator refactor (`SessionPanelViewModel` / `DidPanelViewModel`
  / `RoutinePanelViewModel` / `DtcPanelViewModel`) is deferred to v1.2.
- `UdsView.xaml` polish (replace free-text DID / Routine ID inputs with
  DataGrids bound to `DidDatabase` / `RoutineDatabase`) is deferred to v1.2.
- OEM-specific key algorithms remain out of scope (per spec non-goal N1);
  OEMs wire their implementations at deploy time via DI.
- J1939 / CANopen still deferred to v2.0.
- Linux + SocketCAN cross-platform still deferred to v2.0.
