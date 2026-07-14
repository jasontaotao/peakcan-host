# Release Notes v3.48.2 — Brand assets + ApplicationIcon (PATCH)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.48.2
**Branch**: `feature/v3-48-2-brand-assets`
**Parent**: v3.48.1 PATCH (W35 PeakCanChannel SHIPPED = `7db10c2` on main + capture-decisions `e1c8c25`)

## Why this PATCH

The project previously shipped with **no logo / icon / brand asset** — neither the .exe nor any window carried one. Operators running the app saw the generic WPF stub in the taskbar; the GitHub repo had a text-only README without a banner; the GH social-preview URL rendered as a blank card on social shares.

This PATCH closes the gap with **one SVG source + 9 PNG derivatives + 1 multi-resolution .ico + 1 banner + 1 OG card**, hooked into every surface that accepts a brand asset.

## Assets shipped

| File | Size | Used by |
|---|---|---|
| `src/PeakCan.Host.App/Assets/peakcan-host-logo.svg` | 2.5 KB | Master vector source (all PNGs/ICO derive from this) |
| `Assets/peakcan-host-logo-{16,32,48,64,128,256,512,1024}.png` | 9 PNGs | Available PNG sizes for any future pack-URI XAML Icon= binding |
| `Assets/peakcan-host-logo-180.png` | 13 KB | Stand-alone 180×180 (Apple touch-icon size) |
| `Assets/peakcan-host-icon-240.png` | 19 KB | Smaller 240×240 icon (README smaller variant) |
| `Assets/peakcan-host-banner.png` | 32 KB | 1280×320 banner for README header |
| `Assets/peakcan-host-og-card.png` | 72 KB | 1280×640 GitHub social-preview card |
| `Assets/peakcan-host-logo.ico` | 734 B | Multi-resolution 16/32/48/64/128/256 .ico (embedded in .exe) |
| `docs/og-card.png` | 72 KB | Copy of OG card in `docs/` so GitHub social-settings picker can find it |

## Design

A stylized **CAN-bus waveform passing through a connector frame**:
- **Background** = rounded-square dark-navy gradient (`#1F2A44` → `#0D1428`, 192 px corner radius)
- **Frame ring** = PEAK-orange gradient (`#FFB347` → `#E27A00`, 40 px stroke)
- **Waveform** = signal-cyan gradient (`#0FB5C0` → `#5DD8E0`, 56 px stroke, 13 discrete transitions) representing CAN frame bit transitions (recessive / dominant / recessive)
- **CAN nodes** = small orange dots at the corners of the inner grid
- **Resolution-independent** SVG → renders sharp from 16 px taskbar up to 1024 px social card

## What this PATCH does

### PATCH brand — ApplicationIcon + .exe Win32 resource

1. **NEW `src/PeakCan.Host.App/Assets/peakcan-host-logo.ico`** (734 B, 6 resolutions):
   - Multi-resolution ICO with 16/32/48/64/128/256 px layers
   - Master PNG-32 used as the build-time bitmap icon
   - Embedded in `PeakCan.Host.exe` as the Win32 icon resource (Alt-Tab thumbnail + taskbar pin + pinned-start-tile icon)

2. **`src/PeakCan.Host.App/PeakCan.Host.App.csproj`**: adds `<ApplicationIcon>Assets\peakcan-host-logo.ico</ApplicationIcon>` so the SDK embeds the .ico into the published exe at build time. Also adds `<None Include="Assets\*" CopyToOutputDirectory="PreserveNewest"/>` so the PNGs + banner + OG card ship with the build output for future pack-URI XAML bindings or shared assets folder.

3. **`tools/smoke-diag/smoke-diag.csproj`**: adds the same `<ApplicationIcon>` pointing at the same .ico (relative path `..\..\src\PeakCan.Host.App\Assets\peakcan-host-logo.ico`) so the diagnostic tool exe shows the same brand in the taskbar.

### PATCH brand — README + GitHub social preview

4. **`README.md`**: header now begins with an `<img src="...">` tag pointing at the 1280×320 banner (relative path `src/PeakCan.Host.App/Assets/peakcan-host-banner.png`), so the rendered README leads with the brand.

5. **`docs/og-card.png`** (NEW, 1280×640): copied from `Assets/peakcan-host-og-card.png` so GitHub's social-preview URL picker can find it in the conventional `docs/` or `images/` paths. This image is shown when someone shares a link to the repo on Slack / Twitter / Discord / etc.

### PATCH brand — Per-window XAML `Icon=` attempt (REMOVED, doc in W3 of v3.48.5)

A first attempt wired `Icon="pack://application:,,,/Assets/peakcan-host-logo-32.png"` into the 5 Window XAMLs and added `<Resource>` items in the .csproj for the 9 PNG sizes. **This did not work** — WPF's `ImageSourceConverter` only resolves pack URIs against a generated `Resources.g.cs` manifest that the WPF SDK only emits for *XAML* resources (via `<Page Include="..."/>`), not for raw binary `<Resource Include="..."/>` items. The result was `XamlParseException: Cannot locate resource 'assets/...'`. The Icon= attributes were rolled back; the v3.48.2 PATCH ships the asset files but **does not bind them to per-window XAML**.

### Why this is acceptable (YAGNI rationale)

