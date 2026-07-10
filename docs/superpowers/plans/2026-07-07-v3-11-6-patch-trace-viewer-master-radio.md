# peakcan-host v3.11.6 PATCH — Trace Viewer master-radio XAML parse exception

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the `XamlParseException` (`MarkupExtensionDynamicOrBindingOnClrProp`) thrown after the first `.asc` load. The exception is caused by a `{Binding ...}` markup extension nested inside another binding's `ConverterParameter` in the per-source master-radio XAML — WPF forbids markup extensions in `ConverterParameter` because it's a non-`DependencyProperty` `object`.

**Architecture:** Replace the illegal nested-Binding pattern with a clean `DataTrigger`-driven binding. The per-source `RadioButton.IsChecked` binds directly to `SourceId`, and a `DataTrigger` compares it to the `Window`'s `MasterSourceId` via the `AncestorType=Window` + `DataContext` chain — no `ConverterParameter` binding needed. The `MasterRadioConverter` is deleted (no longer needed). The `SetMasterCommand` stays unchanged.

**Tech Stack:** WPF .NET 10 + CommunityToolkit.Mvvm 8.x. Pure XAML change + VM command stays; no DI changes.

## Background

`src/PeakCan.Host.App/Views/TraceViewerView.xaml:109-116`:
```xml
<RadioButton GroupName="MasterGroup" Margin="0,0,6,0" VerticalAlignment="Center"
             IsChecked="{Binding SourceId,
                                 Converter={StaticResource MasterRadio},
                                 ConverterParameter={Binding DataContext.MasterSourceId,
                                                     RelativeSource={RelativeSource AncestorType=Window}}}"
             Command="{Binding DataContext.SetMasterCommand, RelativeSource={RelativeSource AncestorType=Window}}"
             CommandParameter="{Binding SourceId}"
             ToolTip="Make this the master (clock source)" />
```

The `ConverterParameter={Binding ...}` is the WPF antipattern. `ConverterParameter` is an `object` (not a `DependencyProperty`), and WPF's parser throws `MarkupExtensionDynamicOrBindingOnClrProp` the moment the `DataTemplate` instantiates and resolves the nested markup extension. This is why the error only manifests **after the first `.asc` load** — the per-source legend strip is `Visibility="{Binding HasSources, ...}"` and only instantiates when `Sources` collection gets populated.

## Global Constraints

- **No production regression** — `SetMasterCommand` stays exactly the same. Only the XAML representation of "which source is master" changes.
- **Delete `MasterRadioConverter`** — the new `DataTrigger` pattern replaces the converter. The `MasterRadio` static resource key registration in `App.xaml:20` is removed at the same time.
- **No new tests** — this is a pure XAML representation change; the runtime behavior (click → `SetMasterCommand.Execute(sourceId)` → `MasterSourceId` updates → radio reflects master) is unchanged. Verification is via manual smoke + a unit test that the `MasterRadioConverter` is removed from App.xaml's resource dictionary (regression check that no XAML still references it).
- **No new public API surface** — `SetMasterCommand` source-generated name unchanged.
- **No schema changes, no .tmtrace changes, no DBC changes, no DI changes.**
- **Test delta target**: 1282 + 5 SKIP / 0 fail → 1282 + 5 SKIP / 0 fail (0 net — pure XAML refactor).
- **Plan: only fix the master-radio XAML. Do NOT touch unrelated review backlog (H3/H6/M1-M13 deferred to v3.12.0 MINOR per v3.11.3 release notes).**

---

## File Structure

### Modify
- `src/PeakCan.Host.App/Views/TraceViewerView.xaml` — replace the RadioButton block (lines 109-116) with a `DataTrigger`-driven binding.
- `src/PeakCan.Host.App/App.xaml` — remove the `MasterRadioConverter` resource registration (line 20).
- `tests/PeakCan.Host.App.Tests/Composition/AppHostBuilderTests.cs` (if it references `MasterRadioConverter`) — likely no change since the converter was internal.

### Delete
- `src/PeakCan.Host.App/Composition/Converters/MasterRadioConverter.cs` — no longer needed.

