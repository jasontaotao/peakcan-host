# Published artifacts

This directory contains the `dotnet publish` output for the
PeakCan-Host WPF app.

## Files

- `PeakCan.Host.exe` — single-file self-contained Windows binary
  (~66 MB compressed). The .NET 10 runtime is embedded; no separate
  runtime install is required.
- `PeakCan.Host.pdb` — debug symbols (sourcelink + portable PDB)
  for crash analysis. Not needed at runtime.
- `PeakCan.Host.Core.pdb` / `PeakCan.Host.Infrastructure.pdb` —
  debug symbols for the support assemblies. The actual Core /
  Infrastructure DLLs are bundled inside `PeakCan.Host.exe` via
  `IncludeNativeLibrariesForSelfExtract`.

## Build command

```bash
dotnet publish src/PeakCan.Host.App -c Release -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o artifacts/win-x64/
```

## Smoke launch

```bash
artifacts/win-x64/PeakCan.Host.exe
```

Expected: a 1280×720 WPF window appears with a menu (File / View),
a toolbar (channel list + Probe / Connect buttons), and an empty
Trace view. The status bar reads "Ready". Without a PEAK device
plugged in, clicking **Probe** displays "No PEAK hardware detected".

## Hardware smoke

With a PEAK PCAN-USB FD connected and the PEAK driver installed
([PEAK-System download page](https://www.peak-system.com/PCAN-USB-FD.366.0.html)):

1. **Probe** → status bar reads "PEAK PCAN-USB FD detected on USB1".
2. **Connect** → status bar reads "Connected to USB1 (CAN FD 1 Mbps)".
3. **File ▸ Open DBC...** → pick a `.dbc` file → DBC tab populates.
4. **View ▸ Signals** → decoded values appear as frames arrive.
5. **View ▸ Stats** → 1 Hz OxyPlot chart updates.
6. **View ▸ Send** → enter ID + data, click **Send** → frame on bus.

## Known limitations (MVP v0.1.0)

- Single hardcoded channel (PCAN-USB FD first handle `0x51`).
  Multi-channel enumeration is v1.1.
- No recording / playback (v1.1 ASC / CSV export).
- No frame filters (v1.1).
- No cyclic transmission (v1.1).
- Multiplexor / multiplexed signals are not decoded (v1.1).
