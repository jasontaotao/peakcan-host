using System;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using Xunit.Sdk;

namespace PeakCan.Host.App.Tests.TestHelpers;

/// <summary>
/// v1.6.1 PATCH Item 3: verifies <see cref="CyclicTimerTestHarness"/>
/// behavior in isolation. The harness is consumed by
/// <c>CyclicSendServiceRaceTests</c> + <c>CyclicDbcSendServiceRaceTests</c>;
/// these 4 tests pin the contract those consumers rely on.
/// </summary>
public class CyclicTimerTestHarnessTests
{
    [Fact]
    public async Task WaitUntilAsync_returns_true_when_predicate_already_true()
    {
        var result = await CyclicTimerTestHarness.WaitUntilAsync(
            () => true, TimeSpan.FromMilliseconds(100));

        result.Should().BeTrue();
    }

    [Fact]
    public async Task WaitUntilAsync_returns_false_on_timeout()
    {
        var result = await CyclicTimerTestHarness.WaitUntilAsync(
            () => false, TimeSpan.FromMilliseconds(50));

        result.Should().BeFalse();
    }

    [Fact]
    public async Task AssertWithinAsync_succeeds_within_window()
    {
        var counter = 0;

        await CyclicTimerTestHarness.AssertWithinAsync(
            () => ++counter >= 3, TimeSpan.FromMilliseconds(200), "counter reaches 3");

        counter.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task AssertWithinAsync_throws_XunitException_with_diagnostic_message_on_repeated_timeout()
    {
        var ex = await Assert.ThrowsAsync<XunitException>(() =>
            CyclicTimerTestHarness.AssertWithinAsync(
                () => false,
                TimeSpan.FromMilliseconds(20),
                "never-true predicate"));

        ex.Message.Should().Contain("never-true predicate");
        ex.Message.Should().Contain("3 attempts");
    }
}
