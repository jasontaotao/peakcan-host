namespace PeakCan.Host.Core.Dbc;

/// <summary>Base for all DBC encoding errors.</summary>
public abstract class DbcSignalEncodeException : Exception
{
    public string MessageName { get; }
    protected DbcSignalEncodeException(string messageName, string message) : base(message)
    {
        MessageName = messageName;
    }
}

/// <summary>Signal physical value outside [Min, Max] range.</summary>
public sealed class DbcSignalValueOutOfRangeException : DbcSignalEncodeException
{
    public string SignalName { get; }
    public double Value { get; }
    public double Min { get; }
    public double Max { get; }
    public DbcSignalValueOutOfRangeException(string messageName, string signalName, double value, double min, double max)
        : base(messageName, $"Signal '{signalName}' value {value} outside [{min}, {max}] for message '{messageName}'")
    {
        SignalName = signalName;
        Value = value;
        Min = min;
        Max = max;
    }
}

/// <summary>Signal name not present in message definition.</summary>
public sealed class DbcSignalNotFoundException : DbcSignalEncodeException
{
    public string SignalName { get; }
    public DbcSignalNotFoundException(string messageName, string signalName)
        : base(messageName, $"Signal '{signalName}' not defined in message '{messageName}'")
    {
        SignalName = signalName;
    }
}

/// <summary>Multiplexed message requires multiplexor value, but it's missing from input.</summary>
public sealed class DbcMultiplexorRequiredException : DbcSignalEncodeException
{
    public DbcMultiplexorRequiredException(string messageName)
        : base(messageName, $"Multiplexed message '{messageName}' requires multiplexor value, but none provided") { }
}

/// <summary>Signal has invalid engineering configuration (e.g. Factor=0 → divide by zero).</summary>
public sealed class DbcSignalConfigurationException : DbcSignalEncodeException
{
    public string SignalName { get; }
    public DbcSignalConfigurationException(string messageName, string signalName, string detail)
        : base(messageName, $"Signal '{signalName}' in message '{messageName}': {detail}")
    {
        SignalName = signalName;
    }
}
