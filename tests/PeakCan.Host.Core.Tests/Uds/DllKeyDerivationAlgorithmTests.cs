using System.Runtime.InteropServices;
using FluentAssertions;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.KeyDerivation;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

/// <summary>
/// Phase 1 C3: <see cref="DllKeyDerivationAlgorithm"/> wraps an OEM-supplied
/// native DLL that derives the SecurityAccess (0x27) key from a seed. The
/// native export signature is fixed by contract (see the xmldoc on the
/// <see cref="DllKeyDerivationAlgorithm.GenerateKeyDelegate"/> delegate) so
/// every OEM DLL follows the same ABI; the managed wrapper owns buffer
/// allocation, out-length handling, and error-code translation.
/// <para>
/// To keep these tests free of a real native DLL fixture, the wrapper
/// exposes an internal test seam ctor that takes the already-bound delegate.
/// The production ctor loads the DLL via <c>NativeLibrary</c> and resolves
/// the same delegate — it is exercised by a separate test that asserts the
/// ctor surfaces a missing DLL as a managed exception (no fixture DLL is
/// required for that assertion).
/// </para>
/// <para>
/// Contract obligations (from <see cref="IKeyDerivationAlgorithm"/> +
/// <see cref="PlaceholderKeyAlgorithm"/> precedent):
/// <list type="bullet">
///   <item><see cref="IKeyDerivationAlgorithm.ComputeKey"/> returns the OEM
///   key bytes for the given seed and security level.</item>
///   <item>null seed throws <see cref="ArgumentNullException"/>.</item>
///   <item>A native error code (non-zero return) must surface as a managed
///   exception, not as a silently-empty key — a silent empty key would make
///   SecurityAccess SendKey hit NRC 0x35 with no operator indication of the
///   algorithm failure.</item>
/// </list>
/// </para>
/// <para>
/// C1 decision (2026-07-21 flashing design review): <see cref="DllKeyDerivationAlgorithm"/>
/// implements <c>IDisposable</c> but <see cref="IKeyDerivationAlgorithm"/> does
/// NOT. The wrapper owns a native library handle that must be released on
/// teardown of the flashing session's secondary UdsClient. Callers that
/// hold a typed <see cref="DllKeyDerivationAlgorithm"/> reference Dispose it
/// themselves; callers holding the interface reference (e.g. the DI
/// Placeholder replacement path) must not attempt to Dispose via the
/// interface.
/// </para>
/// </summary>
public class DllKeyDerivationAlgorithmTests
{
    // A lazily-initialized seed snapshot captured by the stub delegate so
    // the assertions can inspect what the native export actually saw. The
    // delegate writes into these fields on each ComputeKey invocation.
    private byte[] _receivedSeed = Array.Empty<byte>();
    private byte _receivedLevel;
    private int _receivedSeedLen;

    private static DllKeyDerivationAlgorithm.GenerateKeyDelegate EchoStub()
    {
        // Default no-op stub returning success + a 2-byte key [0x11,0x22].
        // Tests that need bespoke behaviour pass their own delegate.
        return (byte[] seed, int seedLen, byte[] keyOut, ref int keyOutLen, byte level) =>
        {
            keyOut[0] = 0x11;
            keyOut[1] = 0x22;
            keyOutLen = 2;
            return 0;
        };
    }

    // ---- happy path ----

    [Fact]
    public void ComputeKey_NativeReturnsZero_CopiesOutBytesAndReturnsKey()
    {
        // Native export writes its derived key into keyOut and reports the
        // number of bytes written via keyOutLen. The wrapper must copy
        // exactly keyOutLen bytes out of the fixed-length buffer.
        DllKeyDerivationAlgorithm.GenerateKeyDelegate stub =
            (byte[] seed, int seedLen, byte[] keyOut, ref int keyOutLen, byte level) =>
            {
                _receivedSeed = seed[..seedLen];
                _receivedSeedLen = seedLen;
                _receivedLevel = level;
                keyOut[0] = 0x11;
                keyOut[1] = 0x22;
                keyOut[2] = 0x33;
                keyOutLen = 3;
                return 0; // 0 == success
            };

        using var sut = new DllKeyDerivationAlgorithm(stub);
        var key = sut.ComputeKey(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 }, 0x01);

