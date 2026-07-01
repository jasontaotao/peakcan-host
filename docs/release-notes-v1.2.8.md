# Release Notes — PeakCan Host v1.2.8

**Date:** 2026-06-26

## Summary

v1.2.8 is a 2-commit PATCH that fixes two display bugs in the
Signal view: the "Raw" column showed the wrong value, and the
"Unit" column showed garbled characters for non-ASCII DBC
files.

## Bug 1 — Raw column showed the wrong value

The Signal view's "Raw" column was bugged since v0.8.0: it
formatted the **physical** value (raw * factor + offset) as
"0x" + F0, which produced nonsense for any signal with
non-default factor/offset.

Examples:
- A signal with Factor=0.1 and Offset=0 on raw bits=0 would
  show `Raw: 0x0` but `Physical: 0` — the F0 format
  rounded the small physical to "0" and the "0x" prefix was
  on the wrong value entirely.
- A signal with Factor=0.001 and Offset=0.001 on raw bits=0
  would show `Raw: 0x0` while `Physical: 0.001` — the two
  columns disagreed and neither was the wire bit pattern.

The same root cause broke **multiplexor matching**: the
previous code did `muxValue = (ushort)SignalDecoder.Decode(span, muxSig)`,
which casts the physical value (already factor+offset applied)
to ushort. For a multiplexor with Factor=0.1, Offset=0 on raw
bits=5, physical=0.5, cast to ushort=0 — so a sub-signal
expecting mux=5 would be matched against mux=0. Multiplexor
matching must use the wire-level raw bit pattern, not the
scaled engineering value, per DBC convention.

### Fix

`SignalDecoder` gains a new public API:

```csharp
public static ulong DecodeRaw(ReadOnlySpan<byte> data, Signal signal);
```

Returns the unsigned bit pattern (ulong), masked to the
signal's bit width. For `ValueType.Signed` the bit pattern is
the two's-complement representation (e.g. an 8-bit signed
-1 returns `0xFF`, not `0xFFFFFFFFFFFFFFFF`). For
`ValueType.Float` the lower 32 bits are the IEEE-754
single-precision bit pattern.

`ApplyFrame` now calls both `DecodeRaw` (for the Raw column
and for `muxValue` casting) and `Decode` (for the Physical
column). The two columns now agree: `Raw` shows the wire bit
pattern in hex (`0x5` for raw=5), `Physical` shows the
engineering value. Multiplexor comparison uses `DecodeRaw`,
restoring correct mux matching on signals with non-default
factor/offset.

## Bug 2 — Unit column showed garbled text

The Signal view's "Unit" column (and any other DBC field
containing non-ASCII characters — `VAL_` descriptions,
comment blocks, etc.) showed garbled text on zh-CN / ja-JP /
ko-KR Windows users who saved their DBC files in the system
default code page (GBK/CP936, CP932, CP949). The single-
encoding `File.ReadAllTextAsync(path)` call used the .NET
default which on .NET 6+ is UTF-8 with no BOM detection; for
a GBK-saved DBC, the multi-byte Chinese characters were
decoded as 2-3 separate Latin-1 chars, producing
"ä¸åº¦" / "é«åº¦" / random boxes in the Unit column.

### Fix

New `DbcService.ReadDbcTextAsync` helper that:

1. **Detects BOM** (UTF-8 / UTF-16 LE/BE / UTF-32 LE/BE) and
   decodes with the matching encoding, stripping the BOM so
   it doesn't leak into the parsed text as U+FEFF.
2. **For no-BOM files**, tries strict UTF-8 first
   (`DecoderFallback.ExceptionFallback` so a single bad
   sequence is a hard failure, not silent U+FFFD
   replacement). On UTF-8 failure, **falls back to the
   system OEM code page** (`CultureInfo.CurrentCulture.
   TextInfo.OEMCodePage` — GBK/CP936 on zh-CN, CP932 on
   ja-JP, CP949 on ko-KR).

The OEM fallback handles the common case of Chinese /
Japanese / Korean DBCs that were saved in the system default
code page (the majority of legacy OEM DBCs in the wild). The
strict UTF-8 check first means a UTF-8 DBC with even one
wrong byte is correctly reported as a parse failure, not
silently misread.

## Why this is a PATCH

Both fixes are scoped to display correctness. The public API
of `SignalDecoder` gains a new method but does not break any
existing callers (`Decode` is unchanged). `DbcService` is
the only consumer of the DBC file content. No new public
configuration knobs.

## Tests

548 pass + 6 SKIP + 0 fail (no test count change). The
`DecodeRaw` method is exercised indirectly via the existing
`ApplyFrame` path. The encoding loader is exercised by the
existing DBC load tests (which use ASCII fixtures).

## Files changed

- `src/PeakCan.Host.Core/Dbc/SignalDecoder.cs` — new
  `DecodeRaw` public method returning ulong bit pattern
- `src/PeakCan.Host.App/ViewModels/SignalViewModel.cs` —
  `ApplyFrame` uses `DecodeRaw` for `Raw` column + muxValue
  cast; `Decode` for `Physical` column
- `src/PeakCan.Host.App/Services/DbcService.cs` — new
  `ReadDbcTextAsync` helper (BOM-aware + UTF-8-with-fallback)

## Known issue (carried over)

Stats tab `OxyPlot` `Legend` is empty by default in OxyPlot
2.2.0 (no legend rendering for the FPS / bus-load series
labels). Replacement deferred to **v1.2.9 PATCH** (small)
— add `PlotModel.Legends.Add(new Legend { ... })` with
`LegendPlacement=Outside` so the series names render.

## Next work

1. **v1.2.9 PATCH** (small): add `Legend` to
   `StatsViewModel.PlotModel.Legends` so the FPS / bus-load
   series names render in the chart legend.
2. **v1.3.0 MINOR (OEM IKeyDerivationAlgorithm + OxyPlot
   full replacement)** — blocked on OEM list for the
   algorithm work; OxyPlot chart can be filed as a separate
   task.

## Ship mechanics

`git -c http.proxy="http://127.0.0.1:7897" push origin main`
(proxy alive; direct connection reset on first attempt) +
`git tag -a v1.2.8 -m "..."` + `git push origin v1.2.8` +
`gh release create v1.2.8 --title ... --notes-file
docs/release-notes-v1.2.8.md`.