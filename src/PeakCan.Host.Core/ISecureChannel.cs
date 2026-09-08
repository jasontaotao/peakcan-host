namespace PeakCan.Host.Core;

/// <summary>
/// Marker interface for the SecOcChannel decorator (spec §5-D1). The assembly
/// point checks this interface before wrapping to guarantee the decorator is
/// applied at most once — double wrapping would sign already-signed frames.
/// </summary>
public interface ISecureChannel
{
}
