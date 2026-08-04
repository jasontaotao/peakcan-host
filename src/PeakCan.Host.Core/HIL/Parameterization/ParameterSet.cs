namespace PeakCan.HIL.Core.HIL;

/// <summary>
/// A set of parameter values for expanding a TestCaseTemplate.
/// </summary>
public sealed record ParameterSet(IReadOnlyDictionary<string, object> Values)
{
    public static ParameterSet Empty => new(new Dictionary<string, object>());

    public static ParameterSet Create(params (string Key, object Value)[] pairs)
        => new(pairs.ToDictionary(p => p.Key, p => p.Value));
}
