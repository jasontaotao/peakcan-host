using NetArchTest.Rules;
using PeakCan.Host.App.Composition;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Peak;

namespace PeakCan.Host.Infrastructure.Tests.Architecture;

/// <summary>
/// Task 18: NetArchTest enforcement of the 3-layer separation
/// (Core → Infrastructure → App). Five rules, one per direction:
/// <list type="number">
///   <item>Core must not depend on WPF (System.Windows).</item>
///   <item>Core must not depend on the PEAK SDK (Peak.Can.Basic).</item>
///   <item>App must not depend on the PEAK SDK directly (must go
///     through <see cref="Infrastructure.Peak.PeakCanChannel"/>).</item>
///   <item>Infrastructure must not depend on WPF.</item>
///   <item>Infrastructure must not depend on App (composition root
///     lives in App; Infrastructure has no composition knowledge).</item>
/// </list>
/// <para>
/// <b>Why these names?</b> the assemblies are referenced by namespace
/// at runtime; <c>System.Windows</c> covers every WPF BAML type,
/// <c>Peak.Can.Basic</c> covers the PEAK SDK. The rules fail loudly
/// (test failure + a list of violating types) when a future change
/// crosses a layer boundary.
/// </para>
/// <para>
/// <b>Why a new directory under Infrastructure.Tests?</b> the rules
/// need to inspect types from all three production assemblies. Putting
/// the rules in any single test project would force a cross-project
/// reference that mirrors the rule under test. The compromise is to
/// put all arch rules in <see cref="LayeringRulesTests"/> inside the
/// Infrastructure.Tests project (which already references Core) and
/// add an App reference just for this test (commented in the csproj).
/// The App reference is a test-only concern and does not affect the
/// production Infrastructure assembly that rule 5 actually inspects.
/// </para>
/// </summary>
public class LayeringRulesTests
{
    [Fact]
    public void Core_Should_Not_Depend_On_WPF()
    {
        var result = Types.InAssembly(typeof(CanFrame).Assembly)
            .ShouldNot().HaveDependencyOn("System.Windows")
            .GetResult();
        Assert.True(result.IsSuccessful, Format(result));
    }

    [Fact]
    public void Core_Should_Not_Depend_On_Peak_Can_Basic()
    {
        var result = Types.InAssembly(typeof(CanFrame).Assembly)
            .ShouldNot().HaveDependencyOn("Peak.Can.Basic")
            .GetResult();
        Assert.True(result.IsSuccessful, Format(result));
    }

    [Fact]
    public void App_Should_Not_Depend_On_Peak_Can_Basic()
    {
        // App talks to PEAK hardware via PeakCanChannel (the adapter in
        // Infrastructure). The PEAK SDK namespace must never leak into
        // the App assembly — every cross-SDK call must go through the
        // adapter so the swap-out cost for a non-PEAK backend stays low.
        var result = Types.InAssembly(typeof(AppHostBuilder).Assembly)
            .ShouldNot().HaveDependencyOn("Peak.Can.Basic")
            .GetResult();
        Assert.True(result.IsSuccessful, Format(result));
    }

    [Fact]
    public void Infrastructure_Should_Not_Depend_On_WPF()
    {
        // The adapter layer is UI-agnostic. WPF types in Infrastructure
        // would force the WPF runtime onto the test host (already
        // required for the App.Tests project, but Infrastructure.Tests
        // targets plain net10.0 to keep the test surface small).
        var result = Types.InAssembly(typeof(PeakCanChannel).Assembly)
            .ShouldNot().HaveDependencyOn("System.Windows")
            .GetResult();
        Assert.True(result.IsSuccessful, Format(result));
    }

    [Fact]
    public void Infrastructure_Should_Not_Depend_On_App()
    {
        // Composition lives in App (AppHostBuilder). Infrastructure has
        // no business depending on the composition root — it would
        // create a circular boundary: App → Infrastructure → App.
        var result = Types.InAssembly(typeof(PeakCanChannel).Assembly)
            .ShouldNot().HaveDependencyOn("PeakCan.Host.App")
            .GetResult();
        Assert.True(result.IsSuccessful, Format(result));
    }

    private static string Format(TestResult r)
        => string.Join("\n", r.FailingTypeNames ?? new System.Collections.Generic.List<string>());
}
