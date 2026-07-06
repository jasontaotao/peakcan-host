using PeakCan.Host.App.Services.Trace;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// Test-only <see cref="IAutoSavePrefsStore"/> that keeps state in memory
/// instead of writing to <c>%APPDATA%</c>. Extracted from
/// <see cref="TraceSessionAutoSaverTests"/> so
/// <see cref="ReplaySessionAutoSaverTests"/> can reuse it (v3.7.0 PATCH).
/// <para>
/// <b>Why needed:</b> NSubstitute's <c>Substitute.For&lt;IAutoSavePrefsStore&gt;()</c>
/// auto-stubs <see cref="IAutoSavePrefsStore.LoadAsync"/> to a completed
/// <c>Task&lt;AutoSavePrefs&gt;</c> whose <c>.Result</c> is
/// <c>default(AutoSavePrefs)</c> = <c>null</c> (record is a reference
/// type). <c>ReplaySessionAutoSaver.ApplyAutoSnapshotAsync</c> then NREs
/// on <c>prefs.NeverRestore</c>, caught and reported as
/// <see cref="RestoreAnswer.ApplyFailed"/> — masking the actual test
/// intent. This helper returns a real <see cref="AutoSavePrefs"/> so
/// the prompt/prefs path runs deterministically.
/// </para>
/// </summary>
internal sealed class InMemoryPrefsStore : IAutoSavePrefsStore
{
    public AutoSavePrefs Current { get; set; } = new(NeverRestore: false);
    public Task<AutoSavePrefs> LoadAsync(CancellationToken ct) =>
        Task.FromResult(Current);
    public Task SaveAsync(AutoSavePrefs prefs, CancellationToken ct)
    {
        Current = prefs;
        return Task.CompletedTask;
    }
}