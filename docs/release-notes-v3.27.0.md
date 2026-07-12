# Release Notes v3.27.0 — UdsClient god-class refactor (MINOR)

**Released:** 2026-07-12
**Tag:** v3.27.0
**Branch:** `feature/w12-uds-client-god-class`
**Parent:** v3.26.0 MINOR (`17d7011` on origin/main)

## Why this MINOR

`src/PeakCan.Host.Core/Uds/UdsClient.cs` had grown to **704 LoC** as of v3.26.0 — at 88.0% of the 800 LoC Round-1 ceiling. Single instance class implementing `IDisposable` with **25 methods** (3 ctors + 7 wire/transport + 13 UDS services + 2 lifecycle façades) + 1 public record type `DiagnosticSessionResponse`. ISO 14229 service IDs dictate loose grouping boundaries — the code organically grew into a god-class as new UDS services were added across v1.1.0 → v1.3.x.

This is the **9th god-class refactor** in the project (W12 in the W3-W12 series), the **3rd Core layer** god-class (after W9 IsoTpLayer + W10 DbcParser), and the **FIRST Core-layer instance class with IDisposable + virtual methods + internal test seams + nested types** combination to receive the partial-class split. Previous W3-W11 refactors covered: 6 App-layer VMs (instance ObservableObject) + 2 Core-layer (IsoTpLayer instance + DbcParser static nested-class) + 1 App-layer DI composition root (single-monolithic-method split). **W12 validates the partial-class pattern works for every god-class shape in the project.**

## LoC trajectory (W8.5 D7 CONFIRMED formula)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. All 5 transitions EXACT match.

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | Transport (wire + Rx + Dispose + test seam) | 152-177, 569-695 | 153 | 552 |
| T2 | SessionControl (0x10 + 0x11 + S3 keepalive façades) | 153-210, 531-541 | 69 | 484 |
| T3 | DataIO + DTC (0x22 + 0x2E + 0x19 + 0x14) | 154-186, 445-471 | 60 | 425 |
| T4 | SecurityAccess (0x27 × 2) | 155-291 | 137 | 289 |
| T5 | Transfer (0x3E + 0x31 + 0x34 + 0x36 + 0x37) | 156-171, 173-210, 212-273 | 116 | 174 |
| **Total** | -- | -- | **535** | **174** |

**Net**: 704 → 174 LoC in main file (**-530 LoC, -75.3%**). Total project LoC unchanged (still ~704 across main + 5 partials).

## What this MINOR does

### Refactor — UdsClient split into 5 partial-class files

The instance class `UdsClient` becomes `public partial class UdsClient : IDisposable`. The main file keeps: 6 readonly fields (state), 2 volatile-simulation fields, 2 properties, 3 ctors, `OnP2TimeoutFiredForTesting` internal hook, and the `DiagnosticSessionResponse` public record. Each partial file owns one logical flow group of UDS service methods + the tightly-coupled transport internals.

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `UdsClient/TransportFlow.cs` | A — wire layer + Rx + Dispose + test seam | ~165 | SendRequestAsync, SendRequestInternalAsync, OnP2TimeoutFired, OnMessageReceived, PublicOnMessageReceivedForTesting, Dispose |
| `UdsClient/SessionFlow.cs` | B — 0x10 + 0x11 + S3 keepalive façades | ~75 | DiagnosticSessionControlAsync, EcuResetAsync × 2, StartTesterPresent, StopTesterPresent |
| `UdsClient/DataIOFlow.cs` | C — DataIO + DTC | ~55 | ReadDataByIdentifierAsync (0x22), WriteDataByIdentifierAsync (0x2E), ReadDtcInformationAsync (0x19), ClearDiagnosticInformationAsync (0x14) |
| `UdsClient/SecurityFlow.cs` | D — Security | ~140 | SecurityAccessAsync × 2 overloads (3-arg + 2-arg with IKeyDerivationAlgorithm) |
| `UdsClient/TransferFlow.cs` | E — TesterPresent + RoutineControl + Transfer | ~135 | TesterPresentAsync (0x3E), RoutineControlAsync × 2 (0x31), RequestDownloadAsync (0x34), TransferDataAsync (0x36), RequestTransferExitAsync (0x37) |

