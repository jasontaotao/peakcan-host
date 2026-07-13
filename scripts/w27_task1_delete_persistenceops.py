"""W27 T1 deletion script — remove PersistenceOps region (L208-301) from RecentSessionsService.cs.

Per W19 R1 first-correction: re-grep boundaries BEFORE running this script.
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED + W25 cp1252 binary: use binary read+write with cp1252.

W13 T1 2/3 loose-assertion: predicted -94 LoC (range L208-L301 inclusive = 94 lines
including LoadAsync xmldoc + body + Persist body + Raise expression-bodied).
Within ±2 LoC tolerance.
"""
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

START, END = 208, 301  # UPDATE per Step 1 grep result
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"RecentSessionsService.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 94 (Flow A PersistenceOps L208-L301 with xmldoc). Within ±2 LoC tolerance.")
assert 92 <= delta <= 96, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
