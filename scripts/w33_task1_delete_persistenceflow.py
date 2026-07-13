"""W33 T1 deletion script — remove 3 helpers from SequenceLibrary.cs.

Per W19 R1 first-correction ENHANCED: re-grep boundaries BEFORE running this script
+ post-failure recovery procedure baked in (W31 T2 + W32 T2 lessons learned).
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from main HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED + W31+W32 cp1252 binary: use binary read+write with cp1252.

W33 has 3 non-contiguous regions to delete:
  1. SaveUnlocked: L212-L232 (21 LoC)
  2. LoadUnlocked: L196-L210 (15 LoC)
  3. EnsureLoaded: L190-L194 (5 LoC)

Process in REVERSE ORDER (highest line first) to keep line numbers stable.

W33 T1 2/3 loose-assertion: predicted -41 LoC (3 helpers: SaveUnlocked 21 + LoadUnlocked 15 + EnsureLoaded 5).
Within ±2 LoC tolerance.

**W19 R1 LESSON ENHANCED**: if delta is outside ±2 tolerance, recovery procedure is:
  1. git checkout src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs (restore from git)
  2. Re-grep post-T0 boundaries via grep -n
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

# Verify post-T0 boundaries first
print(f"Total lines: {len(lines)}")
if len(lines) > 189:
    print(f"L190 (EnsureLoaded): {lines[189].strip()}")
if len(lines) > 195:
    print(f"L196 (LoadUnlocked): {lines[195].strip()}")
if len(lines) > 211:
    print(f"L212 (SaveUnlocked): {lines[211].strip()}")
print()

# Process in REVERSE ORDER:
# First pass: delete SaveUnlocked (L212-L232)
new_lines = lines[:211] + lines[232:]
# Second pass: delete LoadUnlocked (now at L196-L210 after first pass)
new_lines = new_lines[:195] + new_lines[210:]
# Third pass: delete EnsureLoaded (now at L190-L194 after second pass)
new_lines = new_lines[:189] + new_lines[194:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

before = 244
after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"SequenceLibrary.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 41 (EnsureLoaded 5 + LoadUnlocked 15 + SaveUnlocked 21). Within ±2 LoC tolerance.")
assert 39 <= delta <= 43, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
