"""W34 T2 deletion script — remove OnTimerTick (L95-L155 post-T1) from CyclicSendService.cs.

Per W19 R1 first-correction ENHANCED: re-grep boundaries BEFORE running this script
+ post-failure recovery procedure baked in (W31 T2 + W32 T2 + W33 T1+T2+T3 + W34 T1 lessons learned).
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from main HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED: use binary read+write with cp1252.

POST-T1 boundaries (after W34 T1 deleted 4 lifecycle methods: Start 36 + Stop 10 + StopInner 17 + Dispose 13 = 76 LoC):
  - OnTimerTick: L95-L155 (61 LoC, shifted by -76 from main HEAD L158-L218)

W34 T2 2/3 loose-assertion: predicted -61 LoC.
Within ±2 LoC tolerance.

**W19 R1 LESSON ENHANCED**: if delta is outside ±2 tolerance, recovery procedure is:
  1. git checkout src/PeakCan.Host.App/Services/CyclicSendService.cs (restore from git)
  2. Re-grep post-T1 boundaries via grep -n
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

# Verify post-T1 boundaries first
print(f"Total lines: {len(lines)}")
if len(lines) > 94:
    print(f"L95 (OnTimerTick): {lines[94].strip()}")
print()

START, END = 95, 155  # UPDATE per Step 1 grep result (OnTimerTick post-T1)
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"CyclicSendService.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 61 (OnTimerTick body). Within ±2 LoC tolerance.")
assert 59 <= delta <= 63, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
