using Xunit;

namespace PeakCan.Host.App.Tests.Collections;

/// <summary>
/// v1.2.11 PATCH Task 10: xunit collection that disables parallelization
/// for STA-bound WPF tests. xunit cannot instantiate multiple
/// <see cref="System.Windows.Application"/> per AppDomain, so any test
/// class that constructs a WPF <c>UserControl</c> via STA +
/// <c>Application</c> must join this collection.
/// <para>
/// Usage: <c>[Collection("WpfApp")]</c> on each such class.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfAppTestCollection
{
    public const string Name = "WpfApp";
}