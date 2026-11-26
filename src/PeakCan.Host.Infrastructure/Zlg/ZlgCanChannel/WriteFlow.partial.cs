using PeakCan.HIL.Core;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Zlg;

// 写流：ZCAN_Transmit / ZCAN_TransmitFD（通道句柄）。
public sealed partial class ZlgCanChannel
{
    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        if (!IsConnected)
            return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "Not connected"));

        var channelHandle = CurrentChannelHandle;
        if (channelHandle == IntPtr.Zero)
            return ValueTask.FromResult(
                Result<Unit>.Fail(ErrorCode.InvalidState, "Channel handle is not available"));

        // 按帧类型分派，而不是按通道模式硬拒：CAN FD 通道上仍可发送经典帧
        // （UDS/ISO-TP 与 DBC 里的经典报文都走这里）。让驱动返回真实状态，
        // 而不是在客户端把合法帧静默拒绝。与 PEAK 适配器行为一致。
        try
        {
            uint ret;
            if (frame.IsFd)
            {
                var message = ZlgCanFrameFormatter.EncodeFd(frame);
                ret = _api.TransmitFd(channelHandle, ref message, 1);
            }
            else
            {
                var message = ZlgCanFrameFormatter.EncodeClassic(frame);
                ret = _api.Transmit(channelHandle, ref message, 1);
            }

            if (ret == ZlgError.Success)
                return ValueTask.FromResult(Result<Unit>.Ok(default));

            var (code, msg) = ZlgErrorMapper.ToErrorCode(ret);
            return ValueTask.FromResult(Result<Unit>.Fail(code, msg));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.IoError, ex.Message));
        }
    }
}
