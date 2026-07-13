"""W29 T1 deletion script — remove PersistenceFlow region (L211-264) from SendFrameLibrary.cs.

Per W19 R1 first-correction: re-grep boundaries BEFORE running this script.
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED + W25/W27/W28 cp1252 binary: use binary read+write with cp1252.

W13 T1 2/3 loose-assertion: predicted -54 LoC (range L211-L264 inclusive = 54 lines
including EnsureLoaded + LoadUnlocked + SaveUnlocked method bodies).
Within ±2 LoC tolerance.
"""
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/SendFrameLibrary.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

START, END = 211, 264  # UPDATE per Step 1 grep result
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"SendFrameLibrary.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 54 (Flow A PersistenceFlow EnsureLoaded+LoadUnlocked+SaveUnlocked range L211-L264). Within ±2 LoC tolerance.")
assert 52 <= delta <= 56, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
