using Microsoft.Maui.Controls;

namespace PeakCan.Host.Mobile.Platform;

public interface ITracePageFactory
{
    ContentPage Create(string cachedFilePath);
}
