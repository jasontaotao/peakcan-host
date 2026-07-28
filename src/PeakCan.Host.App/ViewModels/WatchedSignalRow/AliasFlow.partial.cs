using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// v12 Step 0: Alias property for <see cref="WatchedSignalRow"/>.
/// When non-null, the UI and chat display the alias instead of
/// <see cref="WatchedSignalRow.SignalName"/>. <see cref="WatchedSignalRow.SignalKey"/>
/// is unaffected (still the internal identity).
/// </summary>
public sealed partial class WatchedSignalRow
{
    /// <summary>User-defined display alias. Null = use DBC SignalName.</summary>
    [ObservableProperty]
    private string? _alias;

    /// <summary>
    /// Display name: alias if set, otherwise the DBC signal name.
    /// Used by XAML bindings (watch list, chat panel) instead of direct SignalName binding.
    /// Invokes PropertyChanged when Alias changes.
    /// </summary>
    public string DisplayName => Alias ?? SignalName;

    partial void OnAliasChanged(string? value)
    {
        OnPropertyChanged(nameof(DisplayName));
    }
}
