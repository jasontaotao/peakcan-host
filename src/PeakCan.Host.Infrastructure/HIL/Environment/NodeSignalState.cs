namespace PeakCan.Host.Infrastructure.HIL.Environment;

/// <summary>
/// Per-message runtime signal state table.
/// Encoding order locked: signal state → DbcSignalsSource encode → counter/checksum → send.
/// </summary>
internal sealed class NodeSignalState
{
    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);

    public double GetOrInit(string signalName, double defaultValue = 0)
        => _values.TryGetValue(signalName, out var v) ? v : defaultValue;

    public void Set(string signalName, double value) => _values[signalName] = value;

    public IReadOnlyDictionary<string, double> ToDictionary() => _values;

    public bool HasValues => _values.Count > 0;
}