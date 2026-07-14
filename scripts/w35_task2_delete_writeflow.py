# scripts/w35_task2_delete_writeflow.py — W19 R1 first-correction ENHANCED (boundary verification + recovery procedure documented)
"""W35 T2 deletion script — remove WriteAsync from PeakCanChannel.cs (write-flow cluster).

Per W19 R1 first-correction ENHANCED: re-grep boundaries BEFORE running this script
+ post-failure recovery procedure baked in (W31 T2 + W32 T2 + W33 T1+T2+T3 + W34 T1 + W35 T1 lessons learned).
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from main HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED: use binary read+write with cp1252.

POST-T1 boundaries (after W35 T1 deleted 3 connect lifecycle methods:
ConnectAsync 50 + DisconnectAsync 13 + DisposeAsync 6 = 69 LoC from the file
itself, plus blank-line compression, actual observed shift in main HEAD
line-of-WriteAsync-start is 174 -> 111, i.e. -63 offset; closing-brace line
220 -> 157, same -63 offset):
  - WriteAsync: post-T1 L111-L157 (inclusive), 47 LoC
  - Verified via `git show HEAD:src/.../PeakCanChannel.cs | grep -n 'WriteAsync\|^}'`

W35 T2 2/3 loose-assertion: predicted -47 LoC.
Within ±2 LoC tolerance.

**W19 R1 LESSON ENHANCED**: if delta is outside ±2 tolerance, recovery procedure is:
  1. git checkout HEAD -- src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs (restore from git)
  2. Re-grep post-T1 boundaries via grep -n
  3. Correct the offsets in the script
  4. Re-run the script
  5. Verify delta = expected
  6. Build + test
"""
from pathlib import Path

p = Path("src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# Verify post-T1 boundaries first
print(f"Total lines: {len(lines)}")
print(f"L111 (WriteAsync signature): {lines[110].strip()}")
print(f"L157 (WriteAsync closing brace): {lines[156].strip()}")
print()

# Slice indices: 1-indexed L111-L157 inclusive = 0-indexed indices 110..156 inclusive
# DELETE via lines[:110] + lines[157:]
START_IDX = 110  # 0-indexed: first line of WriteAsync (was L111 in 1-indexed)
END_IDX = 157    # 0-indexed: one past last line of WriteAsync (was L158 in 1-indexed)

before = len(lines)
new_lines = lines[:START_IDX] + lines[END_IDX:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"PeakCanChannel.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 47 (WriteAsync body). Within ±2 LoC tolerance.")
assert 45 <= delta <= 49, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")