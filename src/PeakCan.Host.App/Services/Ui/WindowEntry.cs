using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PeakCan.Host.App.Services.Ui;

/// <summary>
/// P0-2/D3: one row in the Window menu. <see cref="IsActive"/> is the
/// check-state (driven by the window's Activated/Deactivated via
/// <see cref="WindowHostService.SetActive"/>); <see cref="ActivateCommand"/>
/// brings the owning window to the front (or opens it if closed).
/// </summary>
public partial class WindowEntry : ObservableObject
{
    private readonly WindowHostService _host;

    public WindowKey Key { get; }

    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isActive;

    public IRelayCommand ActivateCommand { get; }

    public WindowEntry(WindowHostService host, WindowKey key, string displayName)
    {
        _host = host;
        Key = key;
        DisplayName = displayName;
        ActivateCommand = new RelayCommand(() => _host.Activate(Key));
    }
}