### No-op files
- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` — `SetMasterCommand` + `MasterSourceId` stay unchanged. The `MasterRadioConverter` reference was XAML-only, not used in the VM.

---

### Task 1: Add an XAML-only test (smoke + regression guard)

**Files:**
- Modify: `tests/PeakCan.Host.App.Tests/Composition/ViewSwitcherTests.cs` (or create new `tests/PeakCan.Host.App.Tests/Views/TraceViewerViewXamlTests.cs`).

**Consumes:** `TraceViewerView.xaml` (target file), `App.xaml` (resource dictionary).
**Produces:** A test that asserts `MasterRadioConverter` is no longer referenced in any XAML file. This catches future regressions where someone re-introduces the converter.

- [ ] **Step 1: Create the test file**

Create `tests/PeakCan.Host.App.Tests/Views/TraceViewerViewXamlTests.cs` with EXACTLY this content:

```csharp
using System.IO;
using FluentAssertions;
using PeakCan.Host.App.Tests.Collections;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// v3.11.6 PATCH regression guard: the master-radio XAML antipattern
/// (nested Binding in ConverterParameter) caused a XamlParseException
/// after the first .asc load. This test asserts that no XAML file in the
/// production tree references the deleted <c>MasterRadioConverter</c>
/// or the deleted <c>MasterRadio</c> resource key, so the antipattern
/// cannot be re-introduced silently in a future PATCH.
///
/// STA-bound (XAML inspection is fine on MTA, but the test class joins
/// <see cref="WpfAppTestCollection"/> for consistency with other
/// TraceViewer-related test classes).
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public class TraceViewerViewXamlTests
{
    private static readonly string[] TrackedFiles =
    {
        "src/PeakCan.Host.App/Views/TraceViewerView.xaml",
        "src/PeakCan.Host.App/Windows/UdsWindow.xaml",        // v3.11.3 sibling
        "src/PeakCan.Host.App/Windows/MultiFrameSendWindow.xaml",
        "src/PeakCan.Host.App/Composition/Converters/MasterRadioConverter.cs",
        "src/PeakCan.Host.App/App.xaml",
    };

    [Fact]
    public void NoProductionFile_References_MasterRadioConverter_Or_ResourceKey()
    {
        // Locate repo root: walk up from this test file until we find
        // the .git directory. This avoids hard-coding an absolute path
        // (CI uses a different repo root than local dev machines).
        var repoRoot = FindRepoRoot();
        repoRoot.Should().NotBeNull("test must be able to locate the repo root");

        foreach (var relPath in TrackedFiles)
        {
            var full = Path.Combine(repoRoot!, relPath);
            if (!File.Exists(full))
            {
                // UdsWindow.xaml is v3.11.3 sibling; skip if not on disk yet.
                continue;
            }
            var content = File.ReadAllText(full);
            content.Should().NotContain("MasterRadio",
                $"{relPath} must not reference MasterRadio (deleted in v3.11.6 PATCH; nested Binding in ConverterParameter antipattern)");
            content.Should().NotContain("MasterRadioConverter",
                $"{relPath} must not reference MasterRadioConverter (deleted in v3.11.6 PATCH)");
        }
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
```

Notes:
- The test uses `AppContext.BaseDirectory` to find the test assembly's location, then walks up looking for `.git/`. This is portable across CI + dev machines.
- The 5 tracked files include `MasterRadioConverter.cs` itself (the file should be deleted in Task 2) — the `File.Exists` check skips it gracefully if deletion already happened.
- The test joins `WpfAppTestCollection` for consistency with other TraceViewer-related test classes even though it doesn't need STA (file IO only).

- [ ] **Step 2: Verify the test compiles**

Run: `dotnet build tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo`
Expected: 0 errors.

- [ ] **Step 3: Run the test — expected to FAIL (TraceViewerView.xaml still references MasterRadio)**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~NoProductionFile_References_MasterRadioConverter" --nologo --no-build`
Expected: 1 failed. The test will find `MasterRadio` in `TraceViewerView.xaml` and fail the assertion.

Do not commit yet.

---

### Task 2: Replace the master-radio XAML + delete converter

**Files:**
- Modify: `src/PeakCan.Host.App/Views/TraceViewerView.xaml` — replace lines 109-116 with the `DataTrigger`-driven binding.
- Modify: `src/PeakCan.Host.App/App.xaml` — remove the `MasterRadio` resource registration (line 20).
- Delete: `src/PeakCan.Host.App/Composition/Converters/MasterRadioConverter.cs`

**Consumes:** The current `IsChecked="{Binding SourceId, Converter=MasterRadio, ConverterParameter={Binding DataContext.MasterSourceId, ...}}"` pattern.
**Produces:** A `DataTrigger` that toggles `IsChecked` based on `SourceId == MasterSourceId` comparison.

- [ ] **Step 1: Replace the RadioButton XAML**

In `src/PeakCan.Host.App/Views/TraceViewerView.xaml`, find lines 108-116 (the master-radio RadioButton comment + element):

```xml
                                <!-- v3.3.0 MINOR: per-source "make master" radio. Bound TwoWay via SetMasterCommand. -->
                                <RadioButton GroupName="MasterGroup" Margin="0,0,6,0" VerticalAlignment="Center"
                                             IsChecked="{Binding SourceId,
                                                                 Converter={StaticResource MasterRadio},
                                                                 ConverterParameter={Binding DataContext.MasterSourceId,
                                                                                     RelativeSource={RelativeSource AncestorType=Window}}}"
                                             Command="{Binding DataContext.SetMasterCommand, RelativeSource={RelativeSource AncestorType=Window}}"
                                             CommandParameter="{Binding SourceId}"
                                             ToolTip="Make this the master (clock source)" />
```

Replace with EXACTLY this:

```xml
                                <!-- v3.11.6 PATCH: replaced the
                                     ConverterParameter-nested-Binding pattern
                                     (caused MarkupExtensionDynamicOrBindingOnClrProp
                                     XamlParseException after first .asc load)
                                     with a DataTrigger that toggles IsChecked
                                     based on SourceId == DataContext.MasterSourceId.
                                     The MasterRadioConverter class is deleted;
                                     its App.xaml resource registration is gone too. -->
                                <RadioButton x:Name="MasterRadio" GroupName="MasterGroup"
                                             Margin="0,0,6,0" VerticalAlignment="Center"
                                             Command="{Binding DataContext.SetMasterCommand, RelativeSource={RelativeSource AncestorType=Window}}"
                                             CommandParameter="{Binding SourceId}"
                                             ToolTip="Make this the master (clock source)">
                                    <RadioButton.IsChecked>
                                        <Binding Path="SourceId" Mode="OneWay">
                                            <Binding.Converter>
                                                <!-- Inline converter: returns true when value (SourceId) equals
                                                     parameter (MasterSourceId). The DataTrigger-style binding
                                                     below sets IsChecked directly without using
                                                     ConverterParameter, avoiding the v3.11.5 root cause. -->
                                            </Binding.Converter>
                                        </Binding>
                                    </RadioButton.IsChecked>
                                    <RadioButton.Style>
                                        <Style TargetType="RadioButton">
                                            <Style.Triggers>
                                                <DataTrigger Binding="{Binding SourceId}"
                                                             Value="{Binding DataContext.MasterSourceId, RelativeSource={RelativeSource AncestorType=Window}}">
                                                    <Setter Property="IsChecked" Value="True" />
                                                </DataTrigger>
                                                <DataTrigger Binding="{Binding SourceId}"
                                                             Value="{x:Null}">
                                                    <Setter Property="IsChecked" Value="False" />
                                                </DataTrigger>
                                            </Style.Triggers>
                                        </Style>
                                    </RadioButton.Style>
                                </RadioButton>
```

**Wait** — the above is overly complex. Let me reconsider. The simpler WPF idiom is:

```xml
<RadioButton GroupName="MasterGroup"
             Margin="0,0,6,0" VerticalAlignment="Center"
             Command="{Binding DataContext.SetMasterCommand, RelativeSource={RelativeSource AncestorType=Window}}"
             CommandParameter="{Binding SourceId}"
             ToolTip="Make this the master (clock source)">
    <RadioButton.IsChecked>
        <MultiBinding Converter="{StaticResource ...}">
            <Binding Path="SourceId"/>
            <Binding Path="DataContext.MasterSourceId"
                     RelativeSource="{RelativeSource AncestorType=Window}"/>
        </MultiBinding>
    </RadioButton.IsChecked>
</RadioButton>
```

But that requires a new `IMultiValueConverter` — too much surface for a PATCH.

**Better approach** — use a `MultiBinding` only at the `IsChecked` level. Actually the simplest fix is to keep the `RadioButton.IsChecked` binding TwoWay to a NEW VM-side `IsMaster` property that already exists or can be added per-source. Let me check.

Actually the simplest of all: change the converter signature to accept `object?` as `value` and `object?` as `parameter`, and **don't use Binding in ConverterParameter**. Instead, bind `IsChecked` directly to a `IsMaster` per-row property, OR use `Tag`:

**FINAL approach** (simple, correct, no new surface): Replace the `RadioButton.IsChecked` binding with a `DataTrigger` on `Style` that compares `SourceId` to a `Tag`-based binding:

```xml
<RadioButton GroupName="MasterGroup" Margin="0,0,6,0" VerticalAlignment="Center"
             Command="{Binding DataContext.SetMasterCommand, RelativeSource={RelativeSource AncestorType=Window}}"
             CommandParameter="{Binding SourceId}"
             ToolTip="Make this the master (clock source)">
    <RadioButton.Style>
        <Style TargetType="RadioButton">
            <Style.Triggers>
                <!-- v3.11.6 PATCH: DataTrigger fires when this row's SourceId
                     matches the Window's MasterSourceId. Replaces the v3.3.0
                     binding-with-nested-ConverterParameter pattern that threw
                     MarkupExtensionDynamicOrBindingOnClrProp after .asc load. -->
                <DataTrigger Value="True">
                    <DataTrigger.Binding>
                        <MultiBinding Converter="{x:Static local:SourceIdEqualsMasterConverter.Instance}">
                            <Binding Path="SourceId"/>
                            <Binding Path="DataContext.MasterSourceId"
                                     RelativeSource="{RelativeSource AncestorType=Window}"/>
                        </MultiBinding>
                    </DataTrigger.Binding>
                    <Setter Property="IsChecked" Value="True"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </RadioButton.Style>
</RadioButton>
```

This still needs a new converter (the `IMultiValueConverter` pattern). And the XAML becomes 17 lines instead of 6.

**CLEANER FINAL approach** — add `IsMaster` as a CLR property on `TraceSource` (which already implements `INotifyPropertyChanged`), computed by the registry when sources are loaded:

But that's a bigger surface change.

**SIMPLEST CORRECT APPROACH** — use a `Tag` to thread the master id through, then a `DataTrigger`:

```xml
<RadioButton GroupName="MasterGroup" Margin="0,0,6,0" VerticalAlignment="Center"
             Tag="{Binding DataContext.MasterSourceId, RelativeSource={RelativeSource AncestorType=Window}}"
             Command="{Binding DataContext.SetMasterCommand, RelativeSource={RelativeSource AncestorType=Window}}"
             CommandParameter="{Binding SourceId}"
             ToolTip="Make this the master (clock source)">
    <RadioButton.Style>
        <Style TargetType="RadioButton">
            <Setter Property="IsChecked" Value="False"/>
            <Style.Triggers>
                <DataTrigger Binding="{Binding SourceId}" Value="{Binding RelativeSource={RelativeSource Self}, Path=Tag}">
                    <Setter Property="IsChecked" Value="True"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </RadioButton.Style>
</RadioButton>
```

Wait, `Value="{Binding ...}"` inside a `DataTrigger` is also a markup-extension-on-non-DP. Let me check the WPF docs.

Actually `DataTrigger.Value` is a plain `object` (not a DP), and `Value="..."` with a string literal is fine. But `Value="{Binding ...}"` would trigger the same exception.

The truly correct WPF pattern is to use a `MultiBinding` with a `IMultiValueConverter`:

```csharp
// New file: src/PeakCan.Host.App/Composition/Converters/SourceIdEqualsMasterConverter.cs
public sealed class SourceIdEqualsMasterConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2) return false;
        return string.Equals(values[0]?.ToString(), values[1]?.ToString(), StringComparison.Ordinal);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

```xml
xmlns:local="clr-namespace:PeakCan.Host.App.Composition.Converters"

<RadioButton GroupName="MasterGroup" Margin="0,0,6,0" VerticalAlignment="Center"
             Command="{Binding DataContext.SetMasterCommand, RelativeSource={RelativeSource AncestorType=Window}}"
             CommandParameter="{Binding SourceId}"
             ToolTip="Make this the master (clock source)">
    <RadioButton.IsChecked>
        <MultiBinding Converter="{x:Static local:SourceIdEqualsMasterConverter.Instance}">
            <Binding Path="SourceId"/>
            <Binding Path="DataContext.MasterSourceId"
                     RelativeSource="{RelativeSource AncestorType=Window}"/>
        </MultiBinding>
    </RadioButton.IsChecked>
</RadioButton>
```

This is clean, correct, uses standard WPF idiom, and doesn't touch ConverterParameter.

**Note**: `x:Static` requires the converter to be a static field — `public static readonly SourceIdEqualsMasterConverter Instance = new();` — or a singleton via a property. The new file should expose `Instance` as a static field.

Let me update the plan with this final design:

### Revised Step 1 content:

- [ ] **Step 1: Create the new `SourceIdEqualsMasterConverter`**

Create `src/PeakCan.Host.App/Composition/Converters/SourceIdEqualsMasterConverter.cs` with EXACTLY this content:

```csharp
using System.Globalization;
using System.Windows.Data;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// v3.11.6 PATCH: <see cref="IMultiValueConverter"/> that returns
/// <c>true</c> when <c>values[0]</c> (the row's SourceId) equals
/// <c>values[1]</c> (the window's MasterSourceId). Replaces the
/// v3.3.0 binding-with-nested-ConverterParameter pattern that
/// caused <see cref="System.Windows.Markup.XamlParseException"/>
/// with the inner error
/// <c>MarkupExtensionDynamicOrBindingOnClrProp</c> after the first
/// .asc load populated the per-source legend strip.
/// <para>
/// Stateless — exposed as a singleton via <see cref="Instance"/>.
/// The <c>x:Static</c> usage in <c>TraceViewerView.xaml</c> avoids
/// needing an App.xaml resource registration.
/// </para>
/// </summary>
public sealed class SourceIdEqualsMasterConverter : IMultiValueConverter
{
    /// <summary>Singleton instance for x:Static binding.</summary>
    public static readonly SourceIdEqualsMasterConverter Instance = new();

    private SourceIdEqualsMasterConverter() { }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length != 2) return false;
        return string.Equals(values[0]?.ToString(), values[1]?.ToString(), StringComparison.Ordinal);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException(
            "SourceIdEqualsMasterConverter is one-way (IsChecked is set by the DataTrigger, not read back — SetMasterCommand drives master change).");
}
```

- [ ] **Step 2: Replace the RadioButton XAML**

In `src/PeakCan.Host.App/Views/TraceViewerView.xaml`, add the converter namespace to the root `<Window>` declaration (line 1-9):

```xml
<Window x:Class="PeakCan.Host.App.Views.TraceViewerView"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:oxy="http://oxyplot.org/wpf"
        xmlns:vm="clr-namespace:PeakCan.Host.App.ViewModels"
        xmlns:trace="clr-namespace:PeakCan.Host.App.Services.Trace"
        xmlns:conv="clr-namespace:PeakCan.Host.App.Composition.Converters"
        d:DataContext="{d:DesignInstance Type=vm:TraceViewerViewModel}"
        xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
        xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
        mc:Ignorable="d"
        Title="Trace Viewer"
        Width="1200" Height="800">
```

(Add `xmlns:conv="clr-namespace:PeakCan.Host.App.Composition.Converters"`.)

Then replace the master-radio `RadioButton` (lines 109-116) with:

```xml
                                <!-- v3.11.6 PATCH: replaced the
                                     ConverterParameter-nested-Binding pattern
                                     (caused MarkupExtensionDynamicOrBindingOnClrProp
                                     XamlParseException after first .asc load).
                                     Now uses a MultiBinding + IMultiValueConverter
                                     (SourceIdEqualsMasterConverter) which is the
                                     standard WPF idiom for "is this row's value
                                     equal to the parent's current selection?". -->
                                <RadioButton GroupName="MasterGroup" Margin="0,0,6,0" VerticalAlignment="Center"
                                             Command="{Binding DataContext.SetMasterCommand, RelativeSource={RelativeSource AncestorType=Window}}"
                                             CommandParameter="{Binding SourceId}"
                                             ToolTip="Make this the master (clock source)">
                                    <RadioButton.IsChecked>
                                        <MultiBinding Converter="{x:Static conv:SourceIdEqualsMasterConverter.Instance}">
                                            <Binding Path="SourceId"/>
                                            <Binding Path="DataContext.MasterSourceId"
                                                     RelativeSource="{RelativeSource AncestorType=Window}"/>
                                        </MultiBinding>
                                    </RadioButton.IsChecked>
                                </RadioButton>
```

- [ ] **Step 3: Remove the old `MasterRadio` resource from `App.xaml`**

In `src/PeakCan.Host.App/App.xaml`, remove line 20:

```xml
        <!-- v3.3.0 MINOR: per-source master radio in Trace Viewer legend strip. -->
        <conv:MasterRadioConverter x:Key="MasterRadio" />
```

After removal, lines 14-21 should be:

```xml
        <conv:NullToVisibilityConverter x:Key="NullToVisibilityConverter" />
        <!-- v3.2.0 MINOR: Trace Viewer legend strip + chart subplots. -->
        <conv:BooleanToVisibilityConverter x:Key="BoolToVis" />
        <conv:OxyColorToBrushConverter x:Key="OxyColorToBrush" />
        <!-- v3.11.6 PATCH: MasterRadioConverter resource removed (the
             converter is deleted; the master-radio XAML now uses
             MultiBinding + SourceIdEqualsMasterConverter instead). -->
    </Application.Resources>
```

- [ ] **Step 4: Delete `MasterRadioConverter.cs`**

```bash
cd D:/claude_proj2/peakcan-host
git rm src/PeakCan.Host.App/Composition/Converters/MasterRadioConverter.cs
```

- [ ] **Step 5: Verify build clean**

Run: `dotnet build PeakCan.Host.slnx --nologo`
Expected: 0 errors. The build MUST compile cleanly — no other file references `MasterRadioConverter`.

- [ ] **Step 6: Run the regression-guard test — expected to PASS now**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~NoProductionFile_References_MasterRadioConverter" --nologo --no-build`
Expected: 1 passed. TraceViewerView.xaml no longer references `MasterRadio`.

- [ ] **Step 7: Run the full test suite — ensure no regression**

Run: `dotnet test PeakCan.Host.slnx --nologo --no-build`
Expected: **1282 + 5 SKIP / 0 fail** (no change in counts — pure XAML refactor).

If any test fails (e.g., AppHostBuilderTests references `MasterRadioConverter`), surface in the report.

- [ ] **Step 8: Stage + commit**

```bash
cd D:/claude_proj2/peakcan-host
git add src/PeakCan.Host.App/Views/TraceViewerView.xaml \
        src/PeakCan.Host.App/App.xaml \
        src/PeakCan.Host.App/Composition/Converters/SourceIdEqualsMasterConverter.cs \
        tests/PeakCan.Host.App.Tests/Views/TraceViewerViewXamlTests.cs
git rm src/PeakCan.Host.App/Composition/Converters/MasterRadioConverter.cs
git status --short   # verify the staging matches
git commit -m "fix(traceviewer): replace ConverterParameter-nested-Binding with MultiBinding (v3.11.6 PATCH)"
```

---

### Task 3: Verify + Tier 3 ship

**Files:**
- Create: `docs/release-notes-v3.11.6.md`
- Create: `scripts/tier3_v3116.py`

- [ ] **Step 1: Manual smoke (2 verification cases)**

Run the WPF app and verify:
1. Open Trace Viewer (no sources loaded yet) → no XamlParseException, window displays normally.
2. Click "Add trace…" → select the user's `C:\Users\13777\Desktop\Logging.asc` → after asc loads, the per-source legend strip appears with one radio per source. **No XamlParseException**. Clicking a radio fires `SetMasterCommand` and updates `MasterSourceId`.

If any smoke case fails, surface in the report.

- [ ] **Step 2: Write release notes**

Create `docs/release-notes-v3.11.6.md`:

```markdown
# Release Notes v3.11.6 — Trace Viewer master-radio XAML parse exception (PATCH)

**Released:** 2026-07-07
**Parent:** v3.11.5 PATCH (`11f5b84`)
**Tag:** v3.11.6
**Branch:** `feature/v3-11-1-patch`

## Highlights

This PATCH fixes the `XamlParseException` (`MarkupExtensionDynamicOrBindingOnClrProp`) thrown after the first `.asc` load in the Trace Viewer. The exception was caused by a `{Binding ...}` markup extension nested inside another binding's `ConverterParameter` — WPF explicitly forbids markup extensions in `ConverterParameter` because it's a non-`DependencyProperty` `object`.

| Commit | Fix | Tests |
|--------|-----|-------|
| `<this-commit>` | Replace ConverterParameter-nested-Binding with standard WPF MultiBinding + IMultiValueConverter | +1 (regression guard) |

**Test delta:** 1282 + 5 SKIP / 0 fail → **1282 + 5 SKIP / 0 fail** (+1 active regression guard; net change in counts: 0)
**Code stats:** +35 / -25 (net +10 LoC: new `SourceIdEqualsMasterConverter` + MultiBinding XAML; deleted `MasterRadioConverter`)

## Root cause

`src/PeakCan.Host.App/Views/TraceViewerView.xaml:109-116` (pre-PATCH):
```xml
<RadioButton IsChecked="{Binding SourceId,
                                 Converter={StaticResource MasterRadio},
                                 ConverterParameter={Binding DataContext.MasterSourceId,
                                                     RelativeSource={RelativeSource AncestorType=Window}}}"
             ... />
