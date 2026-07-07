# Release Notes v3.11.7 — MultiBinding Mode=OneWay (PATCH)

**Released:** 2026-07-07
**Parent:** v3.11.6 PATCH (`3cffef5`)
**Tag:** v3.11.7
**Branch:** `feature/v3-11-1-patch`

## Highlights

This PATCH fixes the `XamlParseException` that **persisted after v3.11.6** due to the v3.11.6 MultiBinding fix introducing a new regression: `RadioButton.IsChecked` is a TwoWay DependencyProperty by default, so WPF invoked the converter's `ConvertBack` during the initial `Activate()` path. The `ConvertBack` threw `NotSupportedException`, which bubbled up through `PropertyPathWorker.CheckReadOnly` and surfaced as `XamlParseException` AFTER the first asc load populated the per-source legend strip.

| Commit | Fix | Tests |
|--------|-----|-------|
| `2590b54` | Add `Mode="OneWay"` on the v3.11.6 MultiBinding so WPF never calls `ConvertBack` | +1 (STA smoke test) |

**Test delta:** 1283 + 5 SKIP / 0 fail → **1284 + 5 SKIP / 0 fail** (+1 active STA smoke test)
**Code stats:** +154 / -9 (net +145 LoC: 1-line MultiBinding `Mode="OneWay"` fix + new STA smoke test + extended xmldoc)

## Root cause

The v3.11.6 PATCH replaced the `ConverterParameter-nested-Binding` antipattern with a standard WPF `MultiBinding` + `IMultiValueConverter`. The new `SourceIdEqualsMasterConverter.ConvertBack` threw `NotSupportedException` because the binding is logically one-way (the master change is driven by `SetMasterCommand`, not by reading `IsChecked` back).

But `RadioButton.IsChecked` is a **TwoWay** `DependencyProperty` by default. WPF's binding activation pipeline (`BindingExpression.Activate` → `ClrBindingWorker.AttachDataItem` → `PropertyPathWorker.UpdateSourceValueState` → `PropertyPathWorker.ReplaceItem` → `PropertyPathWorker.CheckReadOnly`) calls `ConvertBack` to push the current `IsChecked` value back through the binding. The `NotSupportedException` surfaced at this call site, bubbled up as `XamlParseException`, and crashed the app.

**Why v3.11.6 missed this**: the regression-guard test `NoProductionFile_References_MasterRadioConverter_Or_ResourceKey` only asserts that production files don't contain banned substrings. It never instantiated the actual WPF `RadioButton` + `MultiBinding` + binding activation pipeline. The crash only surfaces when `ItemsControl` materializes the per-source `DataTemplate` for the first time (i.e., after the first asc load).

The user-provided stack trace (verbatim from `DispatcherUnhandledException`):
```
MS.Internal.Data.PropertyPathWorker.CheckReadOnly(object, object)
MS.Internal.Data.PropertyPathWorker.ReplaceItem(int, object, object)
MS.Internal.Data.PropertyPathWorker.UpdateSourceValueState(int, ICollectionView, object, bool)
MS.Internal.Data.ClrBindingWorker.AttachDataItem()
System.Windows.Data.BindingExpression.Activate(object)
System.Windows.Data.BindingExpression.AttachToContext(AttachAttempt)
System.Windows.Data.BindingExpression.AttachOverride(DependencyObject, DependencyProperty)
System.Windows.Data.BindingExpressionBase.Attach(DependencyObject, DependencyProperty)
System.Windows.Data.MultiBindingExpression.AttachBindingExpression(int, bool)
System.Windows.Data.MultiBindingExpression.AttachOverride(DependencyObject, DependencyProperty)
```

## Fix

Add `Mode="OneWay"` on the MultiBinding. WPF then never calls `ConvertBack`, and the read-only `NotSupportedException` is never invoked.

**Before** (v3.11.6 PATCH, TraceViewerView.xaml:121-127):
```xml
<RadioButton.IsChecked>
    <MultiBinding Converter="{x:Static conv:SourceIdEqualsMasterConverter.Instance}">
        <Binding Path="SourceId"/>
        <Binding Path="DataContext.MasterSourceId"
                 RelativeSource="{RelativeSource AncestorType=Window}"/>
    </MultiBinding>
</RadioButton.IsChecked>
```

**After** (v3.11.7 PATCH):
```xml
<RadioButton.IsChecked>
    <MultiBinding Converter="{x:Static conv:SourceIdEqualsMasterConverter.Instance}"
                  Mode="OneWay">
        <Binding Path="SourceId"/>
        <Binding Path="DataContext.MasterSourceId"
                 RelativeSource="{RelativeSource AncestorType=Window}"/>
    </MultiBinding>
</RadioButton.IsChecked>
```

The `SourceIdEqualsMasterConverter.ConvertBack` (which still throws `NotSupportedException`) is dead code now — but kept for completeness so future developers don't reintroduce the bug.

## Tests

| Test | Asserts |
|------|---------|
| `TraceViewerMasterRadioSmokeTests.MasterRadio_MultiBinding_OneWay_DoesNotThrowOnActivation` (NEW, +1) | Constructs the production-equivalent MultiBinding + RadioButton programmatically (STA-bound), exercises WPF's binding activation pipeline (touch `IsChecked` + Measure + Arrange + read again), asserts no exception fires. The test v3.11.6 SHOULD HAVE shipped. |

## Lessons (1-of-1, captured inline)

1. **`wpf-radiobutton-ischecked-defaults-twoway-must-set-mode-oneway-on-multibinding`** — Any custom `IValueConverter`/`IMultiValueConverter` on a TwoWay DP must either have a working `ConvertBack` OR the binding must declare `Mode="OneWay"`. WPF unconditionally calls `ConvertBack` during `Activate()` for TwoWay bindings, regardless of whether the binding is logically one-way.
2. **`unit-test-passing-is-not-wpf-runtime-validation`** — Regression-guard tests for XAML antipatterns must include a STA smoke test that programmatically constructs the same binding shape + reads/touches the bound DP, OR they catch nothing. v3.11.6's "substrings not present" test passed while the underlying MultiBinding was crashing at runtime.
3. **`ask-for-stack-trace-when-debugging-XAML-rather-than-guessing-antipatterns`** — When user reports a crash, the first response is "what's the stack trace?", not "let me guess another antipattern". v3.11.7's root cause was diagnosed from one stack trace line + 4-second reasoning; v3.11.4 + v3.11.5 + v3.11.6 collectively burned hours on guess-and-test antipattern hunting.

## Upgrade notes

No breaking changes:
- `SetMasterCommand` source-generated name preserved.
- `MasterSourceId` ObservableProperty unchanged.
- `SourceIdEqualsMasterConverter.Convert` unchanged.
- `SourceIdEqualsMasterConverter.ConvertBack` unchanged (still throws `NotSupportedException`; now unreachable due to `Mode="OneWay"` on the binding).

## NEXT

- v3.12.0 MINOR — C2 ReplayViewModel god class split + H3/H6/M1-M13 review backlog closure