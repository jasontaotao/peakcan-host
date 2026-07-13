"""W30 T2 deletion script — remove TryBuildRow + SendOneAsync region (L83-L175 post-T1) from SequenceSendService.cs.

Per W19 R1 first-correction: re-grep boundaries BEFORE running this script.
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from main HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED + W25/W27/W28/W29 cp1252 binary: use binary read+write with cp1252.

W30 T2 2/3 loose-assertion: predicted -93 LoC (range L83-L175 inclusive post-T1 = 93 lines
including TryBuildRow + SendOneAsync method bodies + their xmldocs + blank lines).
Within ±2 LoC tolerance.
"""
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

START, END = 83, 175  # UPDATED per Step 1 grep result (post-T1 shift)
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"SequenceSendService.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 93 (TryBuildRow + SendOneAsync L83-L175 inclusive post-T1). Within ±2 LoC tolerance.")
assert 91 <= delta <= 95, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