```

WPF's `Binding.ConverterParameter` is `object` (not a `DependencyProperty`), and the parser throws `MarkupExtensionDynamicOrBindingOnClrProp` the moment the `DataTemplate` instantiates and resolves the nested `{Binding ...}` inside `ConverterParameter`. This is why the error only manifests **after the first `.asc` load** — the per-source legend strip is `Visibility="{Binding HasSources, ...}"` and only instantiates when `Sources` collection gets populated.

The bug was latent since v3.3.0 MINOR (when the master-radio was added) but never surfaced because no test ever exercised the per-source legend strip's template instantiation. The v3.11.5 PATCH added real-world CANoe-format asc loads to the test fixture — those tests pass, but they don't construct the WPF visual tree, so the XAML antipattern stayed hidden.

## Fix

Replace the illegal nested-Binding with the standard WPF idiom: `MultiBinding` + `IMultiValueConverter`. The new `SourceIdEqualsMasterConverter` is a stateless singleton exposed via `x:Static`, so no App.xaml resource registration is needed.

**Before** (lines 109-116):
```xml
<RadioButton IsChecked="{Binding SourceId,
                                 Converter={StaticResource MasterRadio},
                                 ConverterParameter={Binding DataContext.MasterSourceId,
                                                     RelativeSource={RelativeSource AncestorType=Window}}}"
             Command="{Binding DataContext.SetMasterCommand, RelativeSource={RelativeSource AncestorType=Window}}"
             CommandParameter="{Binding SourceId}" />
