using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Core.Tests.HIL.Fakes;

/// <summary>
/// Hand-rolled fake IUdsSession for unit testing UDS executors.
/// </summary>
internal sealed class FakeIUdsSession : IUdsSession
{
    private readonly IReadOnlyList<DtcInfo> _dtcs;
    private readonly Exception? _readException;
    private readonly Exception? _sendException;

    public bool ReadDtcCalled { get; private set; }
    public byte? LastStatusMask { get; private set; }
    public bool SendRequestCalled { get; private set; }
    public byte? LastServiceId { get; private set; }

    public FakeIUdsSession(IReadOnlyList<DtcInfo>? dtcs = null, Exception? readException = null, Exception? sendException = null)
    {
        _dtcs = dtcs ?? Array.Empty<DtcInfo>();
        _readException = readException;
        _sendException = sendException;
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
}
