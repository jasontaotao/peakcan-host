namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// v1.7.0 MINOR Item 1: V8 isolate resource caps. Bound from
/// <c>Script:MaxHeapSizeMB</c> + sibling keys in <c>appsettings.json</c>
/// via <c>AppHostBuilder</c>.
/// </summary>
/// <param name="MaxHeapSizeMB">
/// Soft cap on V8 isolate heap in megabytes, applied via
/// <c>V8ScriptEngine.MaxRuntimeHeapSize</c> (in bytes). Drives heap
/// monitoring; <paramref name="MaxNewSpaceSizeMB"/> and
/// <paramref name="MaxOldSpaceSizeMB"/> drive hard generation caps.
/// Default 64 MB.
/// </param>
/// <param name="MaxNewSpaceSizeMB">
/// V8 new-space (young generation) hard cap in MiB. Applied via
/// <c>V8RuntimeConstraints.MaxNewSpaceSize</c>. Default 16 MiB.
/// </param>
/// <param name="MaxOldSpaceSizeMB">
/// V8 old-space (tenured generation) hard cap in MiB. Applied via
/// <c>V8RuntimeConstraints.MaxOldSpaceSize</c>. Default 48 MiB.
/// </param>
/// <remarks>
/// Enforcement layers (verified against ClearScript 7.4.5 XML docs):
/// <list type="bullet">
/// <item><c>MaxNewSpaceSizeMB</c> + <c>MaxOldSpaceSizeMB</c> are <b>hard
/// generation caps</b> via <c>V8RuntimeConstraints.Max*SpaceSize</c> in MiB.</item>
/// <item><c>MaxHeapSizeMB</c> is the <b>heap-size monitor</b> via
/// <c>V8ScriptEngine.MaxRuntimeHeapSize</c> in bytes. With ClearScript's
/// default <c>HeapSizePolicy.Interrupt</c>, exceeding it surfaces a
/// catchable JS exception (NOT a native crash).</item>
/// </list>
/// <c>MaxNewSpaceSizeMB + MaxOldSpaceSizeMB</c> should sum to roughly
/// <paramref name="MaxHeapSizeMB"/> for coherent budgeting.
/// <para>Units distinction:
/// <list type="bullet">
/// <item><c>V8RuntimeConstraints.Max*SpaceSize</c> use MiB.</item>
/// <item><c>V8ScriptEngine.MaxRuntimeHeapSize</c> uses bytes.</item>
/// </list></para>
/// </remarks>
internal sealed record ScriptEngineOptions(
    int MaxHeapSizeMB = 64,
    int MaxNewSpaceSizeMB = 16,
    int MaxOldSpaceSizeMB = 48)
{
    /// <summary>Default limits per spec: 64 MB heap, 16 MiB new, 48 MiB old.</summary>
    public static ScriptEngineOptions Default { get; } = new();
}
