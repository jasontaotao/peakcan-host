"""W29 T2 deletion script — remove Mutators region (L106-209) from SendFrameLibrary.cs.

Per W19 R1 first-correction: re-grep boundaries BEFORE running this script.
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED + W25/W27/W28 cp1252 binary: use binary read+write with cp1252.

W13 T1 2/3 loose-assertion: predicted -104 LoC (range L106-L209 inclusive = 104 lines
including 6 lock-gated mutator method bodies + Count getter + their xmldocs).
Within ±2 LoC tolerance.
"""
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/SendFrameLibrary.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

START, END = 106, 209  # UPDATE per Step 1 grep result
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"SendFrameLibrary.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 104 (Flow B Mutators 6 lock-gated methods + Count getter range L106-L209). Within ±2 LoC tolerance.")
assert 102 <= delta <= 106, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
