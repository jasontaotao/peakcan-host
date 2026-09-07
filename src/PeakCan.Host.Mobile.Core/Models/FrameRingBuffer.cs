namespace PeakCan.Host.Mobile.Core.Models;

/// <summary>
/// 环形缓冲：容量固定，写满后覆盖最旧的一帧。表格数据源用它保持最近 N 帧。
/// Snapshot 返回当前内容的按插入序只读拷贝（最旧在前）。
/// 非线程安全——调用方（VM drain）保证单线程访问。
/// </summary>
public sealed class FrameRingBuffer
{
    private readonly FrameRow[] _buffer;
    private int _head;   // 下一次写入位置
    private int _count;

    public FrameRingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new FrameRow[capacity];
    }

    public int Capacity => _buffer.Length;
    public int Count => _count;

    public void Add(FrameRow row)
    {
        _buffer[_head] = row;
        _head = (_head + 1) % _buffer.Length;
        if (_count < _buffer.Length) _count++;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
        Array.Clear(_buffer);
    }

    /// <summary>当前内容的按插入序只读拷贝（最旧在前）。</summary>
    public IReadOnlyList<FrameRow> Snapshot()
    {
        var result = new FrameRow[_count];
        int start = _count == _buffer.Length ? _head : 0;
        for (int i = 0; i < _count; i++)
            result[i] = _buffer[(start + i) % _buffer.Length];
        return result;
    }
}
