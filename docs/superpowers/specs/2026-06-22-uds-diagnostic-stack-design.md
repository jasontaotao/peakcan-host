# PeakCan Host — UDS Diagnostic Stack Design

## 1. Overview

Implement a full UDS (Unified Diagnostic Services) diagnostic stack
conforming to ISO 14229 (application layer) and ISO 15765-2 (transport
protocol over CAN). This enables ECU diagnostics, flash programming,
and parameter read/write operations.

## 2. Goals

- **ISO 14229 compliance** — implement all mandatory diagnostic services
- **ISO 15765-2 compliance** — segmented message transport over CAN
- **Session management** — Default/Extended/Programming sessions
- **Security access** — seed/key authentication with configurable algorithms
- **Tester present** — automatic session keep-alive
- **Timeout management** — configurable P2/P2*, S3, P3* timers
- **Negative response handling** — standard NRC codes with retry logic
- **DID/Routine database** — configurable DID and routine definitions
- **Flash programming** — RequestDownload/TransferData/RequestTransferExit

## 3. Architecture

### 3.1 Layered Design

```
┌─────────────────────────────────────────────┐
│  UDS Client (Application Layer)             │
│  - Service request/response                 │
│  - Session management                       │
│  - Security access                          │
│  - Tester present                           │
├─────────────────────────────────────────────┤
│  ISO-TP (Transport Layer)                   │
│  - Segmentation/reassembly                 │
│  - Flow control                            │
│  - Multi-frame handling                    │
├─────────────────────────────────────────────┤
│  CAN Interface (PeakCan.Host.Core)          │
│  - Frame send/receive                       │
│  - CAN ID filtering                        │
└─────────────────────────────────────────────┘
```

### 3.2 Key Classes

| Class | Responsibility |
|-------|---------------|
| `UdsClient` | High-level API: `ReadDID()`, `WriteDID()`, etc. |
| `UdsSession` | Session state (Default/Extended/Programming) |
| `UdsSecurity` | Security access (seed/key exchange) |
| `IsoTpLayer` | ISO 15765-2 segmentation/reassembly |
| `IsoTpFrame` | Single/Multi/Flow control frame types |
| `UdsTimer` | P2/P2*/S3/P3* timeout management |
| `UdsNegativeResponse` | NRC code parsing and handling |
| `DidDatabase` | DID definitions and read/write handlers |
| `RoutineDatabase` | Routine definitions and execution |

## 4. ISO 14229 Services

### 4.1 Mandatory Services (Full Implementation)

| SID | Service | Description |
|-----|---------|-------------|
| 0x10 | DiagnosticSessionControl | Switch session (Default/Extended/Programming) |
| 0x11 | ECUReset | Hard/Soft/Power-Down reset |
| 0x14 | ClearDiagnosticInformation | Clear DTCs |
| 0x19 | ReadDTCInformation | Read DTCs by status/mask |
| 0x22 | ReadDataByIdentifier | Read DID values |
| 0x23 | ReadMemoryByAddress | Read memory region |
| 0x27 | SecurityAccess | Seed/Key authentication |
| 0x2E | WriteDataByIdentifier | Write DID values |
| 0x2F | InputOutputControlByIdentifier | Control I/O |
| 0x31 | RoutineControl | Start/Stop/Query routines |
| 0x34 | RequestDownload | Initiate download |
| 0x36 | TransferData | Transfer data blocks |
| 0x37 | RequestTransferExit | Finalize transfer |
| 0x3E | TesterPresent | Keep session alive |
| 0x85 | ControlDTCSetting | Enable/disable DTC logging |

### 4.2 Negative Response Codes (NRC)

| Code | Mnemonic | Description |
|------|----------|-------------|
| 0x10 | generalReject | General rejection |
| 0x11 | serviceNotSupported | Service not supported |
| 0x12 | subFunctionNotSupported | Sub-function not supported |
| 0x13 | incorrectMessageLengthOrInvalidFormat | Length/format error |
| 0x14 | responseTooLong | Response exceeds max length |
| 0x22 | conditionsNotCorrect | Preconditions not met |
| 0x24 | requestSequenceError | Sequence error |
| 0x25 | noResponseFromSubnetComponent | Subnet timeout |
| 0x26 | failurePreventsExecutionOfRequestedAction | Failure prevents action |
| 0x31 | requestOutOfRange | Request out of range |
| 0x33 | securityAccessDenied | Security access denied |
| 0x35 | invalidKey | Invalid security key |
| 0x36 | exceededNumberOfAttempts | Too many attempts |
| 0x37 | requiredTimeDelayNotExpired | Time delay not expired |
| 0x70 | uploadDownloadNotAccepted | Upload/download rejected |
| 0x71 | transferDataSuspended | Transfer suspended |
| 0x72 | generalProgrammingFailure | Programming failure |
| 0x73 | wrongBlockSequenceCounter | Wrong block sequence |
| 0x7E | subFunctionNotSupportedInActiveSession | Sub-function not in session |
| 0x7F | serviceNotSupportedInActiveSession | Service not in session |

## 5. ISO 15765-2 Transport Protocol

### 5.1 Frame Types

| Frame | PCI Byte | Description |
|-------|----------|-------------|
| Single (SF) | 0x0L | L = payload length (1-7 bytes) |
| First (FF) | 0x1L LL | LLLL = total length (8-4095 bytes) |
| Consecutive (CF) | 0x2N | N = sequence number (0-15, wraps) |
| Flow Control (FC) | 0x3F FS BS STmin | Flow status + block size + STmin |

### 5.2 Timing Parameters

