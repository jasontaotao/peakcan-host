#!/usr/bin/env python3
"""M2 demo asset generator (spec 2026-09-07 secoc-0x27, Phase 2 M2.5).

Generates demo assets into --out-dir:
  demo-trace.asc          8 valid SecOC frames (FV 0..7), for the green run
                          and the runtime BadMac injection run
  attack-replay.asc       duplicate-FV resend attack -> RejectReason.Replay
  attack-rollback.asc     FV rolled-back attack     -> RejectReason.FvRollback
  demo-pdus.json          SecOC PDU config (keyId reference only, D4)
  demo-suite.json         HIL suite: inject BadMac mid-trace, assert verdicts
  demo.dbc                minimal DBC covering the demo message

Wire format (spec 6.1): data = authentic_data || trunc_fv || trunc_mac
MAC input = DataId(16bit BE) || authentic_data || complete_freshness(32bit BE)
TruncMAC = top mac_len bits of the AES-128-CMAC; TruncFV = low fv_len bits.

The KEY MATERIAL itself is only read from --key-hex (or stdin) and is never
written to any generated file (spec D4: keys never leave the KeyStore).
"""
import argparse
import json
import os
import sys

try:
    from Crypto.Hash import CMAC
    from Crypto.Cipher import AES
except ImportError:
    sys.exit("pycryptodome required: pip install pycryptodome")

DEFAULT_CAN_ID = 0x123
DEFAULT_DATA_ID = 0x321
DEFAULT_FV_BITS = 16
DEFAULT_MAC_BITS = 24
DEFAULT_PAYLOAD = "01 02 03"
FRAME_DT_MS = 400
NUM_FRAMES = 8


