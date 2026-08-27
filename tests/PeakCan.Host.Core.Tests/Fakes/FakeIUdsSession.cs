using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds;

namespace PeakCan.HIL.Core.Tests.HIL.Fakes;

/// <summary>
/// Hand-rolled fake IUdsSession for unit testing UDS executors.
/// DID 读/写支持（Task B 第一步 Q1）：接口扩容前先作为普通方法存在（RED），
/// IUdsSession 加方法后自动成为隐式实现（GREEN）。
/// </summary>
internal sealed class FakeIUdsSession : IUdsSession
{
    private readonly IReadOnlyList<DtcInfo> _dtcs;
    private readonly Exception? _readException;
    private readonly Exception? _sendException;
    private readonly byte[]? _readDidResponse;
    private readonly Exception? _readDidException;
    private readonly Exception? _writeDidException;

    public bool ReadDtcCalled { get; private set; }
    public byte? LastStatusMask { get; private set; }
    public bool SendRequestCalled { get; private set; }
    public byte? LastServiceId { get; private set; }

    // DID 读写调用追踪
    public bool ReadDidCalled { get; private set; }
    public ushort? LastReadDid { get; private set; }
    public bool WriteDidCalled { get; private set; }
    public ushort? LastWrittenDid { get; private set; }
    public byte[]? LastWrittenData { get; private set; }

    public FakeIUdsSession(
        IReadOnlyList<DtcInfo>? dtcs = null, Exception? readException = null, Exception? sendException = null,
        byte[]? readDidResponse = null, Exception? readDidException = null, Exception? writeDidException = null)
    {
        _dtcs = dtcs ?? Array.Empty<DtcInfo>();
        _readException = readException;
        _sendException = sendException;
        _readDidResponse = readDidResponse;
        _readDidException = readDidException;
        _writeDidException = writeDidException;
    }

    public Task<IReadOnlyList<DtcInfo>> ReadDtcInformation(byte statusMask, CancellationToken ct)
    {
        ReadDtcCalled = true;
        LastStatusMask = statusMask;
        if (_readException is not null) throw _readException;
        return Task.FromResult(_dtcs);
    }

    public Task SendRequestAsync(byte serviceId, byte[]? data, CancellationToken ct)
    {
        SendRequestCalled = true;
        LastServiceId = serviceId;
        if (_sendException is not null) throw _sendException;
        return Task.CompletedTask;
    }

    public Task<byte[]> ReadDataByIdentifierAsync(ushort did, CancellationToken ct)
    {
        ReadDidCalled = true;
        LastReadDid = did;
        if (_readDidException is not null) throw _readDidException;
        return Task.FromResult(_readDidResponse ?? Array.Empty<byte>());
    }

    public Task WriteDataByIdentifierAsync(ushort did, byte[] data, CancellationToken ct)
    {
        WriteDidCalled = true;
        LastWrittenDid = did;
        LastWrittenData = data;
        if (_writeDidException is not null) throw _writeDidException;
        return Task.CompletedTask;
    }

    // Task B 第二步（spec 2026-08-27 §Q1）新增接口方法——本 fake 的既有测试只覆盖
    // DID/DTC 路径，其余方法在需要时再补桩（当前无调用方，抛 NotSupported 防静默假通过）。
    public Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync(byte sessionType, CancellationToken ct)
        => throw new NotSupportedException("FakeIUdsSession 未实现 DiagnosticSessionControlAsync");

    public Task ClearDiagnosticInformationAsync(uint groupOfDtc, CancellationToken ct)
        => throw new NotSupportedException("FakeIUdsSession 未实现 ClearDiagnosticInformationAsync");

    public Task<byte[]> RoutineControlAsync(byte routineControlType, ushort routineId, byte[]? data, CancellationToken ct)
        => throw new NotSupportedException("FakeIUdsSession 未实现 RoutineControlAsync");

    public Task<byte[]> RequestSeedAsync(byte level, CancellationToken ct)
        => throw new NotSupportedException("FakeIUdsSession 未实现 RequestSeedAsync");

    public Task<byte[]> SecurityAccessAsync(byte level, CancellationToken ct)
        => throw new NotSupportedException("FakeIUdsSession 未实现 SecurityAccessAsync");

    public Task<byte> EcuResetAsync(byte resetType, CancellationToken ct)
        => throw new NotSupportedException("FakeIUdsSession 未实现 EcuResetAsync");

    public Task TesterPresentAsync(bool suppressPosResponse, CancellationToken ct)
        => throw new NotSupportedException("FakeIUdsSession 未实现 TesterPresentAsync");

    public Task<byte[]> IOControlAsync(ushort did, byte controlType, byte[]? data, byte controlEnableMask = 0xFF, CancellationToken ct = default)
        => throw new NotSupportedException("FakeIUdsSession 未实现 IOControlAsync");
}
