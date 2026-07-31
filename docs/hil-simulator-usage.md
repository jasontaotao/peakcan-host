# HIL Standalone Simulator (Phase 5 Sprint 13)

## Overview

The standalone ECU simulator runs a `StatefulVirtualEcu` on a real PCAN-USB
channel. It connects to hardware, then blocks indefinitely, responding to
incoming UDS requests according to the state machine defined in an ECU script
JSON file. Press Ctrl+C to exit.

## Usage

```bash
peakcan-hil --dbc <path.dbc> --ecu <script.json> --hw USB1 --simulate
```

### Arguments

| Argument | Required | Description |
|----------|----------|-------------|
| `--dbc` | Yes | DBC file path (used for signal decode in logs) |
| `--ecu` | Yes | ECU simulator script JSON path |
| `--hw` | Yes | Hardware channel (USB1..USB16) |
| `--uds-req` | No | UDS request CAN ID (default: 0x7DF) |
| `--uds-resp` | No | UDS response CAN ID (default: 0x7E8) |

### Example

```bash
peakcan-hil --dbc vehicle.dbc --ecu bms_simulator.json --hw USB1 --simulate
# Output: Simulating ECU 'BMS_Simulator' on USB1. Press Ctrl+C to exit.
```

## Architecture

```
┌──────────────┐  CAN bus  ┌─────────────────────────────┐
│  Tester/HIL  │ ◄──────── │  peakcan-hil --simulate      │
│  (external)  │           │                             │
└──────────────┘           │  ┌───────────────────────┐  │
                           │  │ EcuSimulatorHost      │  │
                           │  │  ├─ PeakCanChannel    │  │
                           │  │  └─ StatefulVirtualEcu│  │
                           │  │       └─ EcuStateMachine│ │
                           │  └───────────────────────┘  │
                           └─────────────────────────────┘
```

The `EcuSimulatorHost` (`src/PeakCan.Host.Infrastructure/HIL/EcuSimulatorHost.cs`)
owns the channel lifecycle:

1. `ConnectAsync(CanFd1Mbps, fd: true)` — bring up the PCAN channel.
2. `Task.Delay(Infinite, ct)` — block until Ctrl+C cancels the token.
3. On cancellation: `DisconnectAsync` + dispose the ECU.

The `StatefulVirtualEcu` subscribes to `channel.FrameReceived` and responds
automatically via ISO-TP — no polling loop needed.

## ECU Script Format

```json
{
  "name": "BMS_Simulator",
  "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
  "states": [
    {
      "name": "default",
      "transitions": [
        {
          "serviceId": "0x27",
          "subFunction": 1,
          "response": { "kind": "static", "data": [103, 1, 17, 34, 51, 68] },
          "toState": "seedSent"
        }
      ]
    }
  ]
}
```

Supports both `"states"` (stateful) and `"rules"` (stateless) formats — see
existing `docs/` for full schema.