Each partial file declares `public partial class UdsClient { ... }` and adds the flow's methods verbatim. Cross-flow calls (`SendRequestAsync` from 13 UDS service methods; `Session.SetSession` from `DiagnosticSessionControlAsync`; `Security.IsLocked` from `SecurityAccessAsync`; `_keyAlgorithm.ComputeKey` from `SecurityAccessAsync` 2-arg; etc.) compile via partial-class visibility — no facade pattern, no service-layer extraction.

### Test seam preserved + fix

The `internal void PublicOnMessageReceivedForTesting(byte[])` seam moved with the OnMessageReceived handler to TransportFlow (per W12 D5). The `internal Action? OnP2TimeoutFiredForTesting` hook stayed in main (it's a field, not a method). One pre-existing xmldoc-grep test (`UdsClientSecurityAccessOverloadTests.TwoArg_Overload_XmlDoc_Mentions_MidHandshake_Race`) failed on T4 because it `File.ReadAllText`'d `UdsClient.cs` only and the 2-arg xmldoc landed in `SecurityFlow.cs`. **Test fix**: read both files into a combined text before assertion. Minimal diff, intent preserved. None of the 148 UDS test cases changed behavior.

## Verification

- `dotnet build src/PeakCan.Host.Core/`: **0 errors, 0 warnings**
- `dotnet test --filter "~Uds"`: **148 / 148 PASS** (count unchanged from v3.26.0 baseline)
- `dotnet test` (full solution): **1339 PASS, 5 SKIP, 0 FAIL** — 89 Infrastructure + 801 App + 449 Core — no regression

## Lessons applied

- **W8.5 D7 CONFIRMED** LoC correction formula (`LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`) — all 5 transitions EXACT match, no deviation needed (after the initial T1 assertion-timing fix).
- **W11 D5 sister-lesson** — state ownership + ctor-grouping (all 3 ctors + readonly fields stay in main; per-flow methods move to partials).
- **W11 R3 helper-extract-on-demand** — verified NOT needed: the largest method body after extraction (`OnMessageReceived` at 51 LoC + `SendRequestInternalAsync` at 47 LoC) both fit inline. W12 D7 prediction confirmed.
- **R1 missing-usings** — preflight checked each partial. 4 of 5 partials need 0 new usings. No partial required `using` injection during execution.

## New sibling-lesson candidate (at 2/3 confirmation)

- **`xmldoc-grep-test-breaks-when-partial-class-split-moves-the-overloaded-method-xmldoc-into-different-file`** — first observed in W12 T4. Test that `File.ReadAllText`'s the source file path and greps for an xmldoc substring cannot survive a partial-class refactor if the move lands the method in a different file. Fix: read all known partial files into combined text (cheap, intent-preserving). Awaiting 1 more observation for promotion.

## What stays the same

- Public API surface — `SendRequestAsync` + 13 UDS service methods + 3 ctors + `Start/StopTesterPresent` + `Dispose` + `DiagnosticSessionResponse` record all callable with identical signatures from `UdsClient`.
- ISO 14229 service IDs unchanged.
- Test count unchanged (148 UDS tests pre + post).
- DI registration in `AppHostBuilder` unchanged (it already injects `IUdsClient` → `UdsClient`, partial-class transparent).

## Next steps (post-ship)

- **W12.5 vault-only PATCH** — candidate lesson-promotion opportunity if `xmldoc-grep-test-breaks-when-partial-class-split-moves-overloaded-method-xmldoc-into-different-file` reaches 3/3 confirmation in W13+.
- **W13** — next god-class refactor candidate: `ScriptEngine.cs` (548 LoC App layer) / `AscParser.cs` (513 LoC Core layer) / `ReplayTimeline.cs` (469 LoC Core layer).
