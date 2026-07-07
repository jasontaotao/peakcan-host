namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Base class for all Replay-domain exceptions. Concrete subclasses:
/// <list type="bullet">
///   <item><see cref="ReplayFormatException"/> — asc/blf file parser
///   found malformed content (header, tokens, frames).</item>
///   <item><see cref="ReplayLoadException"/> — pre-parse load failure
///   (file not found, file too large, permission denied).</item>
///   <item><see cref="ReplaySendException"/> — runtime playback sink
///   failure (CAN bus write failed mid-stream).</item>
/// </list>
/// New concrete subclasses MUST describe a single failure class
/// (parse | load | runtime) — never mix. Callers (Replay VM + Trace
/// Viewer VM) catch <see cref="ReplayException"/> to surface ALL
/// replay-related failures via ErrorMessage; catch a concrete subclass
/// only when the recovery path is subclass-specific.
/// </summary>
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
