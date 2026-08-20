using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.Zlg;

// 写流：VCI_Transmit / VCI_TransmitFD。
public sealed partial class ZlgCanChannel
{
    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        if (!IsConnected)
            return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "Not connected"));

        try
        {
            if (frame.IsFd)
            {
                var fdMsg = ZlgCanFrameFormatter.EncodeFd(frame);
                var ret = ZlgNative.ZCAN_TransmitFD(_devType, _devIdx, _canIdx, ref fdMsg, 1);
                if (ret == ZlgError.Success)
                    return ValueTask.FromResult(Result<Unit>.Ok(default));
                var (code, msg) = ZlgErrorMapper.ToErrorCode(ret);
                return ValueTask.FromResult(Result<Unit>.Fail(code, msg));
            }
            else
            {
                var classicMsg = ZlgCanFrameFormatter.EncodeClassic(frame);
                var ret = ZlgNative.ZCAN_Transmit(_devType, _devIdx, _canIdx, ref classicMsg, 1);
                if (ret == ZlgError.Success)
                    return ValueTask.FromResult(Result<Unit>.Ok(default));
                var (code, msg) = ZlgErrorMapper.ToErrorCode(ret);
                return ValueTask.FromResult(Result<Unit>.Fail(code, msg));
            }
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.IoError, ex.Message));
        }
    }
}