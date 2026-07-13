"""W32 T2 deletion script — remove 3 query methods from DbcApi.cs (post-T1 boundaries).

Per W19 R1 first-correction ENHANCED: re-grep boundaries BEFORE running this script
+ post-failure recovery procedure baked in (W31 T2 first-run failure learning).
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from main HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED + W31 cp1252 binary: use binary read+write with cp1252.

POST-T1 boundaries (after W32 T1 deleted Load xmldoc + body L53-L148):
  1. GetMessages: L114-L130 (17 LoC, shifted by -96 from main HEAD L210-L226)
  2. GetSignal: L91-L112 (22 LoC, shifted by -90 from main HEAD L187-L208)
  3. Decode: L54-L89 (36 LoC, shifted by -96 from main HEAD L150-L185)

Process in REVERSE ORDER (highest line first) to keep line numbers stable.

W32 T2 2/3 loose-assertion: predicted -75 LoC (3 query methods: Decode 36 + GetSignal 22 + GetMessages 17).
Within ±2 LoC tolerance.

**W19 R1 LESSON ENHANCED**: if delta is outside ±2 tolerance, recovery procedure is:
  1. git checkout src/PeakCan.Host.App/Services/Scripting/DbcApi.cs (restore from git)
  2. Re-grep post-T1 boundaries via grep -n
  3. Correct the offsets in the script
  4. Re-run the script
  5. Verify delta = expected
  6. Build + test
"""
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/Scripting/DbcApi.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# Verify post-T1 boundaries first
print(f"Total lines: {len(lines)}")
if len(lines) > 53:
    print(f"L54-L89 (Decode): line 54 = {lines[53].strip()}, line 89 = {lines[88].strip()}")
if len(lines) > 90:
    print(f"L91-L112 (GetSignal): line 91 = {lines[90].strip()}, line 112 = {lines[111].strip()}")
if len(lines) > 113:
    print(f"L114-L130 (GetMessages): line 114 = {lines[113].strip()}, line 130 = {lines[129].strip()}")
print()

# Process in REVERSE ORDER:
# First pass: delete GetMessages (L114-L130)
new_lines = lines[:113] + lines[130:]
# Second pass: delete GetSignal (now at L91-L112 after first pass)
new_lines = new_lines[:90] + new_lines[112:]
# Third pass: delete Decode (now at L54-L89 after second pass)
new_lines = new_lines[:53] + new_lines[89:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

before = 183
after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"DbcApi.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 75 (Decode 36 + GetSignal 22 + GetMessages 17). Within ±2 LoC tolerance.")
assert 73 <= delta <= 77, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
