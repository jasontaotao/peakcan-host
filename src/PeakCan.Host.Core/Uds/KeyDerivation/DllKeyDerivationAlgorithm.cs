using System.Runtime.InteropServices;

namespace PeakCan.Host.Core.Uds.KeyDerivation;

/// <summary>
/// OEM-supplied native DLL key derivation wrapper for UDS SecurityAccess
/// (0x27). Drives an OEM-confidential key algorithm that lives in a native
/// library (e.g. an HSM thunk, a custom AES/CRC scheme) by calling a single
/// exported function whose ABI is fixed by the
/// <see cref="GenerateKeyDelegate"/> contract below.
/// <para>
/// Phase 1 C3 design (flashing feature 2026-07-21):
/// <list type="bullet">
///   <item><b>Not registered in DI as a singleton</b> — the diagnostic
///   <see cref="UdsClient"/> singleton is constructed with the
///   <see cref="PlaceholderKeyAlgorithm"/>. A flashing session constructs a
///   SECONDARY <see cref="UdsClient"/> and hands it a freshly-created
///   <see cref="DllKeyDerivationAlgorithm"/> instance, then Disposes both at
///   flash completion. This avoids the ctor capture problem: <see cref="UdsClient"/>'s
///   <c>_keyAlgorithm</c> field is <c>readonly</c> and bound at construction,
///   so swapping the algorithm on the DI singleton would require mutating
///   shared state visible to the diagnostic tabs.</item>
///   <item><b>Implements <see cref="IDisposable"/> but <see cref="IKeyDerivationAlgorithm"/>
///   does NOT</b> (C1 decision). The interface is intentionally kept minimal so
///   the existing <see cref="PlaceholderKeyAlgorithm"/> and test fakes are
///   contract-stable. Only this wrapper owns a native handle and needs
///   teardown; callers that hold a typed <see cref="DllKeyDerivationAlgorithm"/>
///   reference Dispose it directly. Do not attempt to Dispose via the
///   <see cref="IKeyDerivationAlgorithm"/> reference.</item>
/// </list>
/// </para>
/// <para>
/// <b>OEM DLL export contract</b> — OEMs implement and ship a native
/// library with this single exported function:
/// <code>
/// // C export — cdecl calling convention (set by UnmanagedFunctionPointer below).
/// int GenerateKey(
///     const unsigned char* seed,   // [in]    requestSeed bytes
///     int                        seedLen,  // [in]    length of seed
///     unsigned char*             keyOut,   // [out]   caller-provided buffer
///     int*                       keyOutLen,// [in,out] caller sets buffer cap;
///                                          //          export writes bytes-written
///     unsigned char              securityLevel); // [in]  SecurityAccess sub-function
/// // Returns 0 on success; non-zero is an OEM-defined error code.
/// </code>
/// The wrapper passes a 256-byte <paramref name="keyOut"/> cap and trusts the
/// OEM export to write its derived key there. If the export requires more
/// space, raise the cap here (the SecurityAccess key length is bounded by the
/// 0x27 sub-function payload anyway — well under 256 bytes for every known OEM).
/// </para>
/// </summary>
public sealed class DllKeyDerivationAlgorithm : IKeyDerivationAlgorithm, IDisposable
{
    /// <summary>
    /// Native export ABI. Cdecl matches the typical OEM DLL convention;
    /// OEMs MUST compile their export with the matching calling convention
    /// or the thunk will smash the stack. Kept public so test fixtures can
    /// construct a managed thunk with the same signature without a real DLL.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GenerateKeyDelegate(
        byte[] seed,
        int seedLen,
        byte[] keyOut,
        ref int keyOutLen,
        byte securityLevel);

    /// <summary>Maximum bytes the wrapper will allocate for the native keyOut buffer.</summary>
    private const int KeyBufferCapacity = 256;

    private readonly GenerateKeyDelegate _generateKey;
    private readonly IntPtr _nativeHandle;
    private readonly bool _ownsNativeHandle;
    private bool _disposed;

    /// <summary>
    /// Production ctor: load <paramref name="dllPath"/> via
    /// <see cref="NativeLibrary"/>, resolve <paramref name="functionName"/>
    /// to a thunk, and bind a <see cref="GenerateKeyDelegate"/> to it. The
    /// handle is freed in <see cref="Dispose"/>.
    /// </summary>
    /// <param name="dllPath">Absolute or DLL-search-path-relative path to the OEM DLL.</param>
    /// <param name="functionName">Exported function name. Defaults to "GenerateKey".</param>
    /// <exception cref="ArgumentNullException">A null argument was passed.</exception>
    /// <exception cref="SystemException">
    ///   The DLL could not be loaded or the export was not found. The path
    ///   (and function name, when applicable) appears in the message so the
    ///   operator can locate the missing file rather than diagnose a silent
    ///   Placeholder fall-through.
    /// </exception>
    public DllKeyDerivationAlgorithm(string dllPath, string functionName = "GenerateKey")
    {
        ArgumentNullException.ThrowIfNull(dllPath);
        ArgumentNullException.ThrowIfNull(functionName);

        // NativeLibrary.Load throws DllNotFoundException on resolution
        // failure (it does NOT return IntPtr.Zero on this platform); catch
        // and re-wrap with a path-bearing message so the UI operator can
        // locate the missing file rather than diagnose a generic "module
        // not found" error that omits the supplied path.
        IntPtr handle;
        try
        {
            handle = NativeLibrary.Load(dllPath);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or IOException)
        {
            throw new SystemException(
                $"OEM SecurityAccess DLL could not be loaded: '{dllPath}'. " +
                $"Verify the path and that the correct DLL is present for the target ECU " +
                $"(underlying failure: {ex.Message}).",
                ex);
        }

        // Defensive: on a platform whose NativeLibrary.Load returns zero
        // instead of throwing, also surface the failure explicitly.
        if (handle == IntPtr.Zero)
        {
            throw new SystemException(
                $"OEM SecurityAccess DLL could not be loaded: '{dllPath}'. " +
                "Verify the path and that the correct DLL is present for the target ECU.");
        }

        if (!NativeLibrary.TryGetExport(handle, functionName, out var exportPtr))
        {
            NativeLibrary.Free(handle);
            throw new SystemException(
                $"OEM SecurityAccess DLL '{dllPath}' is missing the exported " +
                $"function '{functionName}'. The OEM DLL must export a cdecl " +
                $"function with the GenerateKey(seed, seedLen, keyOut, " +
                $"keyOutLen, securityLevel) signature.");
        }

        _nativeHandle = handle;
        _ownsNativeHandle = true;
        _generateKey = Marshal.GetDelegateForFunctionPointer<GenerateKeyDelegate>(exportPtr);
    }

    /// <summary>
    /// Test seam ctor: bind an already-constructed managed thunk (e.g. a
    /// lambda capturing test state) WITHOUT loading a native library. This
    /// lets the buffer-handling / error-code-translation logic be unit-tested
    /// without shipping a C fixture DLL. Test-only — production never invokes
    /// this ctor.
    /// </summary>
    internal DllKeyDerivationAlgorithm(GenerateKeyDelegate generateKey)
    {
        ArgumentNullException.ThrowIfNull(generateKey);
        _generateKey = generateKey;
        // No native handle in the test-seam path; Dispose is a no-op aside
        // from flipping the _disposed flag so post-dispose access still
        // fails loudly (the tests assert this).
        _nativeHandle = IntPtr.Zero;
        _ownsNativeHandle = false;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="seed"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The wrapper has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///   The native export returned a non-zero error code. A silent empty
    ///   key would let SecurityAccess SendKey proceed into NRC 0x35 with no
    ///   operator indication of the algorithm failure, so the wrapper
    ///   surfaces the OEM error code here instead.
    /// </exception>
    public byte[] ComputeKey(byte[] seed, byte securityLevel)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (_disposed)
            throw new ObjectDisposedException(nameof(DllKeyDerivationAlgorithm));

        var keyOut = new byte[KeyBufferCapacity];
        var keyOutLen = KeyBufferCapacity;

        // The thunk writes its result into keyOut and sets keyOutLen to the
        // number of bytes actually written (≤ capacity). A non-zero return
        // is the OEM error code.
        int rc = _generateKey(seed, seed.Length, keyOut, ref keyOutLen, securityLevel);

        if (rc != 0)
        {
            throw new InvalidOperationException(
                $"OEM SecurityAccess key derivation failed with native error code 0x{rc:X2} " +
                $"(security level 0x{securityLevel:X2}).");
        }

        // Defensive bound — a misbehaving export could set keyOutLen beyond
        // the buffer cap and the wrapper must never return uninitialized
        // memory past the buffer end.
        if (keyOutLen < 0)
            keyOutLen = 0;
        if (keyOutLen > KeyBufferCapacity)
            keyOutLen = KeyBufferCapacity;

        var key = new byte[keyOutLen];
        if (keyOutLen > 0)
            Array.Copy(keyOut, key, keyOutLen);
        return key;
    }

    /// <summary>
    /// Release the native library handle bound at construction (production
    /// ctor only; the test-seam ctor owns no native handle and this is a
    /// no-op besides the disposed flag). Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_ownsNativeHandle && _nativeHandle != IntPtr.Zero)
        {
            NativeLibrary.Free(_nativeHandle);
        }
    }
}
