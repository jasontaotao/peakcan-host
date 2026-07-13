"""W27 T3 deletion script — remove StaticHelpers region (L108-129) from RecentSessionsService.cs.

Per W19 R1 first-correction: re-grep boundaries BEFORE running this script.
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED + W25 cp1252 binary: use binary read+write with cp1252.

W13 T1 2/3 loose-assertion: predicted -22 LoC (range L108-L129 inclusive = 22 lines).
Within ±2 LoC tolerance.
"""
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

START, END = 108, 129  # UPDATE per Step 1 grep result
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"RecentSessionsService.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 22 (Flow C StaticHelpers DefaultPath+Envelope range L108-L129). Within ±2 LoC tolerance.")
assert 20 <= delta <= 24, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
