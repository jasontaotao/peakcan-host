"""W31 T1 deletion script — remove LoadAsync (L152-L182) + Reset (L191-L209) from ReplayService.cs.

Per W19 R1 first-correction: re-grep boundaries BEFORE running this script.
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from main HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED + W25/W27/W28/W29/W30 cp1252 binary: use binary read+write with cp1252.

W31 has 2 non-contiguous regions to delete. Process in REVERSE ORDER (highest line first)
to keep line numbers stable for the second region.

W31 T1 2/3 loose-assertion: predicted -50 LoC (LoadAsync 31 + Reset 19).
Within ±2 LoC tolerance.
"""
from pathlib import Path

p = Path("src/PeakCan.Host.Core/Replay/ReplayService.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# Process in reverse order: Reset (L191-L209) first, then LoadAsync (L152-L182)
START_2, END_2 = 191, 209  # Reset (process first)
new_lines = lines[:START_2 - 1] + lines[END_2:]
START_1, END_1 = 152, 182  # LoadAsync (then this)
new_lines = new_lines[:START_1 - 1] + new_lines[END_1:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

before = 265
after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"ReplayService.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 50 (LoadAsync 31 + Reset 19). Within ±2 LoC tolerance.")
assert 48 <= delta <= 52, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
