using System.Windows;

namespace PeakCan.Host.App.Windows;

/// <summary>
/// P0-3: window host for the HIL testing surface. Carries the existing
/// <see cref="Views.HilView"/>; DataContext is set by the caller (the
/// shared <c>HilViewModel</c> singleton) via the Show factory.
/// </summary>
public partial class HilWindow : Window
{
    public HilWindow()
    {
        InitializeComponent();
    }
}
