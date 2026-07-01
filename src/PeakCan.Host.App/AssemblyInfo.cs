using System.Runtime.CompilerServices;
using System.Windows;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// Expose composition-layer types (e.g. SinkWiringService) to the App
// test project so wiring behaviour can be verified without
// public-izing implementation details.
[assembly: InternalsVisibleTo("PeakCan.Host.App.Tests")]
