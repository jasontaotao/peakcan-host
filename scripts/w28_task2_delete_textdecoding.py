"""W28 T2 deletion script — remove TextDecoding region (L104-214) from DbcService.cs.

Per W19 R1 first-correction: re-grep boundaries BEFORE running this script.
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED + W25 cp1252 binary: use binary read+write with cp1252.

W13 T1 2/3 loose-assertion: predicted -111 LoC (range L104-L214 inclusive = 111 lines
including ReadDbcBytesAsync xmldoc + body + ReadDbcText xmldoc + body).
Within ±2 LoC tolerance.
"""
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/DbcService.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

START, END = 104, 214  # UPDATE per Step 1 grep result
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"DbcService.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 111 (Flow B TextDecoding ReadDbcBytesAsync+ReadDbcText range L104-L214). Within ±2 LoC tolerance.")
assert 109 <= delta <= 113, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