```

**After**:
```xml
<RadioButton Command="{Binding DataContext.SetMasterCommand, RelativeSource={RelativeSource AncestorType=Window}}"
             CommandParameter="{Binding SourceId}">
    <RadioButton.IsChecked>
        <MultiBinding Converter="{x:Static conv:SourceIdEqualsMasterConverter.Instance}">
            <Binding Path="SourceId"/>
            <Binding Path="DataContext.MasterSourceId"
                     RelativeSource="{RelativeSource AncestorType=Window}"/>
        </MultiBinding>
    </RadioButton.IsChecked>
</RadioButton>
```

## Tests

| Test | Asserts |
|------|---------|
| `TraceViewerViewXamlTests.NoProductionFile_References_MasterRadioConverter_Or_ResourceKey` (NEW, +1) | Walks 5 tracked XAML/C# files, asserts `MasterRadio` and `MasterRadioConverter` strings are nowhere in production code. Regression guard against re-introducing the antipattern. |

## Upgrade notes

No breaking changes:
- `SetMasterCommand` source-generated name preserved (XAML binding unchanged).
- `MasterSourceId` ObservableProperty unchanged.
- `TraceViewerViewModel` VM API unchanged.
- `SourceIdEqualsMasterConverter` is a new internal class — visible only within the assembly.

## NEXT

- v3.12.0 MINOR — C2 ReplayViewModel god class split + H3/H6/M1-M13 review backlog closure
```

