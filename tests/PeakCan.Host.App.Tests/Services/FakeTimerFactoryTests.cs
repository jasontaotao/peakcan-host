using FluentAssertions;
using PeakCan.Host.Core.Services;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// v3.5.2 PATCH: contract tests for <see cref="FakeTimerFactory"/>.
/// These pin the test-double seam used by <see cref="RecordServiceTests"/>
/// and <see cref="StatisticsServiceTests"/>: created timers do NOT tick
/// on their own — test code calls <see cref="FakePeriodicTimer.Fire"/>
/// to drive each tick deterministically. This eliminates the
/// wall-clock flake surface that v3.4.5 widened and v3.5.1 could not
/// fully close.
/// </summary>
public class FakeTimerFactoryTests
{
    [Fact]
    public async Task CreateTimer_Returns_Timer_That_Fires_On_Demand()
    {
        var factory = new FakeTimerFactory();
        var timer = factory.CreateTimer(TimeSpan.FromSeconds(1));
        timer.Should().NotBeNull();
        timer.Should().BeAssignableTo<FakePeriodicTimer>();

        var fake = (FakePeriodicTimer)timer;
        var waitTask = fake.WaitForNextTickAsync();

        // Before Fire(), WaitForNextTickAsync must NOT have completed.
        waitTask.IsCompleted.Should().BeFalse("the fake timer must not auto-tick");

        fake.Fire();

        var result = await waitTask;
        result.Should().BeTrue("Fire() resolves the tick with true (continuation available)");
    }

    [Fact]
    public void CreateTimer_Records_Period_And_Exposes_It()
    {
        var factory = new FakeTimerFactory();
        var timer = (FakePeriodicTimer)factory.CreateTimer(TimeSpan.FromMilliseconds(500));
        timer.Period.Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void CreatedTimers_Are_Tracked_In_CreatedTimers_List()
    {
        var factory = new FakeTimerFactory();
        var t1 = factory.CreateTimer(TimeSpan.FromSeconds(1));
        var t2 = factory.CreateTimer(TimeSpan.FromSeconds(2));
        factory.CreatedTimers.Should().HaveCount(2);
        factory.CreatedTimers.Should().Contain(new[] { (FakePeriodicTimer)t1, (FakePeriodicTimer)t2 });
    }

    [Fact]
    public async Task Multiple_CreatedTimers_Are_Independent()
    {
        // Firing one timer must not resolve another timer's pending wait.
        var factory = new FakeTimerFactory();
        var t1 = (FakePeriodicTimer)factory.CreateTimer(TimeSpan.FromSeconds(1));
        var t2 = (FakePeriodicTimer)factory.CreateTimer(TimeSpan.FromSeconds(1));

        var wait1 = t1.WaitForNextTickAsync();
        var wait2 = t2.WaitForNextTickAsync();

        t1.Fire();

        // wait1 should complete; wait2 should NOT yet (t2 has not fired).
        // Use WaitAsync-with-timeout to avoid IsCompletedSuccessfully probes that
        // race against the Fire() swap.
        var result1 = await wait1.WaitAsync(TimeSpan.FromSeconds(2));
        result1.Should().BeTrue();
        wait2.IsCompleted.Should().BeFalse(
            "firing t1 must not affect t2's pending wait");

        t2.Fire();
        var result2 = await wait2.WaitAsync(TimeSpan.FromSeconds(2));
        result2.Should().BeTrue();
    }
}