        key.Should().Equal(new byte[] { 0x11, 0x22, 0x33 },
            "the wrapper must copy exactly keyOutLen bytes from the native buffer");
        _receivedSeed.Should().Equal(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 },
            "seed and seedLen reach the native export verbatim");
        _receivedSeedLen.Should().Be(4);
        _receivedLevel.Should().Be(0x01);
    }

    [Fact]
    public void ComputeKey_TruncatesToKeyOutLen_WhenBufferPartiallyFilled()
    {
        // The native export writes fewer bytes than the buffer advertises
        // (e.g. an OEM algorithm whose output length depends on seed
        // length). The wrapper must not return trailing zero padding —
        // only keyOutLen bytes.
        DllKeyDerivationAlgorithm.GenerateKeyDelegate stub =
            (byte[] seed, int seedLen, byte[] keyOut, ref int keyOutLen, byte level) =>
            {
                keyOut[0] = 0xDE;
                keyOut[1] = 0xAD;
                keyOut[2] = 0xBE;
                keyOut[3] = 0xEF;
                keyOutLen = 2;
                return 0;
            };

        using var sut = new DllKeyDerivationAlgorithm(stub);
        var key = sut.ComputeKey(new byte[] { 0x01, 0x02 }, 0x05);

        key.Should().Equal(new byte[] { 0xDE, 0xAD },
            "only the first keyOutLen bytes must be returned, no trailing buffer padding");
    }

    [Fact]
    public void ComputeKey_EmptyKeyBytesAreHonored()
    {
        // An OEM export that reports keyOutLen == 0 must yield an empty
        // key array (length 0), not a null, so the SendKey leg of
        // SecurityAccess treats it as a zero-length key rather than a NRE.
        DllKeyDerivationAlgorithm.GenerateKeyDelegate stub =
            (byte[] seed, int seedLen, byte[] keyOut, ref int keyOutLen, byte level) =>
            {
                keyOutLen = 0;
                return 0;
            };

        using var sut = new DllKeyDerivationAlgorithm(stub);
        var key = sut.ComputeKey(new byte[] { 0x01 }, 0x01);

        key.Should().NotBeNull().And.HaveCount(0);
    }

    // ---- error paths ----

    [Fact]
    public void ComputeKey_NullSeed_ThrowsArgumentNullException()
    {
        using var sut = new DllKeyDerivationAlgorithm(EchoStub());

        var act = () => sut.ComputeKey(null!, 0x01);

        act.Should().Throw<ArgumentNullException>(
            "IKeyDerivationAlgorithm.ComputeKey contract requires null-seed rejection");
    }

    [Fact]
    public void ComputeKey_NativeReturnsNonZero_ThrowsManagedException()
    {
        // A non-zero native return is the OEM's error code. The wrapper
        // MUST surface it as a managed exception — returning a silent
        // zero/empty key would let SendKey proceed and hit NRC 0x35
        // (invalidKey) with no hint that the algorithm itself failed.
        DllKeyDerivationAlgorithm.GenerateKeyDelegate stub =
            (byte[] seed, int seedLen, byte[] keyOut, ref int keyOutLen, byte level) =>
                0x7F /* OEM-defined error */;

        using var sut = new DllKeyDerivationAlgorithm(stub);
        var act = () => sut.ComputeKey(new byte[] { 0x01 }, 0x01);

        act.Should().Throw<InvalidOperationException>(
            "a non-zero native return must surface as a managed exception, not an empty key");
    }

    [Fact]
    public void ComputeKey_NativeException_PropagatesNotSwallowed()
    {
        // If the bound thunk itself throws (defense-in-depth — native
        // exports should not throw, but a managed thunk could), the wrapper
        // must not swallow it into a zero return.
        DllKeyDerivationAlgorithm.GenerateKeyDelegate stub =
            (byte[] seed, int seedLen, byte[] keyOut, ref int keyOutLen, byte level) =>
                throw new InvalidOperationException("native fault");

        using var sut = new DllKeyDerivationAlgorithm(stub);
        var act = () => sut.ComputeKey(new byte[] { 0x01 }, 0x01);

        act.Should().Throw<InvalidOperationException>().WithMessage("native fault");
    }

    // ---- interface conformance ----

    [Fact]
    public void Sut_Implements_IKeyDerivationAlgorithm()
    {
        using var sut = new DllKeyDerivationAlgorithm(EchoStub());

        // Assert both contracts explicitly: IKeyDerivationAlgorithm (the Core
        // service contract) and IDisposable (the C1-decided teardown contract
        // that only DllKey needs, NOT the interface).
        sut.Should().BeAssignableTo<IKeyDerivationAlgorithm>(
            "DllKey must satisfy the Core IKeyDerivationAlgorithm contract");
    }

    [Fact]
    public void Sut_Implements_IDisposable_SoCallersCanReleaseNativeHandle()
    {
        // C1 decision: DllKeyDerivationAlgorithm owns a NativeLibrary
        // handle and must be Disposable. The INTERFACE
        // IKeyDerivationAlgorithm is intentionally NOT Disposable to keep
        // all existing implementations (Placeholder, Fake) contract-stable;
        // only DllKey needs teardown.
        using var sut = new DllKeyDerivationAlgorithm(EchoStub());

        sut.Should().BeAssignableTo<IDisposable>(
            "DllKey must be Disposable to release the NativeLibrary handle");
    }

    [Fact]
    public void ComputeKey_AfterDispose_ThrowsObjectDisposedException()
    {
        // Repeated use after Dispose must fail loudly — silently re-loading
        // a freed handle would call into unmapped memory.
        var sut = new DllKeyDerivationAlgorithm(EchoStub());
        sut.Dispose();
        sut.Dispose(); // idempotent dispose must not throw

        var act = () => sut.ComputeKey(new byte[] { 0x01 }, 0x01);

        act.Should().Throw<ObjectDisposedException>();
    }

    // ---- ctor guards (the production ctor that loads a real DLL) ----

    [Fact]
    public void Ctor_RealDll_NullPath_ThrowsArgumentNullException()
    {
        var act = () => new DllKeyDerivationAlgorithm((string)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_RealDll_NullFunctionName_ThrowsArgumentNullException()
    {
        var act = () =>
        {
            using var sut = new DllKeyDerivationAlgorithm("phase1-c3-nonexistent.dll", functionName: null!);
        };
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_RealDll_UnresolvableLibrary_SurfacesManagedExceptionWithPath()
    {
        // No OEM ships a library named "phase1-c3-nonexistent.dll"; the
        // production ctor must surface the missing DLL as a managed
        // exception so the flashing VM can present "DLL not found" to
        // the operator instead of a silent fall-through to Placeholder.
        var act = () =>
        {
            using var sut = new DllKeyDerivationAlgorithm("phase1-c3-nonexistent.dll");
        };

        // NativeLibrary cannot resolve a truly absent library; the wrapper
        // must surface a SystemException whose message references the path
        // so the operator can locate the missing file.
        act.Should().Throw<SystemException>(
            "an unresolvable OEM DLL must not be silently swallowed")
            .Where(ex => ex.Message.Contains("phase1-c3-nonexistent.dll"),
                "the failing path must be in the message so the operator can locate it");
    }
}
