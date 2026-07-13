"""W33 T2 deletion script — remove 5 mutators from SequenceLibrary.cs (post-T1 boundaries).

Per W19 R1 first-correction ENHANCED: re-grep boundaries BEFORE running this script
+ post-failure recovery procedure baked in (W31 T2 + W32 T2 + W33 T1 lessons learned).
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from main HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED: use binary read+write with cp1252.

POST-T1 boundaries (after W33 T1 deleted EnsureLoaded + LoadUnlocked + SaveUnlocked):
  1. Count: L177-L188 (12 LoC, unchanged from main HEAD)
  2. Remove: L161-L175 (15 LoC, unchanged)
  3. Add: L140-L159 (20 LoC, unchanged)
  4. Save: L124-L138 (15 LoC, unchanged)
  5. Load: L110-L122 (13 LoC, unchanged)

Process in REVERSE ORDER (highest line first) to keep line numbers stable.

W33 T2 2/3 loose-assertion: predicted -75 LoC (5 mutators: Load 13 + Save 15 + Add 20 + Remove 15 + Count 12).
Within ±2 LoC tolerance.

**W19 R1 LESSON ENHANCED**: if delta is outside ±2 tolerance, recovery procedure is:
  1. git checkout src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs (restore from git)
  2. Re-grep post-T1 boundaries via grep -n
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

# Verify post-T1 boundaries first
print(f"Total lines: {len(lines)}")
if len(lines) > 109:
    print(f"L110 (Load xmldoc): {lines[109].strip()}")
if len(lines) > 114:
    print(f"L115 (Load body): {lines[114].strip()}")
if len(lines) > 123:
    print(f"L124 (Save xmldoc): {lines[123].strip()}")
if len(lines) > 139:
    print(f"L140 (Add xmldoc): {lines[139].strip()}")
if len(lines) > 160:
    print(f"L161 (Remove xmldoc): {lines[160].strip()}")
if len(lines) > 176:
    print(f"L177 (Count xmldoc): {lines[176].strip()}")
if len(lines) > 188:
    print(f"L188 (Count body close): {lines[187].strip()}")
print()

# Process in REVERSE ORDER:
# First pass: delete Count (L177-L188)
new_lines = lines[:176] + lines[188:]
# Second pass: delete Remove (L161-L175)
new_lines = new_lines[:160] + new_lines[175:]
# Third pass: delete Add (L140-L159)
new_lines = new_lines[:139] + new_lines[159:]
# Fourth pass: delete Save (L124-L138)
new_lines = new_lines[:123] + new_lines[138:]
# Fifth pass: delete Load (L110-L122)
new_lines = new_lines[:109] + new_lines[122:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

before = 203
after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"SequenceLibrary.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 75 (Load 13 + Save 15 + Add 20 + Remove 15 + Count 12). Within ±2 LoC tolerance.")
assert 73 <= delta <= 77, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
