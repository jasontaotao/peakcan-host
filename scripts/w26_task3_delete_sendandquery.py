"""W26 T3 deletion script — remove SendAndQuery region (3 non-contiguous ranges) from CanApi.cs.

Per W19 R1 first-correction: re-grep boundaries BEFORE running this script.
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED + W25 cp1252 binary: use binary read+write with cp1252.

3 non-contiguous ranges:
- Send + xmldoc: L56-L98 (43 LoC)
- IsConnected:    L110-L110 (1 LoC)
- GetChannelId:   L148-L148 (1 LoC)
Total = 45 LoC deletion.

W13 T1 2/3 loose-assertion: predicted -45 LoC (within ±2 LoC tolerance).
"""
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/Scripting/CanApi.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# 1-indexed inclusive ranges; delete in reverse order (largest first) to preserve line offsets
RANGES = [
    (56, 98),    # Send + xmldoc
    (110, 110),  # IsConnected
    (148, 148),  # GetChannelId
]

before = len(lines)
# Process in reverse so earlier line numbers stay valid
for start, end in sorted(RANGES, key=lambda r: -r[0]):
    lines = lines[:start - 1] + lines[end:]
new_text = "".join(lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"CanApi.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 45 (Flow C SendAndQuery 3 non-contiguous ranges). Within ±2 LoC tolerance.")
assert 43 <= delta <= 47, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
