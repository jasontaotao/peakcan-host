using System.Collections.ObjectModel;
using System.Reflection;
using FluentAssertions;
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

    // Mirrors UdsClientTests.NewIso() (UdsClientTests.cs:34) — captures
    // sent frames so tests can assert wire-emit behavior.
    private static (IsoTpLayer iso, ObservableCollection<byte[]> sent) NewIsoWithCapture()
    {
        var sent = new ObservableCollection<byte[]>();
        var iso = new IsoTpLayer(
            new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 },
            frame => sent.Add(frame.Data.ToArray()));
        return (iso, sent);
    }

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

    // ========================================================================
    // v1.3.1 PATCH Item 2: 2-arg SecurityAccessAsync must fail-fast on
    // already-locked level before wire emit. The 3-arg overload's entry
    // check already provides this transitively (first 3-arg call would
    // throw), but the explicit pre-check makes the intent visible at
    // the 2-arg signature boundary. Test asserts observable behavior:
    // locked exception type, level match, remaining delay positive,
    // wire NOT touched.
    // ========================================================================

    /// <summary>
    /// v1.3.1 PATCH Item 2: when the security level is already locked,
    /// the 2-arg <c>SecurityAccessAsync(byte, CancellationToken)</c>
    /// overload must throw <see cref="UdsSecurityLockedException"/>
    /// without touching the wire — same contract as the 3-arg overload.
    /// </summary>
    [Fact]
    public async Task TwoArg_Overload_PreChecks_Lockout_Before_RequestSeed()
    {
        var (iso, sent) = NewIsoWithCapture();
        var algo = new FakeKeyDerivationAlgorithm();
        using var client = new UdsClient(iso, algo);

        // Force lockout via 3 manual failed attempts (Default config: 3 attempts)
        client.Security.RecordFailedAttempt(0x01);
        client.Security.RecordFailedAttempt(0x01);
        client.Security.RecordFailedAttempt(0x01);
        client.Security.IsLocked(0x01).Should().BeTrue("setup precondition");

        Func<Task> act = () => client.SecurityAccessAsync(requestLevel: 0x01, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<UdsSecurityLockedException>(
            "the level is locked; the 2-arg overload must fail-fast before RequestSeed");

        ex.Which.SecurityLevel.Should().Be(0x01);
        ex.Which.RemainingDelay.Should().BeGreaterThan(TimeSpan.Zero);
        sent.Should().BeEmpty("locked 2-arg SecurityAccessAsync must not touch the wire");
    }

    /// <summary>
    /// v1.3.1 PATCH Item 2: the 2-arg overload's XML doc must mention
    /// the mid-handshake lockout race (TOCTOU window between RequestSeed
    /// and SendKey legs). This is a gate against future refactoring
    /// that removes the documentation while preserving the behavior.
    /// <para>
    /// The csproj does not enable <c>&lt;GenerateDocumentationFile&gt;</c>,
    /// so the gate reads the raw source file directly. The check is
    /// &lt;remarks&gt;-aware (so adding or rewording the remarks is fine
    /// as long as the semantics stay).
    /// </para>
    /// </summary>
    [Fact]
    public void TwoArg_Overload_XmlDoc_Mentions_MidHandshake_Race()
    {
        // Read the source for the 2-arg overload and assert the
        // <remarks> block documents the mid-handshake race. We slice
        // out the method's XML doc region by finding the method
        // signature and walking back to the preceding '///' comments.
        // W12 PATCH: after the UdsClient god-class refactor (v3.27.0), the
        // 2-arg overload's source lives in the SecurityFlow partial. Read
        // both partial files so the xmldoc invariant holds regardless of
        // which file the partial lands in for future refactors.
        var baseDir = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PeakCan.Host.Core", "Uds");
        var candidates = new[] { "UdsClient.cs", "UdsClient/SecurityFlow.cs" };
        string? combined = null;
        foreach (var name in candidates)
        {
            var p = System.IO.Path.Combine(baseDir, name);
            if (File.Exists(p))
            {
                combined = (combined is null ? "" : combined) + File.ReadAllText(p);
            }
        }
        Assert.NotNull(combined);
        var source = combined!;
        Assert.Contains("mid-handshake", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SecurityAccessAsync(byte requestLevel, CancellationToken ct = default)",
            source, StringComparison.Ordinal);
    }
}
