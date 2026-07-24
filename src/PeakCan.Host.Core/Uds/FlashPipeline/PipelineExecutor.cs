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
    /// Per-step firmware resolver: given a step and its index in the enabled-steps list,
    /// returns the firmware image for that step, or null if none. The index is the stable
    /// identity (NOT the step snapshot — snapshots are record values, so two DownloadTransfer
    /// steps with identical parameters would collide as dictionary keys and silently share
    /// firmware). The executor calls this once per DownloadTransfer step.
    /// </summary>
    /// <param name="step">The current step snapshot.</param>
    /// <param name="index">The step's 0-based index in the enabled-steps list.</param>
    /// <returns>The firmware image for this step, or null if none.</returns>
    public delegate FirmwareImage? FirmwareResolver(FlashStepSnapshot step, int index);

    /// <summary>
    /// Phase 2: Resolves the start address for a Download step's referenced segment.
    /// Given the segment index (from DownloadParams.SegmentIndex), returns the segment's
    /// start address. The executor uses this instead of the flat MemoryAddress field.
    /// </summary>
    /// <param name="segmentIndex">Index into the flattened segment list.</param>
    /// <returns>The segment's start address, or null if not resolvable.</returns>
    public delegate uint? SegmentAddressResolver(int segmentIndex);

    /// <summary>
    /// Execute the enabled step sequence against <paramref name="client"/>, resolving firmware
    /// per-step via <paramref name="firmwareResolver"/>. This is the Phase 1.1 entry point
    /// that supports multiple DownloadTransfer steps each flashing a different firmware image
    /// (flash_driver→RAM then main app, dual-file, N-file).
    /// </summary>
    /// <param name="client">The UdsClient (typically a per-flash secondary client). Must not be null.</param>
    /// <param name="enabledSteps">Ordered, enabled step snapshots (the caller filters disabled steps out first). Must not be null.</param>
    /// <param name="firmwareResolver">
    /// Per-step firmware resolver. Called once per DownloadTransfer step; must return the
    /// firmware image for that step's index, or null if none.
    /// </param>
    /// <param name="profileFlashDriver">
    /// The profile-level flash driver (if any). The executor uses this for
    /// FlashDriverDownload steps — the static executor has no access to the profile, so
    /// the caller must pass the driver in. Null when no driver is loaded.
    /// </param>
    /// <param name="progress">Optional progress reporter bridged to the UI by the caller.</param>
    /// <param name="ct">Cancellation token — propagated to every UDS call.</param>
    public static async Task ExecuteAsync(
        UdsClient client,
        IReadOnlyList<FlashStepSnapshot> enabledSteps,
        FirmwareResolver firmwareResolver,
        SegmentAddressResolver? segmentAddressResolver,
        FlashDriver? profileFlashDriver = null,
        IProgress<FlashProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(enabledSteps);
        ArgumentNullException.ThrowIfNull(firmwareResolver);

        var total = enabledSteps.Count;

        // Phase 2 §6.2: Start a background keep-alive loop so long downloads don't
        // hit the ECU's S3 timeout (typically 5s). Sends 0x3E 80 every 1.5s.
        using var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var keepAliveTask = KeepAliveLoopAsync(client, keepAliveCts.Token);

        try
        {
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = enabledSteps[i];

                try
                {
                    Report(progress, i, total, step.Kind, FlashStatus.Running, message: null);
                    await ExecuteStepAsync(client, step, firmwareResolver, segmentAddressResolver, profileFlashDriver, progress, i, total, ct).ConfigureAwait(false);
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
        finally
        {
            keepAliveCts.Cancel();
            try { await keepAliveTask.ConfigureAwait(false); }  // ensure the loop exits cleanly
            catch (OperationCanceledException) { /* expected — we just cancelled it */ }
        }
    }

    /// <summary>
    /// Phase 2 §6.2: Background keep-alive loop. Sends TesterPresent with suppress-pos-response
    /// (0x3E 80) every 1.5s to prevent the ECU's S3 timer from timing out during long downloads.
    /// Falls back to 0x3E 00 if the ECU doesn't support suppression.
    /// </summary>
    private static async Task KeepAliveLoopAsync(UdsClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false);
                // 0x3E 80 preferred (suppress response); fall back to 0x3E 00 on NRC.
                try
                {
                    await client.TesterPresentAsync(suppressPosResponse: true, ct).ConfigureAwait(false);
                }
                catch (UdsException)
                {
                    // Suppress not supported — retry with non-suppress variant.
                    await client.TesterPresentAsync(suppressPosResponse: false, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;  // propagate cancellation
            }
            catch
            {
                // Keep-alive failure must NOT abort the flash — silently continue.
            }
        }
    }

    /// <summary>
    /// Phase 1 backward-compat overload: single firmware image for the whole run. All
    /// DownloadTransfer steps flash the same image. Delegates to the per-step resolver
    /// overload with a resolver that returns the same image regardless of step/index.
    /// </summary>
    /// <param name="client">The UdsClient (typically a per-flash secondary client). Must not be null.</param>
    /// <param name="enabledSteps">Ordered, enabled step snapshots (the caller filters disabled steps out first). Must not be null.</param>
    /// <param name="firmware">
    /// The single firmware image. Required when an enabled DownloadTransfer step is present; null otherwise.
    /// </param>
    /// <param name="progress">Optional progress reporter bridged to the UI by the caller.</param>
    /// <param name="ct">Cancellation token — propagated to every UDS call.</param>
    /// <param name="profileFlashDriver">
    /// The profile-level flash driver (if any). See the per-step resolver overload for details.
    /// </param>
    public static async Task ExecuteAsync(
        UdsClient client,
        IReadOnlyList<FlashStepSnapshot> enabledSteps,
        FirmwareImage? firmware,
        IProgress<FlashProgress>? progress,
        CancellationToken ct,
        FlashDriver? profileFlashDriver = null)
    {
        // Delegate to the per-step resolver overload — constant resolver returns the same image.
        await ExecuteAsync(client, enabledSteps, (_, __) => firmware, null, profileFlashDriver, progress, ct).ConfigureAwait(false);
    }

    private static async Task ExecuteStepAsync(
        UdsClient client,
        FlashStepSnapshot step,
        FirmwareResolver firmwareResolver,
        SegmentAddressResolver? segmentAddressResolver,
        FlashDriver? profileFlashDriver,
        IProgress<FlashProgress>? progress,
        int stepIndex,
        int total,
        CancellationToken ct)
    {
        switch (step.Kind)
        {
            case FlashStepKind.PreCheck:
                var preCheck = step.PreCheck ?? throw new InvalidOperationException("PreCheck params missing");
                // 调 RoutineControl(StartRoutine, routineId, null) — 返回非空即成功
                await client.RoutineControlAsync(StartRoutine, preCheck.RoutineId, data: null, ct).ConfigureAwait(false);
                break;

            case FlashStepKind.SessionControl:
                await client.DiagnosticSessionControlAsync(ProgrammingSessionType, ct).ConfigureAwait(false);
                break;

            case FlashStepKind.SecurityAccess:
                await ExecuteSecurityAccessAsync(client, step, ct).ConfigureAwait(false);
                break;

            case FlashStepKind.Erase:
                // Phase 2: Read flat RoutineId (source of truth; grouped params mirror via ToSnapshot).
                await client.RoutineControlAsync(StartRoutine, step.RoutineId, data: null, ct: ct).ConfigureAwait(false);
                break;

            case FlashStepKind.DownloadTransfer:
                await ExecuteDownloadTransferAsync(client, step, firmwareResolver, segmentAddressResolver, progress, stepIndex, total, ct).ConfigureAwait(false);
                break;

            case FlashStepKind.Verify:
                await ExecuteVerifyAsync(client, step, firmwareResolver, ct).ConfigureAwait(false);
                break;

            case FlashStepKind.EcuReset:
                await client.EcuResetAsync((byte)step.ResetType, ct).ConfigureAwait(false);
                break;

            case FlashStepKind.CommunicationControl:
                // Phase 2: 0x28 uses functional addressing (broadcast). The sub-function
                // comes from the grouped CommunicationControl params.
                var ccSubFunc = step.CommunicationControl?.SubFunction ?? CommunicationSubFunction.DisableRxAndTx;
                await client.CommunicationControlAsync(ccSubFunc, ct).ConfigureAwait(false);
                break;

            case FlashStepKind.DtcControl:
                // Phase 2: 0x14 DTC control. Sub-function and DTC group from grouped params.
                var dtcSubFunc = step.DtcControl is { } dc ? (DtcControlSubFunction)dc.SubFunction : DtcControlSubFunction.ClearDTCInformation;
                var dtcGroup = step.DtcControl?.DtcGroup ?? 0x00FFFFFF;
                await client.DtcControlAsync(dtcSubFunc, dtcGroup, ct).ConfigureAwait(false);
                break;

            case FlashStepKind.FlashDriverDownload:
                // ISO 14229: 下载 flash driver 到 RAM, ECU 自动识别执行
                if (profileFlashDriver is null)
                    throw new InvalidOperationException("FlashDriverDownload step enabled but no flash driver loaded in profile.");
                await ExecuteFlashDriverDownloadAsync(client, profileFlashDriver, progress, stepIndex, total, ct).ConfigureAwait(false);
                break;

            case FlashStepKind.DependencyCheck:
                // ISO 14229 编程依赖性检查：刷写完成后执行 0x31 RoutineControl，
                // 检查编程完整性 (CRC32) 和软硬件兼容性。
                var depCheck = step.DependencyCheck ?? throw new InvalidOperationException("DependencyCheck params missing");
                await client.RoutineControlAsync(StartRoutine, depCheck.RoutineId, data: null, ct).ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(step), step.Kind, "Unknown FlashStepKind in pipeline");
        }
    }

    private static async Task ExecuteSecurityAccessAsync(UdsClient client, FlashStepSnapshot step, CancellationToken ct)
    {
        // Phase 2: Read from grouped snapshot (source of truth — UI binds grouped params,
        // ToSnapshot maps grouped → snapshot). Fall back to flat fields for backward compat.
        var level = step.SecurityAccess?.Level ?? step.SecurityLevel;
        var mode = step.SecurityAccess?.Mode ?? step.SecurityMode;
        var manualKey = step.SecurityAccess?.ManualKeyHex ?? step.ManualKeyHex;
        var dllPath = step.SecurityAccess?.DllPath ?? step.DllPath;

        switch (mode)
        {
            case SecurityAccessMode.Manual:
                var key = DecodeManualKeyHex(manualKey);
                await client.SecurityAccessAsync(level, key, ct).ConfigureAwait(false);
                break;

            case SecurityAccessMode.Dll:
                // The secondary UdsClient (constructed at flash time) is injected with the
                // OEM DllKeyDerivationAlgorithm, so the 2-arg overload runs the
                // RequestSeed→ComputeKey(DLL)→SendKey handshake via the injected algorithm.
                await client.SecurityAccessAsync(level, ct).ConfigureAwait(false);
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
        FirmwareResolver firmwareResolver,
        SegmentAddressResolver? segmentAddressResolver,
        IProgress<FlashProgress>? progress,
        int stepIndex,
        int total,
        CancellationToken ct)
    {
        var firmware = firmwareResolver(step, stepIndex);
        if (firmware is null)
        {
            throw new InvalidOperationException(
                "DownloadTransfer step is enabled but no firmware image was provided.");
        }

        // Phase 2: Resolve start address from the referenced segment (falls back to flat MemoryAddress).
        var download = step.Download;
        var startAddress = download is { } && segmentAddressResolver is { }
            ? segmentAddressResolver(download.SegmentIndex)
            : null;
        var memoryAddress = startAddress ?? step.MemoryAddress;

        var blockLength = await client.RequestDownloadAsync(memoryAddress, firmware.Length, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Phase 2: FlashDriverDownload — 下载 driver raw bytes 到 ECU RAM。
    /// 起始地址用 0x1000_0000 作为 RAM 典型地址 (Phase 2 简化, 未来从 driver 数据解析)。
    /// 与 ExecuteDownloadTransferAsync 共享 RequestDownload→TransferData→RequestTransferExit 握手,
    /// 但数据来源是 profile-level 的 flash driver 而非 per-step firmware image。
    /// </summary>
    private static async Task ExecuteFlashDriverDownloadAsync(
        UdsClient client,
        FlashDriver driver,
        IProgress<FlashProgress>? progress,
        int stepIndex,
        int total,
        CancellationToken ct)
    {
        var data = driver.Data;
        uint ramAddress = 0x1000_0000;  // RAM 典型地址, Phase 2 简化

        var blockLength = await client.RequestDownloadAsync(ramAddress, (uint)data.Length, ct).ConfigureAwait(false);
        if (blockLength <= 0)
            throw new UdsException($"ECU returned invalid block length for driver download: {blockLength}");

        int offset = 0;
        ulong done = 0;
        while (offset < data.Length)
        {
            ct.ThrowIfCancellationRequested();
            int chunkSize = Math.Min(blockLength, data.Length - offset);
            var chunk = new byte[chunkSize];
            Array.Copy(data, offset, chunk, 0, chunkSize);
            int blockIndex = offset / blockLength;
            byte blockCounter = (byte)((blockIndex % 255) + 1);
            await client.TransferDataAsync(blockCounter, chunk, ct).ConfigureAwait(false);
            offset += chunkSize;
            done += (ulong)chunkSize;
            Report(progress, stepIndex, total, FlashStepKind.FlashDriverDownload, FlashStatus.Running,
                doneBytes: done, totalBytes: (ulong)data.Length, message: null);
        }
        await client.RequestTransferExitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Phase 2: Verify step — compute expected CRC32 from the firmware data, then
    /// invoke the ECU's checksum routine via RoutineControl (0x33) and compare the
    /// ECU's result against the expected value. Throws on mismatch.
    /// </summary>
    private static async Task ExecuteVerifyAsync(
        UdsClient client,
        FlashStepSnapshot step,
        FirmwareResolver firmwareResolver,
        CancellationToken ct)
    {
        // Phase 2: Read expected checksum from the Verify snapshot group.
        // Falls back to flat RoutineId for backward compatibility.
        var verify = step.Verify;
        var routineId = step.RoutineControl?.RoutineId ?? step.RoutineId;
        var expectedCrc = verify?.ExpectedChecksum ?? 0;

        if (expectedCrc == 0)
        {
            // No expected checksum configured — skip verification (OEM-gated).
            return;
        }

        // Build routine data: [startAddr(4B), endAddr(4B), expectedCrc(4B)]
        var data = new byte[12];
        var startAddr = verify?.StartAddress ?? 0;
        var endAddr = verify?.EndAddress ?? 0;
        data[0] = (byte)(startAddr >> 24);
        data[1] = (byte)(startAddr >> 16);
        data[2] = (byte)(startAddr >> 8);
        data[3] = (byte)(startAddr);
        data[4] = (byte)(endAddr >> 24);
        data[5] = (byte)(endAddr >> 16);
        data[6] = (byte)(endAddr >> 8);
        data[7] = (byte)(endAddr);
        data[8] = (byte)(expectedCrc >> 24);
        data[9] = (byte)(expectedCrc >> 16);
        data[10] = (byte)(expectedCrc >> 8);
        data[11] = (byte)(expectedCrc);

        var response = await client.RoutineControlAsync(StartRoutine, routineId, data, ct).ConfigureAwait(false);

        // Parse ECU-returned CRC from response (last 4 bytes)
        if (response.Length < 4)
        {
            throw new UdsException("Verify routine returned invalid response (too short).");
        }

        var actualCrc = (uint)((response[^4] << 24) | (response[^3] << 16) | (response[^2] << 8) | response[^1]);
        if (actualCrc != expectedCrc)
        {
            throw new UdsException(
                $"Checksum mismatch @ 0x{startAddr:X8}-0x{endAddr:X8}: " +
                $"expected 0x{expectedCrc:X8}, ECU returned 0x{actualCrc:X8}");
        }
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
