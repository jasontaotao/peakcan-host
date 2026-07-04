using FluentAssertions;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Unit-level tests for <see cref="RateLimitStatus.Refresh"/>. The per-VM
/// behavior is covered by the existing <c>RateLimitRejectedCount_*</c> tests
/// in <c>SendViewModelTests</c>, <c>DbcSendViewModelTests</c>, and
/// <c>MultiFrameSendViewModelTests</c>; these tests pin the shared
/// try/catch + log + return-currentValue logic.
/// <para>
/// See <c>docs/release-notes-v3.1.0.md</c> for the rationale (3-way DRY
/// refactor + W1 silent-log fix in DbcSend + MultiFrame).
/// </para>
/// </summary>
public class RateLimitStatusTests
{
    [Fact]
    public void Refresh_ReturnsCurrentValue_WhenProviderIsNull()
    {
        // No provider → no-op; the current value is the contract.
        var current = 42L;

        var result = RateLimitStatus.Refresh(provider: null, currentValue: current);

        result.Should().Be(42L);
    }

    [Fact]
    public void Refresh_ReturnsNewValue_WhenProviderReturnsValue()
    {
        // Provider returns a fresh count → helper returns it verbatim.
        Func<long> provider = () => 99L;

        var result = RateLimitStatus.Refresh(provider, currentValue: 0L);

        result.Should().Be(99L);
    }

    [Fact]
    public void Refresh_ReturnsCurrentValue_WhenProviderThrows()
    {
        // Provider throws → catch + return currentValue (keep last known good).
        Func<long> throwingProvider = () => throw new InvalidOperationException("boom");

        var result = RateLimitStatus.Refresh(throwingProvider, currentValue: 7L);

        result.Should().Be(7L, "throwing providers must not reset the chip to zero");
    }

    [Fact]
    public void Refresh_LogsWarning_WhenProviderThrows()
    {
        // Provider throws → catch emits exactly one Warning entry.
        var logger = new RecordingLogger();
        Func<long> throwingProvider = () => throw new InvalidOperationException("boom");

        RateLimitStatus.Refresh(throwingProvider, currentValue: 7L, logger: logger);

        logger.Entries.Should().HaveCount(1);
        logger.Entries[0].Level.Should().Be(LogLevel.Warning);
        logger.Entries[0].Message.Should().Contain("Rate-limit rejected-count provider threw during Poll");
        logger.Entries[0].Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public void Refresh_AllowsProviderReturningSameValue_Repeatedly()
    {
        // Idempotency: provider returns the same value N times; helper does not
        // decrement / increment / mutate — it just returns what the provider gives.
        Func<long> provider = () => 5L;

        for (var i = 0; i < 10; i++)
        {
            RateLimitStatus.Refresh(provider, currentValue: 5L).Should().Be(5L);
        }
    }

    [Fact]
    public void Refresh_DoesNotThrow_WhenLoggerOmitted()
    {
        // v3.1.1 PATCH: pins the `logger == null` default path
        // (NullLogger.Instance fallback). The helper must never propagate
        // the provider exception even when no logger is supplied — the
        // caller gets back currentValue and the warning goes nowhere.
        Func<long> throwingProvider = () => throw new InvalidOperationException("boom");

        var act = () => RateLimitStatus.Refresh(throwingProvider, currentValue: 0L);

        act.Should().NotThrow();
    }

    /// <summary>
    /// Minimal <see cref="ILogger"/> stub that captures emitted entries.
    /// Avoids adding a new package dependency for one test.
    /// </summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}