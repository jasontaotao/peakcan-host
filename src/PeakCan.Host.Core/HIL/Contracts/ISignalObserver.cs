namespace PeakCan.HIL.Core.HIL.Contracts;

/// <summary>
/// Real-time signal observation (push model). Used by assertion layer.
/// </summary>
public interface ISignalObserver
{
    IDisposable ObserveSignal(string name, Action<double> onValueChanged);
    double? GetCurrentValue(string name);
}
