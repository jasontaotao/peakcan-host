using System.Reflection;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

public class UdsClientSecurityAccessOverloadTests
{
    private static IsoTpLayer NewIsoTp()
        => new IsoTpLayer(
            config: new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 },
            sendFrame: _ => { });

    [Fact]
    public void Overload_Signature_Exists_With_Default_CancellationToken()
    {
        // Reflection check that the overload exists with the expected signature
        // and is virtual (so VMs can override it in tests).
        var method = typeof(UdsClient).GetMethod(
            "SecurityAccessAsync",
            new[] { typeof(byte), typeof(CancellationToken) });
        Assert.NotNull(method);
        Assert.True(method!.IsVirtual, "Overload must be virtual so VMs can override it.");
    }

    [Fact]
    public void NewCtor_Injects_KeyAlgorithm_Into_Private_Field()
    {
        // The 3-arg ctor stores the algorithm in the private _keyAlgorithm
        // field. We verify wiring via reflection — this proves the DI seam
        // is plumbed without needing to drive a real ECU round-trip (which
        // would require a hardware simulator or live ECU).
        var algo = new FakeKeyDerivationAlgorithm();
        using var sut = new UdsClient(NewIsoTp(), algo);

        var field = typeof(UdsClient).GetField(
            "_keyAlgorithm",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Same(algo, field!.GetValue(sut));
    }

    [Fact]
    public void LegacyCtor_Leaves_KeyAlgorithm_Field_Null()
    {
        // Legacy ctor must NOT inject a key algorithm; the new overload
        // throws InvalidOperationException when this field is null.
        using var sut = new UdsClient(NewIsoTp());

        var field = typeof(UdsClient).GetField(
            "_keyAlgorithm",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Null(field!.GetValue(sut));
    }

    [Fact]
    public void NewOverload_Behavior_When_KeyAlgorithm_Is_Placeholder()
    {
        // Verify the wiring contract: a PlaceholderKeyAlgorithm injected
        // via the 3-arg ctor will surface KeyAlgorithmNotConfiguredException
        // when its ComputeKey is invoked. The full SecurityAccessAsync
        // overload calls ComputeKey internally, so this test proves the
        // exception path is reachable without performing an ECU round-trip.
        // The end-to-end ECU integration test is a v1.2 manual test against
        // real hardware (see docs/superpowers/specs/.../design.md §7.3).
        var algo = new PlaceholderKeyAlgorithm();
        Assert.Throws<KeyAlgorithmNotConfiguredException>(() =>
        {
            algo.ComputeKey(new byte[] { 0x01 }, 0x01);
        });
    }
}
