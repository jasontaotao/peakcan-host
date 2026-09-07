using PeakCan.Host.Mobile.Views;

namespace PeakCan.Host.Mobile;

public partial class App
{
    private readonly FilesPage _filesPage;

    public App(FilesPage filesPage)
    {
        InitializeComponent();
        _filesPage = filesPage;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new NavigationPage(_filesPage));
    }

    protected override void OnSleep()
    {
        base.OnSleep();
        if (Windows.Count > 0 && Windows[0].Page is NavigationPage { CurrentPage: TracePage tracePage })
            tracePage.PauseForBackground();
    }
}