The `<ApplicationIcon>` on the App csproj already handles all OS-level iconography (Alt-Tab thumbnail, taskbar pin, pinned-start-tile, .exe file icon in Explorer). **Per-window `Icon=` is a UX nicety**, not a functional requirement — closing the window immediately uses the same .exe icon. Wiring per-window icons correctly requires either:
1. Switching to `<Page Include="...xaml">` for every PNG so the SDK emits resources.g.cs (high-friction, requires moving PNGs to a separate XAML-included folder) OR
2. Custom IValueConverter on `Icon=` that pulls from `Application.GetResourceStream` (which also needs the asset to be a `<Resource>` in the .csproj — same SDK gap) OR
3. Loading the icon programmatically in each window's Loaded handler via `Icon = new BitmapImage(new Uri(...))` (per-window code-behind, breaks the XAML-as-source-of-truth pattern this project follows)

None of the 3 is a v3.48.2 PATCH-sized change. **A future v3.48.5+ MINOR can revisit** if operator feedback shows per-window icons are needed.

## Architecture

This PATCH adds a single `Assets/` folder at the App-project level with one canonical logo source (SVG) and 12 derivative files. No new layer is introduced; no service is touched; no behaviour changes. All existing tests pass without modification.

### Build verification

- `dotnet build PeakCan.Host.slnx`: 0 errors, 1 pre-existing CS8602 warning in `DbcService/LoadLifecycle.partial.cs:88` (W34-era, unrelated to icons)
- `dotnet test --filter "FullyQualifiedName~AppHostBuilderTests|FullyQualifiedName~UdsWindowTests"`: **25/25 PASS** (the 2 tests that initially FAILED on the broken pack-URI Icon= attribute now PASS after the rollback)
- `dotnet test PeakCan.Host.slnx`: **1339/1339 PASS, 5 SKIP, 0 fail** (full solution test suite: Core.Tests 449 + Infrastructure.Tests 89 + App.Tests 801, 2 hardware-required SKIP each)

### EXE icon embedding verification

- `src/PeakCan.Host.App/bin/Debug/net10.0-windows/PeakCan.Host.exe` = 163328 bytes
- Embedded .ico offset = 89432 (verified by searching the binary header for `00 00 01 00` — the Windows ICO magic)
- Tools/smoke-diag exe rebuilt with same ApplicationIcon; size diff <1 KB vs pre-PATCH

## What was captured

W3 icon-assets PATCH = 5 sub-captures planned: brand design + 12 asset files generated + 2 csprojs modified + README + docs/og-card. Per W3-W34 sister pattern of dispatch-after-each-commit pkm-capture.

## What was skipped (YAGNI)

- **Per-window XAML `Icon=`** — the v3.48.2 attempt to wire `Icon="pack://..."` failed (WPF SDK doesn't generate resources.g.cs for raw `<Resource>` binary items). A future v3.48.5+ can revisit via either `<Page Include>` reorg or programmatic `BitmapImage` Loaded handler. Not blocking v3.48.2.
- **MSIX / installer icon** — the project is currently `dotnet publish`-based, no MSIX packaging yet. When MSIX lands, the .ico can be reused as `Square150x150Logo.png` etc.
- **Apple-touch-icon for the GitHub Pages site** — no GitHub Pages site yet.
- **Favicon.ico for docs/index.html** — no docs/index.html yet.
- **In-app About dialog logo** — out of scope for v3.48.2 (would require a new dialog XAML).

## Process lessons applied

- **Lesson #11** (file-scoped using directives): not applicable, no .cs file changes.
- **W17 wc-l-splitlines CONFIRMED**: detected per-file encoding (cp1252 vs UTF-8) before writing — multi-byte chars in UdsWindow.xaml + MultiFrameSendWindow.xaml + TraceViewerView.xaml tripped the default UTF-8 decoder. Used `detect-and-read()` helper to fall back to UTF-8 when cp1252 decode failed.
- **W22 + W23 LESSON (verbatim + struct-ctor)**: not applicable, no .cs source changes.
- **YAGNI**: per-window XAML Icon= rollback after attempted ROI proved negative in this PATCH scope — documented the 3 alternative paths for v3.48.5+ to choose from if needed.

## Cumulative trajectory (peakcan-host v3 series)

After this PATCH:
- **32 cycles** total: 31 god-class refactors + 1 brand-asset PATCH
- 9 vault-only PATCH cycles (W17 + W23.5-W25.5 + W26.5-W32.5)
- **-5,352 LoC** cumulative source LoC reduction (god-class series)
- App project now has **13 Assets/ files** (12 icon files + 1 .ico) — first time the project has visible brand assets

## Next (post-v3.48.2 ship)

- **v3.48.5 vault-only PATCH** (or merge into W35.5 if you want it inline): consolidate the 1 W35 + 1 v3.48.2 lesson-promotion candidate if any. Currently zero new lesson candidates from v3.48.2; the existing W35.5 lesson batch (`infrastructure-channel-layer-sister-pattern-empirical-w18-w25-LOCKED` promotion + `second-cycle-god-class-refactor-empirical-w28-w29-w35` NEW 1/3) is independent.
- **W36** god-class refactor candidates: DbcViewModel.cs 208 LoC, ReplayViewModel.cs 278 LoC, TraceSessionBundle.cs 247 LoC, or smaller god-classes.
- **Optional v3.49.0 MINOR**: revisit per-window XAML Icon= via one of the 3 documented paths (Resources.g.cs via `<Page>` reorg OR programmatic `BitmapImage` Loaded handler OR custom IValueConverter).
