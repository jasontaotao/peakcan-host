using System.Buffers.Binary;
using System.Text;

namespace PeakCan.Host.Core.Xcp.Record;

/// <summary>
/// S4-T2 自研最小 MDF 4.10 写入器（spec D1 T0 裁决：NuGet 无可用写入库，回退自研只写子集）。
/// <para>
/// 布局：未压缩 MDF 4.10，<b>每对象一个 DGBLOCK</b>（DG 链），每 DG = 一个 CG，
/// 记录布局 [time f64 master + value f64]。理由：XCP 样本按对象逐条异步交错到达，
/// 单 CG 多列需要跨对象行装配（重采样丢真）；DG-per-object 逐样本无损。
/// </para>
/// <para>
/// 流式方案（MDF 标准形态）：DG.data → <b>DLBLOCK 数据列表</b> → DT 块序列。
/// DT 块在文件尾追加（每块目标 16 MB，块长写完即定，无需预留/回写 DT 长度），
/// 元数据区与数据区物理分离不重叠；DL 槽位（1024 DT/通道，16 MB/块 ≈ 16 GB/通道容量）
/// 与 CG cycles 在 Finalize 时回写。
/// </para>
/// <para>
/// 崩溃语义：未 Finalize 关闭 = ID 保持 "UnFinMF "，已落盘 DT 块自带正确块长可被工具抢救；
/// DL count / cycles 尽力回写。
/// </para>
/// <para>线程模型：单写线程（sink 后台消费）；本类不做跨线程同步。</para>
/// </summary>
public sealed class Mdf4StreamWriter : IMdfRecordWriter
{
    private const int IdBlockSize = 64;
    private const int HdBlockSize = 104;
    private const int DgBlockSize = 64;
    private const int CgBlockSize = 104;
    private const int CnBlockSize = 160;
    private const int DtHeaderSize = 24;
    private const int DlSlots = 1024;            // 每 DL 的 DT 槽位数
    private const long DtTargetSize = 16 * 1024 * 1024; // 单 DT 目标 16 MB

    private readonly FileStream _stream;
    private readonly MdfChannelSpec[] _channels;
    private readonly long[] _dlOffsets;
    private readonly long[] _cgOffsets;
    private readonly long[] _payloadLens;
    private readonly byte[][] _buffers;
    private readonly int[] _bufferLens;
    private readonly long[] _counts;
    private readonly long[] _openDt;     // 当前未封口的 DT 块偏移（0 = 无）
    private readonly long[] _dtPayload;  // 当前 DT 已写载荷
    private readonly int[] _dlSlot;      // 当前 DL 槽位游标
    private long _total;
    private bool _finalized;
    private bool _disposed;

    private Mdf4StreamWriter(FileStream stream, IReadOnlyList<MdfChannelSpec> channels, DateTimeOffset startTimeUtc)
    {
        _stream = stream;
        _channels = [.. channels];
        var n = _channels.Length;
        _dlOffsets = new long[n];
        _cgOffsets = new long[n];
        _payloadLens = new long[n];
        _buffers = new byte[n][];
        _bufferLens = new int[n];
        _counts = new long[n];
        _openDt = new long[n];
        _dtPayload = new long[n];
        _dlSlot = new int[n];

        WriteIdBlock();
        WriteHeaderBlock(startTimeUtc);
        WriteDataGroups();
        _stream.Flush();
    }

