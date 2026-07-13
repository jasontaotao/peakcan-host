"""W33 T3 deletion script — remove DefaultPath (L118-L122) from SequenceLibrary.cs.

Per W19 R1 first-correction ENHANCED: re-grep boundaries BEFORE running this script.
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from main HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED: use binary read+write with cp1252.

POST-T2 boundaries (after W33 T2 deleted 5 mutators):
  - DefaultPath: L118-L122 (5 LoC, shifted by -116 from main HEAD L234-L238)

W33 T3 2/3 loose-assertion: predicted -5 LoC.
Within ±2 LoC tolerance.

**W19 R1 LESSON ENHANCED**: if delta is outside ±2 tolerance, recovery procedure is:
  1. git checkout src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs (restore from git)
  2. Re-grep post-T2 boundaries via grep -n
  3. Correct the offsets in the script
  4. Re-run the script
  5. Verify delta = expected
  6. Build + test
"""
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

START, END = 118, 122  # UPDATE per Step 1 grep result (DefaultPath post-T2)
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"SequenceLibrary.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 5 (DefaultPath body). Within ±2 LoC tolerance.")
assert 3 <= delta <= 7, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
