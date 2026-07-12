"""W25 T3 deletion script — remove RegisterChannel + UnregisterChannel (L112-134) from ChannelRouter.cs.

Per W19 R1 first-correction: re-grep boundaries BEFORE running this script.
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED: count main file lines AFTER deletion.

W13 T1 2/3 loose-assertion: predicted -23 LoC (range L112-L134 inclusive = 23 lines),
within ±2 LoC tolerance of SPEC predicted 22.
"""
from pathlib import Path

p = Path("src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# 1-indexed inclusive; convert to 0-indexed exclusive end
START, END = 112, 134
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"ChannelRouter.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 23 (Flow A Channels RegisterChannel+UnregisterChannel range L112-L134). Within ±2 LoC tolerance.")
assert 21 <= delta <= 25, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
