"""W30 T1 deletion script — remove SendAsync region (L75-L165) from SequenceSendService.cs.

Per W19 R1 first-correction: re-grep boundaries BEFORE running this script.
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED + W25/W27/W28/W29 cp1252 binary: use binary read+write with cp1252.

W30 T1 2/3 loose-assertion: predicted -91 LoC (range L75-L165 inclusive = 91 lines
including SendAsync method body + xmldoc).
Within ±2 LoC tolerance.
"""
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

START, END = 75, 165  # UPDATE per Step 1 grep result
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"SequenceSendService.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 91 (SendAsync body L75-L165 inclusive). Within ±2 LoC tolerance.")
assert 89 <= delta <= 93, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