def cmac_trunc(key: bytes, data_id: int, auth: bytes, fv: int, mac_bits: int) -> bytes:
    c = CMAC.new(key, ciphermod=AES)
    c.update(data_id.to_bytes(2, "big") + auth + fv.to_bytes(4, "big"))
    full = c.digest()
    return full[: mac_bits // 8]


def build_frame(key, auth: bytes, fv: int, fv_bits, mac_bits, can_id, idx=None, corrupt_mac=False):
    # idx = position in the trace (drives the timestamp). The playback scheduler
    # dispatches in timestamp order, so attack frames must carry increasing
    # timestamps even when their FV rolls back (an "old frame arriving late").
    if idx is None:
        idx = fv
    fv_bytes = fv_bits // 8
    mac_bytes = mac_bits // 8
    data = bytearray(auth)
    data += fv.to_bytes(fv_bytes, "big")
    mac = bytearray(cmac_trunc(key, args_data_id, auth, fv, mac_bits))
    if corrupt_mac:
        mac = bytearray(b ^ 0xFF for b in mac)
    data += mac
    ts = FRAME_DT_MS / 1000.0 * idx  # frame index == FV for the demo traces
    hex_data = " ".join(f"{b:02X}" for b in data)
    # PEAK ASC format (standard 11-bit id, no 'x' suffix)
    return f"{ts:.6f} 1 {can_id:x} Rx d {len(data)} {hex_data}"


def write_asc(path: str, header: str, lines):
    with open(path, "w", newline="\n") as f:
        f.write(header + "\n")
        f.write("base hex timestamps absolute\n")
        f.write("\n".join(lines) + "\n")


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--key-hex", help="AES-128 key, 32 hex chars. Omit to read a line from stdin.")
    ap.add_argument("--out-dir", default="artifacts/secoc-demo")
    ap.add_argument("--can-id", type=lambda s: int(s, 0), default=DEFAULT_CAN_ID)
    ap.add_argument("--data-id", type=lambda s: int(s, 0), default=DEFAULT_DATA_ID)
    ap.add_argument("--fv-len-bits", type=int, default=DEFAULT_FV_BITS)
    ap.add_argument("--mac-len-bits", type=int, default=DEFAULT_MAC_BITS)
    ap.add_argument("--payload", default=DEFAULT_PAYLOAD, help="authentic data bytes, hex, space separated")
    ap.add_argument("--key-id", default="demo-key")
    args = ap.parse_args()
    globals()["args_data_id"] = args.data_id

    key_hex = args.key_hex or sys.stdin.readline().strip()
    key = bytes.fromhex(key_hex)
    if len(key) != 16:
        sys.exit("key must be exactly 16 bytes (AES-128, spec 6.1)")
    auth = bytes.fromhex(args.payload.replace(" ", ""))
    can_id = args.can_id

    os.makedirs(args.out_dir, exist_ok=True)
    header = "date Sat Sep 08 12:00:00 PM 2026"

    # demo-trace.asc: FV 0..7 all valid
    write_asc(os.path.join(args.out_dir, "demo-trace.asc"), header,
              [build_frame(key, auth, fv, args.fv_len_bits, args.mac_len_bits, can_id)
               for fv in range(NUM_FRAMES)])

    # attack-replay.asc: ...,2,3,3 -> duplicate resend -> Replay
    fvs = [0, 1, 2, 3, 3]
    write_asc(os.path.join(args.out_dir, "attack-replay.asc"), header,
              [build_frame(key, auth, fv, args.fv_len_bits, args.mac_len_bits, can_id, idx=i)
               for i, fv in enumerate(fvs)])

    # attack-rollback.asc: ...,2,3,2 -> old frame arriving late -> FvRollback
    fvs = [0, 1, 2, 3, 2]
    write_asc(os.path.join(args.out_dir, "attack-rollback.asc"), header,
              [build_frame(key, auth, fv, args.fv_len_bits, args.mac_len_bits, can_id, idx=i)
               for i, fv in enumerate(fvs)])

    # demo-pdus.json: keyId reference only (spec D4 - no key material)
    with open(os.path.join(args.out_dir, "demo-pdus.json"), "w", newline="\n") as f:
        json.dump([{
            "CanId": f"0x{can_id:X}",
            "DataId": f"0x{args.data_id:X}",
            "FvLenBits": args.fv_len_bits,
            "MacLenBits": args.mac_len_bits,
            "KeyId": args.key_id,
            "Mode": "both",
        }], f, indent=2)
        f.write("\n")

    # demo.dbc: minimal message definition (report decoding only)
    with open(os.path.join(args.out_dir, "demo.dbc"), "w", newline="\n") as f:
        f.write('VERSION ""\n\nNS_ :\n\nBS_:\n\nBU_: DEMO_ECU\n\n')
        f.write(f"BO_ {can_id} DEMO_SECOC: {len(auth) + args.fv_len_bits // 8 + args.mac_len_bits // 8} DEMO_ECU\n")
        f.write(' SG_ Payload : 0|24@1+ (1,0) [0|0] ""  DEMO_ECU\n')

    # demo-suite.json: green -> inject BadMac -> red -> clear -> green
    pass_step = {"parameters": {"$kind": "delay", "Milliseconds": 10}}
    # Deliberately-failing else-branch: expect a frame that never appears.
    fail_step = {"parameters": {
        "$kind": "expectFrame",
        "id": {"raw": 2042, "format": "Standard", "type": "Data"},
        "timeoutMs": 200}}
    suite = {
        "name": "SecOcM2Demo",
        "cases": [
            {
                "id": "secoc_demo_badmac",
                "name": "BadMacInjectAndRecover",
                "description": "Replay-verified frames all green -> corrupt MAC bytes on RX -> "
                               "secocRejected fires -> clear fault -> recovery accepted. "
                               "Each if-step passes only when the SecOC verdict matches the phase.",
                "steps": [
                    {"parameters": {"$kind": "delay", "Milliseconds": 600}},
                    {"parameters": {
                        "$kind": "injectFault", "FaultType": "Corrupt", "Direction": "Receive",
                        "CanId": {"raw": can_id, "format": "Standard", "type": "Data"},
                        "Probability": "1.0", "DelayMs": "0",
                        "CorruptByteIndices": [len(auth), len(auth) + 1, len(auth) + 2],
                        "CorruptXorMask": 255, "FaultId": "badmac"}},
                    {"parameters": {"$kind": "delay", "Milliseconds": 1300}},
                    {"parameters": {"$kind": "clearFault", "FaultId": "badmac"}},
                    {"parameters": {"$kind": "delay", "Milliseconds": 1300}},
                    {"parameters": {
                        "$kind": "if", "Condition": "secocRejected(291)",
                        "Then": [pass_step], "Else": [fail_step]}},
                    {"parameters": {
                        "$kind": "if", "Condition": "secocAccepted(291)",
                        "Then": [pass_step], "Else": [fail_step]}},
                ],
            }
        ],
        "globalCaseFixtureKeys": [],
        "suiteFixtureKeys": [],
        "config": {"failurePolicy": "ContinueAll", "continueAfterSetupFailure": True},
        "timeoutMs": 0,
    }
    with open(os.path.join(args.out_dir, "demo-suite.json"), "w", newline="\n") as f:
        json.dump(suite, f, indent=2)
        f.write("\n")

    # demo-suite-green.json: no faults; asserts every frame accepted.
    green = {
        "name": "SecOcM2DemoGreen",
        "cases": [
            {
                "id": "secoc_demo_green",
                "name": "AllFramesAccepted",
                "description": "Baseline: all replayed frames verify; no rejection ever fires.",
                "steps": [
                    {"parameters": {"$kind": "delay", "Milliseconds": 3200}},
                    {"parameters": {
                        "$kind": "if", "Condition": "secocAccepted(291)",
                        "Then": [pass_step], "Else": [fail_step]}},
                    {"parameters": {
                        "$kind": "if", "Condition": "secocRejected(291)",
                        "Then": [fail_step], "Else": [pass_step]}},
                ],
            }
        ],
        "globalCaseFixtureKeys": [],
        "suiteFixtureKeys": [],
        "config": {"failurePolicy": "ContinueAll", "continueAfterSetupFailure": True},
        "timeoutMs": 0,
    }
    with open(os.path.join(args.out_dir, "demo-suite-green.json"), "w", newline="\n") as f:
        json.dump(green, f, indent=2)
        f.write("\n")

    # attack-suite.json: pairs with attack-*.asc; asserts a rejection fired.
    attack = {
        "name": "SecOcM2DemoAttack",
        "cases": [
            {
                "id": "secoc_demo_attack",
                "name": "AttackRejected",
                "description": "Attack trace (replay / FvRollback) must be rejected.",
                "steps": [
                    {"parameters": {"$kind": "delay", "Milliseconds": 2400}},
                    {"parameters": {
                        "$kind": "if", "Condition": "secocRejected(291)",
                        "Then": [pass_step], "Else": [fail_step]}},
                ],
            }
        ],
        "globalCaseFixtureKeys": [],
        "suiteFixtureKeys": [],
        "config": {"failurePolicy": "ContinueAll", "continueAfterSetupFailure": True},
        "timeoutMs": 0,
    }
    with open(os.path.join(args.out_dir, "attack-suite.json"), "w", newline="\n") as f:
        json.dump(attack, f, indent=2)
        f.write("\n")

    print(f"assets written to {args.out_dir}")
    print("next: import the key via `peakcan-hil --secoc-key import --key-id demo-key "
          "--key-file <file> --store-dir <dir>` (key file must NOT be committed)")


if __name__ == "__main__":
    main()
