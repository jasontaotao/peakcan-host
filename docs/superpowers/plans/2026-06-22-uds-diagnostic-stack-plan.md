# PeakCan Host — UDS Diagnostic Stack Implementation Plan

> **Spec**: [2026-06-22-uds-diagnostic-stack-design.md](../specs/2026-06-22-uds-diagnostic-stack-design.md)
> **Baseline**: `e5de8e2` (v1.0.0)
> **Target**: v1.1.0

## Task Breakdown

### Phase A: Transport Layer (ISO-TP)

#### A1: ISO-TP frame types
- Create `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpFrame.cs`
- Single Frame (SF), First Frame (FF), Consecutive Frame (CF), Flow Control (FC)
- Frame encoding/decoding methods
- **Estimated**: 2 hours

#### A2: ISO-TP layer
- Create `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs`
- Segmentation: payload → SF/FF/CF sequence
- Reassembly: SF/FF/CF → complete payload
- Flow control handling (BS, STmin)
- Sequence number validation
- **Estimated**: 4 hours

#### A3: CAN ID configuration
- Create `src/PeakCan.Host.Core/Uds/CanIdConfig.cs`
- Request CAN ID, Response CAN ID
- Functional/Physical addressing support
- **Estimated**: 1 hour

#### A4: ISO-TP unit tests
- Create `tests/PeakCan.Host.Core.Tests/Uds/IsoTp/`
- Test SF/FF/CF/FC encoding/decoding
- Test segmentation/reassembly
- Test sequence number validation
- Test flow control handling
- **Estimated**: 4 hours

### Phase B: UDS Client Core

#### B1: UDS negative response codes
- Create `src/PeakCan.Host.Core/Uds/UdsNegativeResponse.cs`
- NRC enum with all standard codes (0x10-0x7F)
- NRC parsing from response bytes
- **Estimated**: 1 hour

#### B2: UDS session management
- Create `src/PeakCan.Host.Core/Uds/UdsSession.cs`
- Session states: Default, Extended, Programming
- Session transitions with validation
- **Estimated**: 2 hours

#### B3: UDS timer management
- Create `src/PeakCan.Host.Core/Uds/UdsTimer.cs`
- P2, P2*, S3, P3* timers
- Timeout detection and callback
- **Estimated**: 2 hours

#### B4: UDS client core
- Create `src/PeakCan.Host.Core/Uds/UdsClient.cs`
- High-level API: `SendServiceAsync()`, `WaitForResponseAsync()`
- Request/response correlation
- Negative response handling with retry logic
- **Estimated**: 4 hours

#### B5: UDS core unit tests
- Create `tests/PeakCan.Host.Core.Tests/Uds/`
- Test session transitions
- Test timer behavior
- Test negative response handling
- **Estimated**: 3 hours

### Phase C: Mandatory Services

#### C1: DiagnosticSessionControl (0x10)
- Create `src/PeakCan.Host.Core/Uds/Services/DiagnosticSessionControl.cs`
- Session switching (Default/Extended/Programming)
- Session validation before other services
- **Estimated**: 2 hours

#### C2: ECUReset (0x11)
- Create `src/PeakCan.Host.Core/Uds/Services/EcuReset.cs`
- Hard/Soft/Power-Down reset types
- Wait for ECU restart
- **Estimated**: 1.5 hours

#### C3: ReadDataByIdentifier (0x22)
- Create `src/PeakCan.Host.Core/Uds/Services/ReadDataByIdentifier.cs`
- Single/multi-DID read
- Response parsing
- **Estimated**: 2 hours

#### C4: WriteDataByIdentifier (0x2E)
- Create `src/PeakCan.Host.Core/Uds/Services/WriteDataByIdentifier.cs`
- Single-DID write
- Write verification
- **Estimated**: 1.5 hours

#### C5: SecurityAccess (0x27)
- Create `src/PeakCan.Host.Core/Uds/Services/SecurityAccess.cs`
- Seed request, Key send
- Multiple security levels
- Configurable key algorithm
- **Estimated**: 3 hours

