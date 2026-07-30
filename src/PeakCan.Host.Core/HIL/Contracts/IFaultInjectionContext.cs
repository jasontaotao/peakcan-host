namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>
/// Optional interface for IAssertionContext implementations that support fault injection.
/// Implemented by Infrastructure layer; Core layer checks via cast.
/// </summary>
public interface IFaultInjectionContext
{
    /// <summary>Add a fault rule. Returns a disposable handle for removal.</summary>
    IDisposable AddFault(FaultRule fault);

    /// <summary>Tag a fault handle with an ID for targeted clearing.</summary>
    void TagFault(string faultId, IDisposable handle);

    /// <summary>Remove all faults, or only those matching the given ID.</summary>
    void ClearFaults(string? faultId = null);
}
