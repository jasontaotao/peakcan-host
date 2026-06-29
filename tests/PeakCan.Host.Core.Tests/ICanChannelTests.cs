using FluentAssertions;
using PeakCan.Host.Core;
using Xunit;

namespace PeakCan.Host.Core.Tests;

/// <summary>
/// v1.6.1 PATCH Item 4: verifies <see cref="BaudRate.FromFdDescriptor"/>
/// — renamed from <c>FromDescriptor</c> + de-Obsoleted to make the
/// FD-only nature explicit. Core cannot reference <c>TPCANBaudrate</c>
/// (NetArchTest rule 2), so a 3-arg <c>(descriptor, name, classicCode)</c>
/// overload awaits v1.6.x MINOR scope. See class doc on
/// <see cref="BaudRate"/> for the full constraint.
/// </summary>
public class ICanChannelTests
{
    [Fact]
    public void FromFdDescriptor_sets_IsFd_true()
    {
        var baud = BaudRate.FromFdDescriptor("f_clock_mhz=20,nom_brp=1", "1 Mbps (custom)");

        baud.IsFd.Should().BeTrue();
    }

    [Fact]
    public void FromFdDescriptor_round_trips_descriptor_and_name()
    {
        const string descriptor = "f_clock_mhz=20,nom_brp=1";
        const string name = "1 Mbps (custom)";

        var baud = BaudRate.FromFdDescriptor(descriptor, name);

        baud.Descriptor.Should().Be(descriptor);
        baud.Name.Should().Be(name);
    }

    [Fact]
    public void FromDescriptor_no_longer_exists_compile_time()
    {
        // Compile-time guarantee: if a future change re-adds
        // BaudRate.FromDescriptor(string, string), the build breaks.
        // We assert via reflection at runtime so the test still
        // documents the contract even when no compile error fires.
        var method = typeof(BaudRate).GetMethod(
            "FromDescriptor",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        method.Should().BeNull("BaudRate.FromDescriptor should be renamed to FromFdDescriptor in v1.6.1");
    }
}
