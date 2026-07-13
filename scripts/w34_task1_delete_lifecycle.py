"""W34 T1 deletion script — remove 4 methods from CyclicSendService.cs (lifecycle cluster).

Per W19 R1 first-correction ENHANCED: re-grep boundaries BEFORE running this script
+ post-failure recovery procedure baked in (W31 T2 + W32 T2 + W33 T1+T2+T3 lessons learned).
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from main HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED: use binary read+write with cp1252.

W34 has 4 non-contiguous regions to delete (Lifecycle cluster):
  1. Dispose: L218-L230 (13 LoC)
  2. StopInner: L141-L157 (17 LoC)
  3. Stop: L131-L140 (10 LoC, with xmldoc)
  4. Start: L95-L130 (36 LoC, with xmldoc)

Process in REVERSE ORDER (highest line first) to keep line numbers stable.

W34 T1 2/3 loose-assertion: predicted -76 LoC (4 methods: Dispose 13 + StopInner 17 + Stop 10 + Start 36).
Within ±2 LoC tolerance.

**W19 R1 LESSON ENHANCED**: if delta is outside ±2 tolerance, recovery procedure is:
  1. git checkout src/PeakCan.Host.App/Services/CyclicSendService.cs (restore from git)
  2. Re-grep post-T0 boundaries via grep -n
  3. Correct the offsets in the script
  4. Re-run the script
  5. Verify delta = expected
  6. Build + test
"""
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/CyclicSendService.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# Verify post-T0 boundaries first
print(f"Total lines: {len(lines)}")
if len(lines) > 94:
    print(f"L95 (Start xmldoc): {lines[94].strip()}")
if len(lines) > 130:
    print(f"L131 (Stop xmldoc): {lines[130].strip()}")
if len(lines) > 140:
    print(f"L141 (StopInner): {lines[140].strip()}")
if len(lines) > 217:
    print(f"L218 (Dispose): {lines[217].strip()}")
print()

# Process in REVERSE ORDER:
# First pass: delete Dispose (L218-L230)
new_lines = lines[:217] + lines[230:]
# Second pass: delete StopInner (now at L141-L157 after first pass)
new_lines = new_lines[:140] + new_lines[157:]
# Third pass: delete Stop (now at L131-L140 after second pass, plus xmldoc)
new_lines = new_lines[:130] + new_lines[140:]
# Fourth pass: delete Start (now at L95-L130 after third pass, plus xmldoc)
new_lines = new_lines[:94] + new_lines[130:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

before = 243
after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"CyclicSendService.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 76 (Start 36 + Stop 10 + StopInner 17 + Dispose 13). Within ±2 LoC tolerance.")
assert 74 <= delta <= 78, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
