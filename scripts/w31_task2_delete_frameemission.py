"""W31 T2 deletion script — remove 4 helpers from ReplayService.cs (post-T1 boundaries).

Per W19 R1 first-correction: re-grep boundaries BEFORE running this script.
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from main HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED + W25/W27/W28/W29/W30 cp1252 binary: use binary read+write with cp1252.

POST-T1 boundaries (after W31 T1 deleted LoadAsync L152-L182 + Reset L191-L209):
  1. EmitFrameToSinkAsync: L204-L210 (7 LoC, shifted by -50 from main HEAD L251-L257)
  2. EmitFrame: L161-L199 (39 LoC, shifted by -50 from main HEAD L211-L249)
  3. OnSinkThrewFromTimeline: L131-L145 (15 LoC, unchanged)
  4. RaisePlaybackEnded: L122-L129 (8 LoC, unchanged)

Process in REVERSE ORDER (highest line first) to keep line numbers stable.

W31 T2 2/3 loose-assertion: predicted -69 LoC (4 helpers: 7+39+15+8).
Within ±2 LoC tolerance.
"""
from pathlib import Path

p = Path("src/PeakCan.Host.Core/Replay/ReplayService.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# Verify post-T1 boundaries first
print(f"Total lines: {len(lines)}")
print(f"L122-L129 (RaisePlaybackEnded): lines 122-129 = {lines[121].strip()} ... {lines[128].strip()}")
print(f"L131-L145 (OnSinkThrewFromTimeline): line 131 = {lines[130].strip()}, line 145 = {lines[144].strip()}")
print(f"L161-L199 (EmitFrame): line 161 = {lines[160].strip()}, line 199 = {lines[198].strip()}")
print(f"L204-L210 (EmitFrameToSinkAsync): line 204 = {lines[203].strip()}, line 210 = {lines[209].strip()}")
print(f"L215 closing brace: {lines[214].strip()}")
print()

# Process in REVERSE ORDER:
# First pass: delete EmitFrameToSinkAsync (L204-L210)
new_lines = lines[:203] + lines[210:]
# Second pass: delete EmitFrame (now at L161-L199 after first pass)
new_lines = new_lines[:160] + new_lines[199:]
# Third pass: delete OnSinkThrewFromTimeline (now at L131-L145 after second pass)
new_lines = new_lines[:130] + new_lines[145:]
# Fourth pass: delete RaisePlaybackEnded (now at L122-L129 after third pass)
new_lines = new_lines[:121] + new_lines[129:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

before = 215
after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"ReplayService.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 69 (4 helpers: RaisePlaybackEnded 8 + OnSinkThrewFromTimeline 15 + EmitFrame 39 + EmitFrameToSinkAsync 7). Within ±2 LoC tolerance.")
assert 67 <= delta <= 71, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
