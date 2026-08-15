using System.Text;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>流式 CAN 帧 → PEAK ASCII (.asc) 文件。BufferedStream 缓冲，Dispose 时 flush+close。
/// 首帧时间戳作为 offset 基准。Write 由 consumer 单线程调用；Dispose 与 Write 竞态由软关闭标志保护。</summary>
internal sealed class AscFrameSink : IHilFrameSink
{
    private readonly BufferedStream _buffered;
    private readonly StreamWriter _writer;
    private int _disposed;                       // Interlocked 标志
    private double? _timestampOffsetUs;

    public AscFrameSink(string path) : this(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { }

    internal AscFrameSink(Stream stream)
    {
        _buffered = new BufferedStream(stream);
        _writer = new StreamWriter(_buffered, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var sb = new StringBuilder();
        AscFileFormat.WriteHeader(sb);
        _writer.Write(sb.ToString());
    }

    public void Write(CanFrame frame)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _timestampOffsetUs ??= frame.Timestamp.TotalMicroseconds;
            var elapsedUs = frame.Timestamp.TotalMicroseconds - _timestampOffsetUs.Value;
            var sb = new StringBuilder();
            AscFileFormat.WriteFrameLine(sb, frame, elapsedUs);
            _writer.Write(sb.ToString());
        }
        catch (Exception)
        {
            // A7: IO 失败不传播（不杀 consumer loop）。MVP 静默丢弃，见 spec 待优化 #5。
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _writer.Flush();
            _buffered.Flush();
        }
        catch (Exception) { /* A7: flush 失败也不传播 */ }
        finally
        {
            // Dispose 亦可能触发对底层流的写入（flush 残留缓冲）；各自 try/catch，
            // 保证「写失败不传播」整体成立（A7/P12）。
            try { _writer.Dispose(); } catch (Exception) { /* A7 */ }
            try { _buffered.Dispose(); } catch (Exception) { /* A7 */ }
        }
    }
}
