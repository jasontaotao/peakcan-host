using PeakCan.Host.App.Composition.Converters;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class WatchedSignalRow
{
    // Flow: String-formatted text columns for XAML binding (LatestText +
    // BlueText + DeltaText get-only). Methods moved verbatim from
    // WatchedSignalRow.cs (W42 T3).
    //
    // Cross-flow state accessed via partial-class visibility:
    //   - _signal + _dbc + _decimalDigits (SignalContextFlow.partial.cs)
    //   - _latestValue + _blueLatestValue (LiveValueFlow.partial.cs)
    //
    // Pure read-side formatter: ZERO setter, ZERO [ObservableProperty],
    // ZERO plain private field of its own. v3.50.5 PATCH introduced these
    // string properties to replace the XAML-side DoubleNanToStr converter;
    // the .Text properties handle NaN formatting internally.
    //
    // G4: enum signals have no subtractable semantics between text labels;
    // DeltaText returns Placeholder when _signal?.ValueTableName is not null.

    // === v3.50.5 PATCH: string-formatted columns for XAML binding ===
    // Sister pattern of DeltaValue computed property: prefer DBC VAL_ table
    // text when the signal + Dbc document are bound; fall back to F2 numeric
    // formatting; NaN → "—" placeholder. These properties drive the watch
    // list's Latest / Δ / Blue columns; the XAML binding drops the
    // DoubleNanToStr converter since the .Text properties handle NaN
    // formatting internally.

    /// <summary>Decoded Latest value as a string for XAML binding.
    /// Prefers DBC VAL_ table text when available; falls back to F2 numeric.</summary>
    public string LatestText
    {
        get
        {
            if (IsPlaceholder || double.IsNaN(_latestValue)) return DoubleNanToStringConverter.Placeholder;
            if (_signal is not null && _dbc is not null)
            {
                var text = SignalDecoder.TryDecodeEnumText(_signal, _latestValue, _dbc);
                if (text is not null) return text;
            }
            // v3.50.6 PATCH: factor-derived precision replaces F2.
            return SignalFormatter.FormatValue(_decimalDigits, _latestValue);
        }
    }

    /// <summary>Decoded Blue (comparison anchor) value as a string for XAML binding.
    /// Same fallback semantics as <see cref="LatestText"/>.</summary>
    public string BlueText
    {
        get
        {
            if (double.IsNaN(_blueLatestValue)) return DoubleNanToStringConverter.Placeholder;
            if (_signal is not null && _dbc is not null)
            {
                var text = SignalDecoder.TryDecodeEnumText(_signal, _blueLatestValue, _dbc);
                if (text is not null) return text;
            }
            return SignalFormatter.FormatValue(_decimalDigits, _blueLatestValue);
        }
    }

    /// <summary>Δ as a string for XAML binding. Enum signals show "—" (no
    /// subtractable semantics between text labels); numeric signals show
    /// F2 diff. NaN → "—" placeholder.</summary>
    public string DeltaText
    {
        get
        {
            if (double.IsNaN(_latestValue) || double.IsNaN(_blueLatestValue))
                return DoubleNanToStringConverter.Placeholder;
            // G4: enum signals have no subtractable semantics between text labels.
            if (_signal?.ValueTableName is not null)
                return DoubleNanToStringConverter.Placeholder;
            return SignalFormatter.FormatValue(_decimalDigits, _blueLatestValue - _latestValue);
        }
    }
}