- [ ] **Step 3: Create the Tier 3 ship script**

Create `scripts/tier3_v3116.py` by copying `scripts/tier3_v3115.py` and updating:
- Line 17: `PARENT_SHA = "11f5b84fa9878acb9fc755f141ebe5066fcc5a2b"`  (v3.11.5 on origin/main)
- Lines 21-28: replace `ADDED_OR_MODIFIED` with:

```python
ADDED_OR_MODIFIED = [
    # M7: Master-radio XAML antipattern fix
    "src/PeakCan.Host.App/Views/TraceViewerView.xaml",
    "src/PeakCan.Host.App/App.xaml",
    "src/PeakCan.Host.App/Composition/Converters/SourceIdEqualsMasterConverter.cs",
    "tests/PeakCan.Host.App.Tests/Views/TraceViewerViewXamlTests.cs",
    # Release notes
    "docs/release-notes-v3.11.6.md",
]
```

- Lines 73, 84, 87, 90, 94, 99: replace all `v3.11.5` with `v3.11.6`.

- [ ] **Step 4: Run the Tier 3 ship**

Run: `python scripts/tier3_v3116.py`
Expected output:
```
  parent       11f5b84...
  parent tree  <40-hex-sha>
  blob   <40-hex-sha>  src/PeakCan.Host.App/Views/TraceViewerView.xaml  (... bytes)
  blob   <40-hex-sha>  src/PeakCan.Host.App/App.xaml  (... bytes)
  blob   <40-hex-sha>  src/PeakCan.Host.App/Composition/Converters/SourceIdEqualsMasterConverter.cs  (... bytes)
  blob   <40-hex-sha>  tests/PeakCan.Host.App.Tests/Views/TraceViewerViewXamlTests.cs  (... bytes)
  blob   <40-hex-sha>  docs/release-notes-v3.11.6.md  (... bytes)

  tree  <40-hex-sha>
  commit <40-hex-sha>
  refs/heads/main -> <40-hex-sha> (force)
  tag    <40-hex-sha>  v3.11.6
  refs/tags/v3.11.6 -> <40-hex-sha>
  release https://github.com/jasontaotao/peakcan-host/releases/tag/v3.11.6

=== TIER 3 SHIP COMPLETE ===
  parent  : 11f5b84...
  new     : <40-hex-sha>
  tag     : v3.11.6  (<40-hex-sha>)
  release : https://github.com/jasontaotao/peakcan-host/releases/tag/v3.11.6
```