    /// <summary>创建写入器并落全部元数据块（DL 槽位清零，DT 流式追加）。</summary>
    public static Mdf4StreamWriter Create(string path, IReadOnlyList<MdfChannelSpec> channels, DateTimeOffset startTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count == 0)
            throw new ArgumentException("至少一个通道", nameof(channels));
        if (channels.Count > 64)
            throw new ArgumentException("通道数超写子集上限 64", nameof(channels));

        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 64 * 1024);
        try
        {
            return new Mdf4StreamWriter(stream, channels, startTimeUtc);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>已写入记录总数（跨通道）。</summary>
    public long RecordCount => Interlocked.Read(ref _total);

    /// <summary>各通道记录条数（索引 = 构造时通道序）。</summary>
    public IReadOnlyList<long> RecordCounts => _counts;

    /// <summary>追加一条记录（单写线程调用；64 KB 微缓冲攒批，满批落盘为/追加进 DT）。</summary>
    public async Task WriteRecordAsync(int channelIndex, double timeSeconds, double value, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)channelIndex, (uint)_channels.Length);

        var buf = _buffers[channelIndex] ??= new byte[64 * 1024];
        if (_bufferLens[channelIndex] + 16 > buf.Length)
            await FlushChannelAsync(channelIndex, ct).ConfigureAwait(false);

        var off = _bufferLens[channelIndex];
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(off, 8), timeSeconds);
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(off + 8, 8), value);
        _bufferLens[channelIndex] = off + 16;
        _counts[channelIndex]++;
        Interlocked.Increment(ref _total);
    }

    /// <summary>收尾：缓冲落盘 → 封口未满 DT → 回写 DL count / CG cycles → 翻转 ID 终态。幂等。</summary>
    public async Task FinalizeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finalized)
            return;
        _finalized = true;

        await FlushAllAsync(ct).ConfigureAwait(false);
        for (var i = 0; i < _channels.Length; i++)
        {
            await CloseOpenDtAsync(i, ct).ConfigureAwait(false);
            PatchDlCount(i);
            PatchCycles(i);
        }

        _stream.Seek(0, SeekOrigin.Begin);
        await _stream.WriteAsync("MDF     "u8.ToArray(), ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>崩溃/异常路径：缓冲落盘 + 封口 DT + 尽力回写，ID 保持 UnFinMF。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (!_finalized)
            {
                await FlushAllAsync(CancellationToken.None).ConfigureAwait(false);
                for (var i = 0; i < _channels.Length; i++)
                {
                    await CloseOpenDtAsync(i, CancellationToken.None).ConfigureAwait(false);
                    PatchDlCount(i);
                    PatchCycles(i);
                }
            }
        }
        catch
        {
            // 崩溃路径尽力而为：回写失败也必须关流，不遮蔽原始故障。
        }
        finally
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---------------- 元数据写入（构造期一次） ----------------

    private void WriteIdBlock()
    {
        var id = new byte[IdBlockSize];
        Encoding.ASCII.GetBytes("UnFinMF ", 0, 8, id, 0);
        Encoding.ASCII.GetBytes("4.10    ", 0, 8, id, 8);
        Encoding.ASCII.GetBytes("PKHN-HST", 0, 8, id, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(id.AsSpan(0x1C, 2), 410);
        _stream.Write(id);
    }

    private void WriteHeaderBlock(DateTimeOffset start)
    {
        var firstDg = ComputeFirstDataGroupOffset();
        var hd = new byte[HdBlockSize];
        WriteBlockHead(hd, "##HD", HdBlockSize, 6);
        // links @+24: dg, fh, ch, at, ev, cm
        BinaryPrimitives.WriteUInt64LittleEndian(hd.AsSpan(24, 8), (ulong)firstDg);
        // 数据 @+72：start_time_ns / tz / dst / time_flags=2（UTC，对齐 golden）
        var ns = (start - DateTimeOffset.UnixEpoch).Ticks * 100;
        BinaryPrimitives.WriteInt64LittleEndian(hd.AsSpan(72, 8), ns);
        BinaryPrimitives.WriteInt16LittleEndian(hd.AsSpan(80, 2), (short)(start.Offset.TotalMinutes + 0.5));
        hd[84] = 2;
        _stream.Write(hd);
    }

    private void WriteDataGroups()
    {
        var n = _channels.Length;
        var dgOffsets = new long[n];
        var pos = ComputeFirstDataGroupOffset();
        for (var i = 0; i < n; i++)
        {
            dgOffsets[i] = pos;
            pos += DgBlockSize + CgBlockSize;
            pos += TxBlockSize("time") + TxBlockSize("s");
            pos += TxBlockSize(_channels[i].Name);
            if (!string.IsNullOrEmpty(_channels[i].Unit))
                pos += TxBlockSize(_channels[i].Unit!);
            pos += CnBlockSize + CnBlockSize + DlBlockSize;
        }

        for (var i = 0; i < n; i++)
        {
            var ch = _channels[i];
            var dgOff = dgOffsets[i];
            var cgOff = dgOff + DgBlockSize;
            var cnTimeOff = cgOff + CgBlockSize
                            + TxBlockSize("time") + TxBlockSize("s")
                            + TxBlockSize(ch.Name)
                            + (string.IsNullOrEmpty(ch.Unit) ? 0 : TxBlockSize(ch.Unit!));
            var dlOff = cnTimeOff + CnBlockSize + CnBlockSize;
            _cgOffsets[i] = cgOff;
            _dlOffsets[i] = dlOff;

            // DG：next / cg / data(DL) / comment=0；rec_id_size=0。
            var d = new byte[DgBlockSize];
            WriteBlockHead(d, "##DG", DgBlockSize, 4);
            BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(24, 8), i + 1 < n ? (ulong)dgOffsets[i + 1] : 0UL);
            BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(32, 8), (ulong)cgOff);
            BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(40, 8), (ulong)dlOff);
            _stream.Write(d);

            // CG：first_ch=time CN；rec_id=1, samples_byte_nr=16, inval=0。
            var cg = new byte[CgBlockSize];
            WriteBlockHead(cg, "##CG", CgBlockSize, 6);
            BinaryPrimitives.WriteUInt64LittleEndian(cg.AsSpan(32, 8), (ulong)cnTimeOff);
            BinaryPrimitives.WriteUInt32LittleEndian(cg.AsSpan(72, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(cg.AsSpan(96, 4), 16);
            _stream.Write(cg);

            var txNameTime = WriteTx("time");
            var txUnitTime = WriteTx("s");
            var txName = WriteTx(ch.Name);
            var txUnit = string.IsNullOrEmpty(ch.Unit) ? 0L : WriteTx(ch.Unit!);

            WriteChannelBlock(nextCn: cnTimeOff + CnBlockSize, name: txNameTime, unit: txUnitTime,
                channelType: 2, syncType: 1, byteOffset: 0);
            WriteChannelBlock(nextCn: 0, name: txName, unit: txUnit,
                channelType: 0, syncType: 0, byteOffset: 8);

            WriteDataList();
        }
    }

    private void WriteChannelBlock(long nextCn, long name, long unit,
        byte channelType, byte syncType, int byteOffset)
    {
        var b = new byte[CnBlockSize];
        WriteBlockHead(b, "##CN", CnBlockSize, 8);
        // links @24: next, component, name, source, conversion, data, unit, comment
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24, 8), (ulong)nextCn);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(40, 8), (ulong)name);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(64, 8), (ulong)unit);
        // 数据 @88: type/sync/dtype/bitoff(1B), byteoff, bitcnt, flags, invalbit, prec, res, att, 6×double
        b[88] = channelType;
        b[89] = syncType;
        b[90] = 4; // REAL_LE（golden 参照）
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(92, 4), (uint)byteOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(96, 4), 64);
        b[104] = 0xFF; // precision 未指定
        _stream.Write(b);
    }

    /// <summary>DL：links = dl_next(0) + DlSlots 个 DT 槽位（清零）；data.count=0（Finalize 回写）。</summary>
    private void WriteDataList()
    {
        var blockLen = DlBlockSize;
        var dl = new byte[blockLen];
        WriteBlockHead(dl, "##DL", blockLen, 1 + DlSlots);
        _stream.Write(dl);
    }

    // DL 数据区 = flags(4) + count(4) + offsets 数组（非等长列表规范要求，asammdf 口径验证）。
    private int DlBlockSize => 24 + (1 + DlSlots) * 8 + 8 + DlSlots * 8;

    private long WriteTx(string text)
    {
        var off = _stream.Position;
        var payload = Encoding.ASCII.GetByteCount(text) + 1;
        var blockLen = 24 + ((payload + 7) & ~7);
        var head = new byte[24];
        WriteBlockHead(head, "##TX", blockLen, 0);
        _stream.Write(head);
        _stream.Write(Encoding.ASCII.GetBytes(text));
        _stream.WriteByte(0);
        for (var pad = payload; (pad & 7) != 0; pad++)
            _stream.WriteByte(0);
        return off;
    }

    private static int TxBlockSize(string text)
    {
        var payload = Encoding.ASCII.GetByteCount(text) + 1;
        return 24 + ((payload + 7) & ~7);
    }

    private static void WriteBlockHead(byte[] b, string magic, long blockLen, int linksNr)
    {
        Encoding.ASCII.GetBytes(magic, 0, 4, b, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8, 8), (ulong)blockLen);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16, 2), (ushort)linksNr);
    }

    private long ComputeFirstDataGroupOffset() => IdBlockSize + HdBlockSize; // 0xA8

    // ---------------- 记录追加 / DT 生命周期 ----------------

    private async Task FlushChannelAsync(int i, CancellationToken ct)
    {
        if (_bufferLens[i] == 0)
            return;

        if (_openDt[i] == 0)
            await OpenDtBlockAsync(i, ct).ConfigureAwait(false);

        _stream.Seek(_openDt[i] + DtHeaderSize + _dtPayload[i], SeekOrigin.Begin);
        await _stream.WriteAsync(_buffers[i].AsMemory(0, _bufferLens[i]), ct).ConfigureAwait(false);
        _dtPayload[i] += _bufferLens[i];
        _payloadLens[i] += _bufferLens[i];
        _bufferLens[i] = 0;

        if (_dtPayload[i] >= DtTargetSize)
            await CloseOpenDtAsync(i, ct).ConfigureAwait(false);
    }

    /// <summary>开新 DT 块：文件尾写占位头（长度随写随回写，封口时定稿），登记进 DL 槽位。</summary>
    private async Task OpenDtBlockAsync(int i, CancellationToken ct)
    {
        if (_dlSlot[i] >= DlSlots)
            throw new InvalidOperationException(
                $"DL 槽位耗尽（{DlSlots} × {DtTargetSize / 1024 / 1024} MB/通道）；当前写子集容量上限，需扩 DL 链");
        var slotAddr = _dlOffsets[i] + 24 + 8 + 8 * _dlSlot[i];
        var dtOff = _stream.Length;
        var head = new byte[DtHeaderSize];
        WriteBlockHead(head, "##DT", DtHeaderSize, 0);
        _stream.Seek(dtOff, SeekOrigin.Begin);
        await _stream.WriteAsync(head.AsMemory(0, DtHeaderSize), ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);

        _stream.Seek(slotAddr, SeekOrigin.Begin);
        var link = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(link, (ulong)dtOff);
        await _stream.WriteAsync(link, ct).ConfigureAwait(false);

        _openDt[i] = dtOff;
        _dtPayload[i] = 0;
        _dlSlot[i]++;
    }

    private async Task CloseOpenDtAsync(int i, CancellationToken ct)
    {
        if (_openDt[i] == 0)
            return;
        _stream.Seek(_openDt[i] + 8, SeekOrigin.Begin);
        var len = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(len, (ulong)(DtHeaderSize + _dtPayload[i]));
        await _stream.WriteAsync(len, ct).ConfigureAwait(false);
        _openDt[i] = 0;
        _dtPayload[i] = 0;
        _stream.Seek(0, SeekOrigin.End);
    }

    private void PatchDlCount(int i)
    {
        // DL data 区：flags(4) reserved(4) count(4) reserved(4) @ 24+(1+DlSlots)*8。
        _stream.Seek(_dlOffsets[i] + 24 + (1 + DlSlots) * 8 + 4, SeekOrigin.Begin); // count 在 DL data+4
        var c = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(c, (uint)_dlSlot[i]);
        _stream.Write(c);
    }

    private void PatchCycles(int i)
    {
        // CG 数据区：rec_id(4)+pad(4)+cycles(8)@+80（golden 参照）。
        _stream.Seek(_cgOffsets[i] + 80, SeekOrigin.Begin);
        var c = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(c, (ulong)_counts[i]);
        _stream.Write(c);
    }

    private async Task FlushAllAsync(CancellationToken ct)
    {
        for (var i = 0; i < _channels.Length; i++)
            await FlushChannelAsync(i, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }
}



