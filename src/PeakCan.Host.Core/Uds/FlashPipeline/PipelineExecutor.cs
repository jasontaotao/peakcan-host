using System.Globalization;

namespace PeakCan.Host.Core.Uds.FlashPipeline;

/// <summary>
/// Walks the enabled flashing-pipeline steps and dispatches each onto a <see cref="UdsClient"/>
/// method in order. Pure execution logic — no UI, no DI; the App-layer FlashPanelViewModel
/// supplies the <see cref="UdsClient"/>, the step snapshots, the firmware image, the progress
/// bridge and the cancellation token. Keeps Core free of any UI dependency.
/// <para>
/// The dispatch surfaces the ECU-reported block length from <see cref="UdsClient.RequestDownloadAsync"/>
/// as the TransferData chunk size (TransferFlow.cs: the response carries maxNumberOfBlockLength).
/// The block sequence counter rolls over modulo 255 → 1 per ISO 14229-1 §10.6.3.4 (255 wraps
/// to 1, NOT 0).
/// </para>
/// <para>
/// On an unhandled step exception with <see cref="FlashStepSnapshot.AutoResetOnFailure"/> set,
/// the executor tries <see cref="UdsClient.EcuResetAsync"/>(0x01) as a safety net so the ECU
/// is not left half-flashed, then RE-THROWS the original exception — auto-reset is a net,
/// not an error handler, so the UI can still surface the root cause.
/// </para>
/// </summary>
public static class PipelineExecutor
{
    /// <summary>ISO 14229 Programming sessionType byte.</summary>
    public const byte ProgrammingSessionType = 0x03;

    /// <summary>RoutineControl sub-function: StartRoutine.</summary>
    public const byte StartRoutine = 0x01;

    /// <summary>
    /// Execute the enabled step sequence against <paramref name="client"/>.
    /// </summary>
    /// <param name="client">The UdsClient (typically a per-flash secondary client). Must not be null.</param>
    /// <param name="enabledSteps">Ordered, enabled step snapshots (the caller filters disabled steps out first). Must not be null.</param>
    /// <param name="firmware">
    /// The firmware image. Required when an enabled DownloadTransfer step is present; null otherwise.
    /// </param>
    /// <param name="progress">Optional progress reporter bridged to the UI by the caller.</param>
    /// <param name="ct">Cancellation token — propagated to every UDS call.</param>
    public static async Task ExecuteAsync(
        UdsClient client,
        IReadOnlyList<FlashStepSnapshot> enabledSteps,
        FirmwareImage? firmware,
        IProgress<FlashProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(enabledSteps);

        var total = enabledSteps.Count;
        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = enabledSteps[i];

            try
            {
                Report(progress, i, total, step.Kind, FlashStatus.Running, message: null);
                await ExecuteStepAsync(client, step, firmware, progress, i, total, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is operator-intent, NOT a failure — never triggers the auto-reset net.
                progress?.Report(MakeProgress(i, total, step.Kind, FlashStatus.Cancelled, message: "Cancelled"));
                throw;
            }
            catch (Exception ex)
            {
                // Safety net: auto-reset to leave the ECU in a sane state on failure.
                if (step.AutoResetOnFailure)
                {
                    try
                    {
                        await client.EcuResetAsync(0x01, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // A reset that itself fails must NOT mask the original cause.
                    }
                }
                progress?.Report(MakeProgress(i, total, step.Kind, FlashStatus.Failed, message: ex.Message));
                throw;
            }
        }

        if (total > 0)
        {
            progress?.Report(MakeProgress(total - 1, total, enabledSteps[total - 1].Kind, FlashStatus.Success, message: "Done"));
        }
    }

    private static async Task ExecuteStepAsync(
        UdsClient client,
        FlashStepSnapshot step,
        FirmwareImage? firmware,
        IProgress<FlashProgress>? progress,
        int stepIndex,
        int total,
        CancellationToken ct)
    {
        switch (step.Kind)
        {
            case FlashStepKind.PreCheck:
                // Phase 1 placeholder: executor defers pre-check enforcement to the operator.
                // An enabled PreCheck step does nothing — Phase 2 will wire precondition DIDs/routines.
                break;

            case FlashStepKind.SessionControl:
                await client.DiagnosticSessionControlAsync(ProgrammingSessionType, ct).ConfigureAwait(false);
                break;

            case FlashStepKind.SecurityAccess:
                await ExecuteSecurityAccessAsync(client, step, ct).ConfigureAwait(false);
                break;

            case FlashStepKind.Erase:
                await client.RoutineControlAsync(StartRoutine, step.RoutineId, data: null, ct: ct).ConfigureAwait(false);
                break;

            case FlashStepKind.DownloadTransfer:
                await ExecuteDownloadTransferAsync(client, step, firmware, progress, stepIndex, total, ct).ConfigureAwait(false);
                break;

            case FlashStepKind.Verify:
                await client.RoutineControlAsync(StartRoutine, step.RoutineId, data: null, ct: ct).ConfigureAwait(false);
                break;

            case FlashStepKind.EcuReset:
                await client.EcuResetAsync((byte)step.ResetType, ct).ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(step), step.Kind, "Unknown FlashStepKind in pipeline");
        }
    }