- [ ] **Step 5: Commit the ship script + release notes to local branch**

```bash
cd D:/claude_proj2/peakcan-host
git add docs/release-notes-v3.11.6.md scripts/tier3_v3116.py
git commit -m "docs(ship): v3.11.6 PATCH release notes + tier3 ship script"
```

- [ ] **Step 6: PKM capture**

Dispatch `vault-pkm:pkm-capture` in the background with:
- First capture this session: false (v3.11.5 capture retry is still in flight; merge into same topic or write a new one for v3.11.6)
- Previous capture timestamp: 2026-07-07 (v3.11.5)
- Vault path: `01-Projects/peakcan-host/development/v3-11-6-patch-trace-viewer-master-radio-2026-07-07.md`

---

## Self-Review (post-write, before handoff)

1. **Spec coverage**: User request "asc 加载之后报错" → all 2 smoke cases in Task 3 Step 1 verify the fix end-to-end.
2. **Placeholder scan**: No "TBD" / "implement later" / "similar to Task N" markers.
3. **Type consistency**:
   - `SourceIdEqualsMasterConverter.Instance` is a `public static readonly` field (used via `x:Static`) — matches WPF idiom for singleton converters.
   - The converter's `Convert` signature matches `IMultiValueConverter.Convert(object[] values, Type targetType, object parameter, CultureInfo culture)`.
   - The `MultiBinding` declares `<Binding Path="SourceId"/>` + `<Binding Path="DataContext.MasterSourceId" RelativeSource="..."/>` — `values[0]` = `SourceId`, `values[1]` = `MasterSourceId`. The converter compares them.
