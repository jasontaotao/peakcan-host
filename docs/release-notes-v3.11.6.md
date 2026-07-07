# Release Notes v3.11.6 — Trace Viewer master-radio XAML parse exception (PATCH)

**Released:** 2026-07-07
**Parent:** v3.11.5 PATCH (`11f5b84`)
**Tag:** v3.11.6
**Branch:** `feature/v3-11-1-patch`

## Highlights

This PATCH fixes the `XamlParseException` (`MarkupExtensionDynamicOrBindingOnClrProp`) thrown after the first `.asc` load in the Trace Viewer. The exception was caused by a `{Binding ...}` markup extension nested inside another binding's `ConverterParameter` — WPF explicitly forbids markup extensions in `ConverterParameter` because it's a non-`DependencyProperty` `object`.

| Commit | Fix | Tests |
|--------|-----|-------|
| `91c452f` | Replace ConverterParameter-nested-Binding with standard WPF MultiBinding + IMultiValueConverter | +1 (regression guard) |

**Test delta:** 1282 + 5 SKIP / 0 fail → **1283 + 5 SKIP / 0 fail** (+1 active regression guard)
**Code stats:** +125 / -31 (net +94 LoC: new `SourceIdEqualsMasterConverter` + MultiBinding XAML; deleted `MasterRadioConverter`)

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

The new `SourceIdEqualsMasterConverter` (file: `src/PeakCan.Host.App/Composition/Converters/SourceIdEqualsMasterConverter.cs`):
```csharp
public sealed class SourceIdEqualsMasterConverter : IMultiValueConverter
{
    public static readonly SourceIdEqualsMasterConverter Instance = new();

    private SourceIdEqualsMasterConverter() { }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length != 2) return false;
        return string.Equals(values[0]?.ToString(), values[1]?.ToString(), StringComparison.Ordinal);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException(
            "SourceIdEqualsMasterConverter is one-way (IsChecked is set, not read back).");
}
```

The old `MasterRadioConverter` + its `App.xaml` resource registration are deleted (no other file referenced them).

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