#### C6: TesterPresent (0x3E)
- Create `src/PeakCan.Host.Core/Uds/Services/TesterPresent.cs`
- Automatic S3 timer reset
- Background task with configurable interval
- **Estimated**: 1.5 hours

#### C7: RoutineControl (0x31)
- Create `src/PeakCan.Host.Core/Uds/Services/RoutineControl.cs`
- Start/Stop/Query routine results
- Routine ID configuration
- **Estimated**: 2 hours

#### C8: Services unit tests
- Create `tests/PeakCan.Host.Core.Tests/Uds/Services/`
- Test each service request/response
- Test error handling
- **Estimated**: 4 hours

### Phase D: Advanced Services

#### D1: ReadDTCInformation (0x19)
- Create `src/PeakCan.Host.Core/Uds/Services/ReadDtcInformation.cs`
- Read by status mask
- Read by DTC number
- DTC snapshot data
- **Estimated**: 3 hours

#### D2: ClearDiagnosticInformation (0x14)
- Create `src/PeakCan.Host.Core/Uds/Services/ClearDiagnosticInformation.cs`
- Clear all DTCs
- Clear specific DTC group
- **Estimated**: 1 hour

#### D3: Flash programming services
- Create `src/PeakCan.Host.Core/Uds/Services/RequestDownload.cs`
- Create `src/PeakCan.Host.Core/Uds/Services/TransferData.cs`
- Create `src/PeakCan.Host.Core/Uds/Services/RequestTransferExit.cs`
- Address/length validation
- Block sequence management
- **Estimated**: 4 hours

#### D4: Advanced services unit tests
- Test DTC read/clear
- Test flash programming sequence
- **Estimated**: 3 hours

### Phase E: Database + UI

#### E1: DID database
- Create `src/PeakCan.Host.Core/Uds/Database/DidDefinition.cs`
- Create `src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs`
- JSON configuration file support
- Common DIDs (VIN, ECU ID, etc.)
- **Estimated**: 3 hours

#### E2: Routine database
- Create `src/PeakCan.Host.Core/Uds/Database/RoutineDefinition.cs`
- Create `src/PeakCan.Host.Core/Uds/Database/RoutineDatabase.cs`
- JSON configuration file support
- **Estimated**: 2 hours

#### E3: UDS ViewModel
- Create `src/PeakCan.Host.App/ViewModels/UdsViewModel.cs`
- Session state display
- DID read/write commands
- Routine execution commands
- DTC list display
- **Estimated**: 4 hours

#### E4: UDS View
- Create `src/PeakCan.Host.App/Views/UdsView.xaml`
- TreeView for DIDs/Routines
- DataGrid for DTC list
- Session/security controls
- **Estimated**: 3 hours

#### E5: Integration + tests
- Wire UDS into AppShell
- Add "UDS" tab
- Integration tests
- **Estimated**: 3 hours

## Execution Order

```
A1 → A2 → A3 → A4
      ↓
B1 → B2 → B3 → B4 → B5
      ↓
[C1, C2, C3, C4, C5, C6, C7 parallel] → C8
      ↓
[D1, D2, D3 parallel] → D4
      ↓
E1 → E2 → E3 → E4 → E5
```

## Test Strategy

- **Unit tests**: 80%+ coverage for ISO-TP, UDS client, all services
- **Integration tests**: Full diagnostic session (connect → read DID → write DID → disconnect)
- **Manual tests**: Test against real ECU or UDS simulator
- **Edge cases**: Multi-frame segmentation, timeout handling, NRC retry

## Estimated Total

| Phase | Tasks | Estimated |
|-------|-------|-----------|
| A: Transport | 4 | ~11 hours |
| B: UDS Core | 5 | ~12 hours |
| C: Services | 8 | ~17 hours |
| D: Advanced | 4 | ~11 hours |
| E: Database + UI | 5 | ~15 hours |
| **Total** | **26** | **~66 hours** |
