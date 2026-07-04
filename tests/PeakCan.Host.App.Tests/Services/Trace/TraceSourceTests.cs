using System.ComponentModel;
using FluentAssertions;
using OxyPlot;
using PeakCan.Host.App.Services.Trace;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// v3.4.3 PATCH: pins <see cref="TraceSource"/>'s manual
/// <see cref="INotifyPropertyChanged"/> implementation on the new
/// <see cref="TraceSource.CanIdFilter"/> mutable property. The other
/// five fields stay init-only (record back-compat at construction site).
/// NOTE: TraceSource lives in PeakCan.Host.App (Services.Trace), not in
/// Core, so these tests live in the App.Tests project (deviation from the
/// plan path "PeakCan.Host.Core.Tests/Replay/TraceSourceTests.cs" — see
/// implementer report).
/// </summary>
public class TraceSourceTests
{
    private static TraceSource MakeSource() =>
        new("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid);

    [Fact]
    public void CanIdFilter_Default_IsEmptyString()
    {
        // Arrange + Act: fresh instance from the v3.2.0 positional ctor shape.
        var src = MakeSource();

        // Assert: default value is the empty string (matches the plan's
        // "empty = inherit global" semantics; the parser treats null/whitespace
        // the same way, but the property itself is non-null by default).
        src.CanIdFilter.Should().Be("");
    }

    [Fact]
    public void CanIdFilter_Set_DifferentValue_FiresPropertyChangedOnce()
    {
        // Arrange: subscribe to PropertyChanged and capture the event.
        var src = MakeSource();
        var fires = new List<string?>();
        src.PropertyChanged += (_, e) => fires.Add(e.PropertyName);

        // Act: set from default "" to "100".
        src.CanIdFilter = "100";

        // Assert: exactly one fire, with the expected property name.
        fires.Should().HaveCount(1);
        fires[0].Should().Be(nameof(TraceSource.CanIdFilter));
    }

    [Fact]
    public void CanIdFilter_Set_SameValue_DoesNotFire()
    {
        // Arrange: default "" then explicit "100", then "" again.
        // The ""→"" no-op case AND the "100"→"100" idempotent case
        // must not fire — INPC deduplication is the whole reason this
        // binding survives typing in the legend TextBox without
        // rebuilding on every keystroke (well, every distinct keystroke).
        var src = MakeSource();
        src.CanIdFilter = "100";

        var fires = 0;
        src.PropertyChanged += (_, _) => fires++;

        // Act 1: set to same value "100" → no-op.
        src.CanIdFilter = "100";
        // Act 2: set "100" back to "" → different value → one fire.
        src.CanIdFilter = "";
        // Act 3: set "" to "" — exercise the early-return guard explicitly.
        src.CanIdFilter = "";

        // Assert: exactly one fire (the second set), zero from the two idempotent sets.
        fires.Should().Be(1);
    }
}