namespace PeakCan.Host.Core.Replay;

/// <summary>Base class for Replay load/format errors.</summary>
public abstract class ReplayException : Exception
{
    protected ReplayException(string message) : base(message) { }
    protected ReplayException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>File not found, IO error, or other load-time failure.</summary>
public sealed class ReplayLoadException : ReplayException
{
    public ReplayLoadException(string message) : base(message) { }
    public ReplayLoadException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>File contents cannot be parsed (no frames, >50% malformed, missing required headers).</summary>
public sealed class ReplayFormatException : ReplayException
{
    public ReplayFormatException(string message) : base(message) { }
}

/// <summary>
/// v1.4.2 PATCH Item 3: thrown by <c>ReplayFrameSinkAdapter</c> when
/// <c>SendService.SendAsync</c> returns <c>Result&lt;Unit&gt;.Fail</c>
/// (e.g. no active channel, PEAK error). Surfaces the first-failure
/// reason up to <c>ReplayService</c>, which raises
/// <c>PlaybackEnded</c> with <c>Error</c> populated so the UI can
/// display the failure to the user.
/// </summary>
public sealed class ReplaySendException : ReplayException
{
    public ReplaySendException(string message) : base(message) { }
    public ReplaySendException(string message, Exception inner) : base(message, inner) { }
}
