using System.Collections.Generic;
using FluentAssertions;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.FlashPipeline;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds.FlashPipeline;

/// <summary>
/// Phase 1 C4 Task 2.1–2.3: <see cref="PipelineExecutor"/> walks the enabled
/// flashing-pipeline steps and dispatches each onto a <see cref="UdsClient"/> method.
/// These tests use a recording/substituent-driven <see cref="RecordingUdsClient"/> that
/// overrides the 7 virtual executor-facing methods to capture call order + arguments and
/// let fast positive responses back without touching the wire. The executor uses
/// <see cref="RequestDownloadAsync"/>'s return as the TransferData chunk size — per the
/// codebase contract (TransferFlow.cs returns the ECU's maxNumberOfBlockLength) — so each
/// test configures a fixed block length.
/// </summary>
public sealed class PipelineExecutorTests
{
    // The ISO 14229 Programming sessionType byte (§10.2): 0x03.
    private const byte ProgrammingSession = 0x03;

    /// <summary>
    /// <see cref="RecordingUdsClient"/> inherits <see cref="UdsClient"/> (built on a no-op
    /// IsoTpLayer so no wire calls escape) and overrides every executor-facing virtual
    /// method to record the call + return a configurable positive response. The recorded
    /// log lets tests assert dispatch order + arguments without mock frameworks.
    /// </summary>
    private sealed class RecordingUdsClient : UdsClient
    {
        public RecordingUdsClient() : base(
            new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { }),
            new UdsTimer())
        {
        }

        public enum Op
        {
            SessionControl,
            SecurityAccess2Arg,
            SecurityAccess3Arg,
            RoutineControl,
            RequestDownload,
            TransferData,
            RequestTransferExit,
            EcuReset,
        }

        public int RoutineControlCallCount { get; private set; }

        public sealed record Call(Op Op, object? Arg1 = null, object? Arg2 = null, object? Arg3 = null);

        public readonly List<Call> Calls = new();

        // Configurable: RequestDownload returns this as maxNumberOfBlockLength (chunk size).
        public int DownloadBlockLength { get; set; } = 8;

        // Configurable: whether each call throws (used by failure-path tests).
        public Exception? ThrowOnNext { get; set; }

        public override Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync(byte sessionType, CancellationToken ct = default)
        {
            Calls.Add(new Call(Op.SessionControl, sessionType));
            MaybeThrow();
            return Task.FromResult(new DiagnosticSessionResponse { SessionType = sessionType, P2 = 50, P2Star = 5000 });
        }

        public override Task<byte[]> SecurityAccessAsync(byte level, byte[]? key = null, CancellationToken ct = default)
        {
            // 3-arg entry (key != null path = Manual). Record arg shape.
            Calls.Add(new Call(Op.SecurityAccess3Arg, level, key));
            MaybeThrow();
            return Task.FromResult(Array.Empty<byte>());
        }

        public override Task<byte[]> SecurityAccessAsync(byte requestLevel, CancellationToken ct = default)
        {
            // 2-arg entry (DLL/Auto path, no manual key).
            Calls.Add(new Call(Op.SecurityAccess2Arg, requestLevel));
            MaybeThrow();
            return Task.FromResult(Array.Empty<byte>());
        }

        public override Task<byte[]> RoutineControlAsync(byte routineControlType, ushort routineId, byte[]? data = null, CancellationToken ct = default)
        {
            // RoutineControlAsync (Erase / Verify dispatch). TransferFlow.cs declares this
            // virtual, so the executor's Erase/Verify path can run wire-free here.
            Calls.Add(new Call(Op.RoutineControl, routineControlType, routineId));
            RoutineControlCallCount++;
            MaybeThrow();
            return Task.FromResult(Array.Empty<byte>());
        }

        public override Task<int> RequestDownloadAsync(uint address, uint length, CancellationToken ct = default)
        {
            Calls.Add(new Call(Op.RequestDownload, address, length));
            MaybeThrow();
            return Task.FromResult(DownloadBlockLength);
        }

        public override Task TransferDataAsync(byte blockSequenceCounter, byte[] data, CancellationToken ct = default)
        {
            Calls.Add(new Call(Op.TransferData, blockSequenceCounter, data.Length));
            MaybeThrow();
            return Task.CompletedTask;
        }

