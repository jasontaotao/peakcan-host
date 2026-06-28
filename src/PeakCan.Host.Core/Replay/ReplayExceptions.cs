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