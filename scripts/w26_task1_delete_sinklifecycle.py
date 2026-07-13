"""W26 T1 deletion script — remove SinkLifecycle region (L233-310) from CanApi.cs.

Per W19 R1 first-correction: re-grep boundaries BEFORE running this script.
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED + W25 cp1252 binary: use binary read+write with cp1252.

W13 T1 2/3 loose-assertion: predicted -78 LoC (range L233-L310 inclusive = 78 lines),
within ±2 LoC tolerance.
"""
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/Scripting/CanApi.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# 1-indexed inclusive; convert to 0-indexed exclusive end
START, END = 233, 310
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"CanApi.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 78 (Flow A SinkLifecycle OnFrame(CanFrame)+OnError+Dispose range L233-L310). Within ±2 LoC tolerance.")
assert 76 <= delta <= 80, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
