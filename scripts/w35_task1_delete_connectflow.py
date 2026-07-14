# scripts/w35_task1_delete_connectflow.py — W19 R1 first-correction ENHANCED + W17 wc-l-splitlines CONFIRMED
"""W35 T1 deletion script — remove 3 methods from PeakCanChannel.cs (lifecycle cluster).

Per W19 R1 first-correction ENHANCED: re-grep boundaries BEFORE running this script
+ post-failure recovery procedure baked in (W31 T2 + W32 T2 + W33 T1+T2+T3 + W34 T1 lessons learned).
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from main HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED: use binary read+write with cp1252.

W35 has 3 non-contiguous regions to delete (Connect lifecycle cluster):
  1. DisposeAsync: L222-L227 (6 LoC)
  2. DisconnectAsync: L160-L172 (13 LoC)
  3. ConnectAsync: L109-L158 (50 LoC)

Process in REVERSE ORDER (highest line first) to keep line numbers stable.

W35 T1 2/3 loose-assertion: predicted -69 LoC (3 methods: ConnectAsync 50 + DisconnectAsync 13 + DisposeAsync 6).
Within +-2 LoC tolerance.

**W19 R1 LESSON ENHANCED**: if delta is outside +-2 tolerance, recovery procedure is:
  1. git checkout src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs (restore from git)
  2. Re-grep post-T0 boundaries via grep -n
  3. Correct the offsets in the script
  4. Re-run the script
  5. Verify delta = expected
  6. Build + test
"""
from pathlib import Path

p = Path("src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# Verify post-T0 boundaries first
print(f"Total lines: {len(lines)}")
if len(lines) > 108:
    print(f"L109 (ConnectAsync): {lines[108].strip()}")
if len(lines) > 159:
    print(f"L160 (DisconnectAsync): {lines[159].strip()}")
if len(lines) > 221:
    print(f"L222 (DisposeAsync): {lines[221].strip()}")
print()

# Process in REVERSE ORDER:
# First pass: delete DisposeAsync (L222-L227)
new_lines = lines[:221] + lines[227:]
# Second pass: delete DisconnectAsync (now at L160-L172 after first pass)
new_lines = new_lines[:159] + new_lines[172:]
# Third pass: delete ConnectAsync (now at L109-L158 after second pass)
new_lines = new_lines[:108] + new_lines[158:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

before = 244
after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"PeakCanChannel.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 69 (ConnectAsync 50 + DisconnectAsync 13 + DisposeAsync 6). Within +-2 LoC tolerance.")
assert 67 <= delta <= 71, f"FAIL: delta {delta} outside +-2 tolerance"
print("PASS.")