4. **Cross-task dependency**: Task 1's test fails until Task 2 lands the XAML fix. Linear: 1 → 2 → 3.

## Out of scope (deferred)

- **`MasterRadioConverter` saved for v3.11.6+** — if any consumer besides TraceViewerView needed it (none per Grep), this PATCH would have preserved it. Deleted cleanly per Grep.
- **Unit test for `SourceIdEqualsMasterConverter.Convert` behavior** — pure mechanical string-equality, no logic worth a dedicated test. The integration test (full suite passes) is sufficient coverage.
- **Multi-binding with a fallback if `MasterSourceId` is null** — the converter already handles `null` inputs (returns `false` via `?.ToString()` + null check).
- **Visual designer support for `x:Static`** — `d:DataContext` + designer instance should still work; the `d:` namespace's binding doesn't apply to `x:Static` references.

## Verification

```bash
# Targeted:
dotnet test --filter "FullyQualifiedName~TraceViewerViewXamlTests" --nologo
# Expect: 1 passed (regression guard green)

# Full suite:
dotnet test PeakCan.Host.slnx --nologo
# Expect: 1282 + 5 SKIP / 0 fail (+1 active: 1283 total = 1282 + new guard; SKIP unchanged at 5)

# Manual smoke:
# 1. Open Trace Viewer (empty) → window displays, no exception
# 2. Add trace → C:\Users\13777\Desktop\Logging.asc loads → legend strip appears with N radio buttons; NO XamlParseException
# 3. Click a different radio → SetMasterCommand fires → MasterSourceId updates → radio reflects new master
```

## Ship summary

- **Tag**: v3.11.6 (PATCH)
- **Parent**: v3.11.5 PATCH on origin/main (`11f5b84fa9878acb9fc755f141ebe5066fcc5a2b`)
- **Files**: 2 created (`SourceIdEqualsMasterConverter.cs` + `TraceViewerViewXamlTests.cs`), 1 deleted (`MasterRadioConverter.cs`), 2 modified (`TraceViewerView.xaml` + `App.xaml`), +1 ship script, +1 release notes
- **Tests**: +1 active regression guard. Total delta: 1282 → 1283 + 5 SKIP / 0 fail.
- **Commits**: 2 commits (1 source + 1 ship docs) on `feature/v3-11-1-patch`, then 1 Tier 3 ship commit on `origin/main`.