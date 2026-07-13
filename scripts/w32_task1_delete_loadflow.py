"""W32 T1 deletion script — remove Load region (L53-L148) from DbcApi.cs.

Per W19 R1 first-correction ENHANCED: re-grep boundaries BEFORE running this script.
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from main HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED + W31 cp1252 binary: use binary read+write with cp1252.

W32 T1 2/3 loose-assertion: predicted -96 LoC (range L53-L148 inclusive = 96 lines
including Load xmldoc L53-L75 + body L76-L148).
Within ±2 LoC tolerance.
"""
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/Scripting/DbcApi.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

START, END = 53, 148  # xmldoc L53-L75 + body L76-L148
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"DbcApi.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 96 (Load xmldoc L53-L75 + body L76-L148). Within ±2 LoC tolerance.")
assert 94 <= delta <= 98, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