    private static async Task ExecuteSecurityAccessAsync(UdsClient client, FlashStepSnapshot step, CancellationToken ct)
    {
        switch (step.SecurityMode)
        {
            case SecurityAccessMode.Manual:
                var key = DecodeManualKeyHex(step.ManualKeyHex);
                await client.SecurityAccessAsync(step.SecurityLevel, key, ct).ConfigureAwait(false);
                break;

            case SecurityAccessMode.Dll:
                // The secondary UdsClient (constructed at flash time) is injected with the
                // OEM DllKeyDerivationAlgorithm, so the 2-arg overload runs the
                // RequestSeed→ComputeKey(DLL)→SendKey handshake via the injected algorithm.
                await client.SecurityAccessAsync(step.SecurityLevel, ct).ConfigureAwait(false);
                break;

            case SecurityAccessMode.Auto:
                // Phase 1 placeholder. Auto would reuse a DI-registered OEM key algorithm —
                // implies a deploy-time DI registration doc story deferred to Phase 3.
                throw new NotImplementedException(
                    "SecurityAccess Auto mode is not implemented in Phase 1. Select Manual or Dll, " +
                    "or wait for Phase 3's deploy-time DLL registration.");

            default:
                throw new ArgumentOutOfRangeException(nameof(step), step.SecurityMode, "Unknown SecurityAccessMode");
        }
    }

    /// <summary>
    /// Hex-decode the operator-typed Manual key string. Rejects non-hex characters and odd
    /// digit counts BEFORE any wire call — a garbage SendKey would hit the ECU and NRC 0x35
    /// (invalidKey) with no hint the input was bad locally.
    /// </summary>
    private static byte[] DecodeManualKeyHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            throw new ArgumentException("Manual SecurityAccess key is empty — supply hex bytes.", nameof(hex));
        }
        // Strip optional whitespace; reject embedded non-hex.
        var trimmed = hex.Replace(" ", string.Empty).Replace("\t", string.Empty);
        if (trimmed.Length == 0 || trimmed.Length % 2 != 0 || !IsAllHex(trimmed))
        {
            throw new ArgumentException(
                $"Manual SecurityAccess key '{hex}' is not valid hex (must be even-length hex digits).",
                nameof(hex));
        }

        var bytes = new byte[trimmed.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(trimmed.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return bytes;
    }

    private static bool IsAllHex(string s)
    {
        foreach (var c in s)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }

    private static async Task ExecuteDownloadTransferAsync(
        UdsClient client,
        FlashStepSnapshot step,
        FirmwareImage? firmware,
        IProgress<FlashProgress>? progress,
        int stepIndex,
        int total,
        CancellationToken ct)
    {
        if (firmware is null)
        {
            throw new InvalidOperationException(
                "DownloadTransfer step is enabled but no firmware image was provided.");
        }

        var blockLength = await client.RequestDownloadAsync(step.MemoryAddress, firmware.Length, ct).ConfigureAwait(false);
        if (blockLength <= 0)
        {
            throw new UdsException($"ECU returned an invalid block length: {blockLength} (must be > 0).");
        }

        int offset = 0;
        ulong done = 0;
        var data = firmware.Data;
        while (offset < data.Length)
        {
            ct.ThrowIfCancellationRequested();
            int chunkSize = Math.Min(blockLength, data.Length - offset);
            var chunk = new byte[chunkSize];
            Array.Copy(data, offset, chunk, 0, chunkSize);

            // ISO 14229-1 §10.6.3.4: blockSequenceCounter starts at 1 and wraps to 1
            // (not 0) after 255. blockIndex + 1 maps the 0-based loop counter to the
            // 1-based wire counter.
            int blockIndex = offset / blockLength;
            byte blockCounter = (byte)((blockIndex % 255) + 1);

            await client.TransferDataAsync(blockCounter, chunk, ct).ConfigureAwait(false);

            offset += chunkSize;
            done += (ulong)chunkSize;
            Report(progress, stepIndex, total, step.Kind, FlashStatus.Running,
                doneBytes: done, totalBytes: (ulong)data.Length, message: null);
        }

        await client.RequestTransferExitAsync(ct).ConfigureAwait(false);
    }

    private static FlashProgress MakeProgress(int stepIndex, int total, FlashStepKind kind,
        FlashStatus status, ulong? doneBytes = null, ulong? totalBytes = null, string? message = null) =>
        new()
        {
            CurrentStepIndex = stepIndex + 1, // 1-based for the "Step 3/7" label.
            TotalSteps = total,
            CurrentStepKind = kind,
            Status = status,
            CurrentStepDoneBytes = doneBytes,
            CurrentStepTotalBytes = totalBytes,
            Message = message,
        };

    private static void Report(IProgress<FlashProgress>? progress, int stepIndex, int total,
        FlashStepKind kind, FlashStatus status, ulong? doneBytes = null, ulong? totalBytes = null,
        string? message = null)
    {
        progress?.Report(MakeProgress(stepIndex, total, kind, status, doneBytes, totalBytes, message));
    }
}