| Parameter | Description | Default |
|-----------|-------------|---------|
| P2 | Time between request and response | 50ms |
| P2* | Time after NRC 0x78 (requestCorrectlyReceivedResponsePending) | 5000ms |
| S3 | Time before session timeout | 5000ms |
| P3* | Time for response pending | 5000ms |

### 5.3 Segmentation Algorithm

```
Sender:
1. If payload ≤ 7 bytes → Single Frame
2. If payload > 7 bytes → First Frame + Consecutive Frames
3. Wait for Flow Control after First Frame
4. Send Consecutive Frames with STmin delay

Receiver:
1. Receive First Frame → allocate buffer
2. Send Flow Control (BS, STmin)
3. Receive Consecutive Frames → reassemble
4. Validate sequence numbers
```

## 6. Session Management

### 6.1 Session States

```
┌──────────────┐
│   Default    │ ← Power-on state
│   Session    │
└──────┬───────┘
       │ 0x10 02 (Extended)
       ▼
┌──────────────┐
│  Extended    │ ← Most diagnostic operations
│   Session    │
└──────┬───────┘
       │ 0x10 03 (Programming)
       ▼
┌──────────────┐
│ Programming  │ ← Flash programming
│   Session    │
└──────────────┘
```

### 6.2 Session Transitions

- Default → Extended: Always allowed
- Default → Programming: Allowed with security access
- Extended → Programming: Allowed with security access
- Programming → Default: ECUReset required
- Any → Default: Session timeout (S3)

## 7. Security Access

### 7.1 Seed/Key Exchange

```
Client                    ECU
  │                        │
  ├─ RequestSeed (0x27 01)─►
  │                        │
  ◄─── Seed ──────────────┤
  │                        │
  ├─ SendKey (0x27 02)────►
  │   Key = f(Seed)        │
  │                        │
  ◄─── Positive/Negative ─┤
```

### 7.2 Security Levels

| Level | Description |
|-------|-------------|
| 0x01 | Level 1 (Seed request) |
| 0x02 | Level 1 (Key send) |
| 0x03 | Level 2 (Seed request) |
| 0x04 | Level 2 (Key send) |
| ... | Up to 0x7F |

## 8. DID Database

### 8.1 DID Definition

```csharp
public class DidDefinition
{
    public ushort Did { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public DidAccess Access { get; set; }
    public int DataLength { get; set; }
    public Func<byte[]>? ReadHandler { get; set; }
    public Action<byte[]>? WriteHandler { get; set; }
}
```

### 8.2 Common DIDs

| DID | Name | Description |
|-----|------|-------------|
| 0xF180 | BootSoftwareIdentification | Boot SW ID |
| 0xF186 | ActiveDiagnosticSession | Current session |
| 0xF187 | ECUIdentificationNumber | ECU serial |
| 0xF18A | SystemSupplierIdentifier | Supplier ID |
| 0xF190 | VIN | Vehicle ID number |
| 0xF1A0 | ApplicationSoftwareIdentification | App SW ID |

## 9. Flash Programming

### 9.1 Download Sequence

```
1. DiagnosticSessionControl (Programming)
2. SecurityAccess (Seed/Key)
3. RequestDownload (address, length)
4. TransferData (block 1)
5. TransferData (block 2)
...
N. RequestTransferExit
```

### 9.2 Upload Sequence

```
1. DiagnosticSessionControl (Extended)
2. SecurityAccess (Seed/Key)
3. RequestUpload (address, length)
4. TransferData (block 1)
5. TransferData (block 2)
...
N. RequestTransferExit
```

## 10. Implementation Phases

### Phase A: Transport Layer (ISO-TP)
1. `IsoTpFrame` types (SF, FF, CF, FC)
2. `IsoTpLayer` segmentation/reassembly
3. CAN ID configuration (request/response IDs)
4. Unit tests for ISO-TP

### Phase B: UDS Client Core
1. `UdsClient` high-level API
2. `UdsSession` session management
3. `UdsTimer` timeout handling
4. Negative response processing
5. Unit tests for UDS client

### Phase C: Services
1. DiagnosticSessionControl (0x10)
2. ECUReset (0x11)
3. ReadDataByIdentifier (0x22)
4. WriteDataByIdentifier (0x2E)
5. SecurityAccess (0x27)
6. TesterPresent (0x3E)
7. RoutineControl (0x31)
8. Unit tests for services

### Phase D: Advanced Services
1. ReadDTCInformation (0x19)
2. ClearDiagnosticInformation (0x14)
3. RequestDownload (0x34)
4. TransferData (0x36)
5. RequestTransferExit (0x37)
6. Unit tests

### Phase E: Database + UI
1. DID database (JSON/XML config)
2. Routine database
3. UDS tab in UI (TreeView + DataGrid)
4. DID read/write UI
5. Routine execution UI

## 11. File Changes Estimate

| Area | New Files | Modified Files |
|------|-----------|----------------|
| Core (ISO-TP + UDS) | 15-20 | 0 |
| App (ViewModels + Views) | 5-8 | 2 |
| Tests | 25-30 | 0 |
| Docs + Config | 3-5 | 1 |
| **Total** | ~50 | ~3 |

## 12. Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| ISO 14229 complexity | High | Start with mandatory services, add optional later |
| ISO 15765-2 edge cases | Medium | Comprehensive unit tests for segmentation |
| ECU-specific quirks | Medium | Configurable timing and DID definitions |
| Flash programming safety | High | Validate addresses, checksum verification |
| Multi-ECU support | Medium | Design with ECU address abstraction from start |

## 13. Open Questions

1. **Configuration format** — JSON vs XML for DID/Routine definitions?
2. **Multi-ECU** — Single ECU first, or design for multiple from start?
3. **Flash file format** — S-record, Intel HEX, or raw binary?
4. **Logging** — UDS-specific log file or integrated with existing Serilog?