        public override Task RequestTransferExitAsync(CancellationToken ct = default)
        {
            Calls.Add(new Call(Op.RequestTransferExit));
            MaybeThrow();
            return Task.CompletedTask;
        }

        public override Task<byte> EcuResetAsync(byte resetType, CancellationToken ct = default)
        {
            Calls.Add(new Call(Op.EcuReset, resetType));
            MaybeThrow();
            return Task.FromResult((byte)0);
        }

        private void MaybeThrow()
        {
            if (ThrowOnNext is { } ex)
            {
                ThrowOnNext = null;
                throw ex;
            }
        }
    }

    private static FlashStepSnapshot Step(FlashStepKind kind, ushort routineId = 0, uint memoryAddress = 0) => new()
    {
        Kind = kind,
        IsEnabled = true,
        RoutineId = routineId,
        MemoryAddress = memoryAddress,
    };

    [Fact]
    public async Task SessionControl_Step_Calls_DiagnosticSessionControl_With_0x03()
    {
        var client = new RecordingUdsClient();

        await PipelineExecutor.ExecuteAsync(client, new[] { Step(FlashStepKind.SessionControl) },
            firmware: null, progress: null, ct: default);

        client.Calls.Should().ContainSingle(c => c.Op == RecordingUdsClient.Op.SessionControl
            && (byte)c.Arg1! == ProgrammingSession);
    }

    [Fact]
    public async Task EcuReset_Step_Calls_EcuReset_With_Configured_ResetType()
    {
        var client = new RecordingUdsClient();
        var step = Step(FlashStepKind.EcuReset) with { ResetType = EcuResetType.KeyOffOn };

        await PipelineExecutor.ExecuteAsync(client, new[] { step }, firmware: null, progress: null, ct: default);

        client.Calls.Should().ContainSingle(c => c.Op == RecordingUdsClient.Op.EcuReset
            && (byte)c.Arg1! == (byte)EcuResetType.KeyOffOn);
    }

    [Fact]
    public async Task SecurityAccess_Manual_Decodes_Hex_And_Calls_3Arg_Overload()
    {
        var client = new RecordingUdsClient();
        var step = Step(FlashStepKind.SecurityAccess) with
        {
            SecurityMode = SecurityAccessMode.Manual,
            SecurityLevel = 0x01,
            ManualKeyHex = "DEADBEEF",
        };

        await PipelineExecutor.ExecuteAsync(client, new[] { step }, firmware: null, progress: null, ct: default);

        var secCall = client.Calls.Should().ContainSingle(c => c.Op == RecordingUdsClient.Op.SecurityAccess3Arg).Subject;
        (secCall.Arg1 as byte?).Should().Be(0x01);
        (secCall.Arg2 as byte[]).Should().Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
    }

    [Fact]
    public async Task SecurityAccess_Dll_Calls_2Arg_Overload()
    {
        var client = new RecordingUdsClient();
        var step = Step(FlashStepKind.SecurityAccess) with
        {
            SecurityMode = SecurityAccessMode.Dll,
            SecurityLevel = 0x0B,
        };

        await PipelineExecutor.ExecuteAsync(client, new[] { step }, firmware: null, progress: null, ct: default);

        var secCall = client.Calls.Should().ContainSingle(c => c.Op == RecordingUdsClient.Op.SecurityAccess2Arg).Subject;
        (secCall.Arg1 as byte?).Should().Be(0x0B);
    }

    [Fact]
    public async Task SecurityAccess_Auto_Throws_NotImplemented()
    {
        var client = new RecordingUdsClient();
        var step = Step(FlashStepKind.SecurityAccess) with { SecurityMode = SecurityAccessMode.Auto };

        var act = async () => await PipelineExecutor.ExecuteAsync(client, new[] { step },
            firmware: null, progress: null, ct: default);

        await act.Should().ThrowAsync<NotImplementedException>();
    }

    [Fact]
    public async Task SecurityAccess_Manual_Invalid_Hex_Throws_Before_Wire()
    {
        // PipelineExecutor must reject a malformed hex string BEFORE calling the UDS stack,
        // otherwise a garbage SendKey would hit the ECU and NRC 0x35 (invalidKey) with no
        // hint that the input was bad locally.
        var client = new RecordingUdsClient();
        var step = Step(FlashStepKind.SecurityAccess) with
        {
            SecurityMode = SecurityAccessMode.Manual,
            ManualKeyHex = "ZZ-not-hex-ZZ",
        };

        var act = async () => await PipelineExecutor.ExecuteAsync(client, new[] { step },
            firmware: null, progress: null, ct: default);

        await act.Should().ThrowAsync<ArgumentException>();
        client.Calls.Should().NotContain(c => c.Op == RecordingUdsClient.Op.SecurityAccess3Arg,
            "no wire call may escape when the hex is rejected locally");
    }

    [Fact]
    public async Task DownloadTransfer_Chunks_By_BlockLength_And_Increments_BlockCounter()
    {
        var client = new RecordingUdsClient { DownloadBlockLength = 4 };
        // 10 bytes with a block length of 4 → chunks of 4, 4, 2 → 3 TransferData calls.
        var firmware = FirmwareFileParser.Parse(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        var step = Step(FlashStepKind.DownloadTransfer) with { MemoryAddress = 0x0800_0000 };

        await PipelineExecutor.ExecuteAsync(client, new[] { step }, firmware, progress: null, ct: default);

        var transferCalls = client.Calls.Where(c => c.Op == RecordingUdsClient.Op.TransferData).ToList();
        transferCalls.Should().HaveCount(3);
        // Block sequence counter starts at 1 and increments each block.
        (transferCalls[0].Arg1 as byte?).Should().Be(1);
        (transferCalls[1].Arg1 as byte?).Should().Be(2);
        (transferCalls[2].Arg1 as byte?).Should().Be(3);
        // Chunk sizes: 4, 4, 2.
        (transferCalls[0].Arg2 as int?).Should().Be(4);
        (transferCalls[1].Arg2 as int?).Should().Be(4);
        (transferCalls[2].Arg2 as int?).Should().Be(2);

        client.Calls.Should().ContainSingle(c => c.Op == RecordingUdsClient.Op.RequestTransferExit,
            "the sequence MUST end with RequestTransferExit to close the transfer handshake");
    }

    [Fact]
    public async Task DownloadTransfer_BlockCounter_Wraps_After_255_To_One()
    {
        // ISO 14229-1 §10.6.3.4: the blockSequenceCounter rolls over to 1 after 255, not 0.
        // Drive 256 blocks with block length 1 over a 256-byte image.
        var client = new RecordingUdsClient { DownloadBlockLength = 1 };
        var firmwareBytes = new byte[256];
        for (int i = 0; i < 256; i++) firmwareBytes[i] = (byte)i;
        var firmware = FirmwareFileParser.Parse(firmwareBytes);
        var step = Step(FlashStepKind.DownloadTransfer) with { MemoryAddress = 0x0800_0000 };

        await PipelineExecutor.ExecuteAsync(client, new[] { step }, firmware, progress: null, ct: default);

        var transferCalls = client.Calls.Where(c => c.Op == RecordingUdsClient.Op.TransferData).ToList();
        transferCalls.Should().HaveCount(256);
        (transferCalls[254].Arg1 as byte?).Should().Be(255, "255th block is the last before wrap");
        (transferCalls[255].Arg1 as byte?).Should().Be(1, "256th block wraps to 1 per ISO 14229 rollover");
    }

    [Fact]
    public async Task DownloadTransfer_Step_Without_Firmware_Throws()
    {
        var client = new RecordingUdsClient();
        var step = Step(FlashStepKind.DownloadTransfer) with { MemoryAddress = 0x0800_0000 };

        var act = async () => await PipelineExecutor.ExecuteAsync(client, new[] { step },
            firmware: null, progress: null, ct: default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        client.Calls.Should().NotContain(c => c.Op == RecordingUdsClient.Op.RequestDownload);
    }

    [Fact]
    public async Task Failure_With_AutoReset_Triggers_EcuReset_Then_ReThrows_Main()
    {
        var client = new RecordingUdsClient();
        client.ThrowOnNext = new UdsException("ECU rejected Erase");
        var step = Step(FlashStepKind.Erase, routineId: 0xFF00) with { AutoResetOnFailure = true };

        var act = async () => await PipelineExecutor.ExecuteAsync(client, new[] { step },
            firmware: null, progress: null, ct: default);

        // The original exception must still bubble so the UI shows WHY it failed —
        // auto-reset is a safety net, NOT an error handler that swallows root cause.
        (await act.Invoking(a => a()).Should().ThrowAsync<UdsException>()).WithMessage("ECU rejected Erase");
        client.Calls.Should().ContainSingle(c => c.Op == RecordingUdsClient.Op.EcuReset
            && (byte)c.Arg1! == 0x01, "AutoReset must fire EcuReset(0x01) on failure");
    }

    [Fact]
    public async Task Failure_Without_AutoReset_Does_Not_Trigger_EcuReset()
    {
        var client = new RecordingUdsClient();
        client.ThrowOnNext = new UdsException("boom");
        var step = Step(FlashStepKind.Erase, routineId: 0xFF00) with { AutoResetOnFailure = false };

        var act = async () => await PipelineExecutor.ExecuteAsync(client, new[] { step },
            firmware: null, progress: null, ct: default);

        await act.Should().ThrowAsync<UdsException>();
        client.Calls.Should().NotContain(c => c.Op == RecordingUdsClient.Op.EcuReset,
            "AutoResetOnFailure=false must NOT trigger the safety-net reset");
    }

    [Fact]
    public async Task Cancellation_Stops_And_Reports_Cancelled()
    {
        var client = new RecordingUdsClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled
        var step = Step(FlashStepKind.SessionControl);

        var act = async () => await PipelineExecutor.ExecuteAsync(client, new[] { step },
            firmware: null, progress: null, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Full_Default_Sequence_Dispatches_In_Order()
    {
        var client = new RecordingUdsClient { DownloadBlockLength = 16 };
        var firmware = FirmwareFileParser.Parse(new byte[32]);
        var steps = new[]
        {
            Step(FlashStepKind.SessionControl),
            Step(FlashStepKind.SecurityAccess) with { SecurityMode = SecurityAccessMode.Dll, SecurityLevel = 0x01 },
            Step(FlashStepKind.Erase, routineId: 0xFF00),
            Step(FlashStepKind.DownloadTransfer) with { MemoryAddress = 0x0800_0000 },
            Step(FlashStepKind.Verify, routineId: 0x0204),
            Step(FlashStepKind.EcuReset),
        };

        await PipelineExecutor.ExecuteAsync(client, steps, firmware, progress: null, ct: default);

        var opOrder = client.Calls.Select(c => c.Op.ToString()).ToArray();
        // Expected envelope: SessionControl, SecurityAccess(2arg), [Erase — see note],
        // RequestDownload, TransferData*, RequestTransferExit, [Verify — see note], EcuReset.
        opOrder.Should().StartWith(nameof(RecordingUdsClient.Op.SessionControl),
            "session control is always first on the programming path");
        // The download→exit handshake must be contiguous and precede EcuReset.
        var dlIdx = Array.IndexOf(opOrder, nameof(RecordingUdsClient.Op.RequestDownload));
        var exitIdx = Array.IndexOf(opOrder, nameof(RecordingUdsClient.Op.RequestTransferExit));
        var resetIdx = Array.IndexOf(opOrder, nameof(RecordingUdsClient.Op.EcuReset));
        dlIdx.Should().BeLessThan(exitIdx);
        exitIdx.Should().BeLessThan(resetIdx,
            "RequestTransferExit must close the transfer BEFORE EcuReset boots the new image");
    }

    [Fact]
    public async Task Null_Client_Throws()
    {
        var act = async () => await PipelineExecutor.ExecuteAsync(null!, Array.Empty<FlashStepSnapshot>(),
            firmware: null, progress: null, ct: default);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Null_Steps_Throws()
    {
        var act = async () => await PipelineExecutor.ExecuteAsync(new RecordingUdsClient(), null!,
            firmware: null, progress: null, ct: default);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
