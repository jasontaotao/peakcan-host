using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PeakCan.Host.Core.Xcp.Record;

/// <summary>单条样本（原始值 + 失效位）。</summary>
public readonly record struct Mdf4Sample(double Time, double Value, bool Invalid);

/// <summary>一个通道的读回数据（DG-per-object 布局，每通道一组样本）。</summary>
public sealed record Mdf4ChannelData(string Name, string Unit, IReadOnlyList<Mdf4Sample> Samples);

/// <summary>归因事件条目（S4 事件组：时间 + kind/cause/detail/receive_kind + 预期时长）。</summary>
public sealed record Mdf4GapEvent(
    double Time, string Kind, string Cause, string Detail, string ReceiveKind, double ExpectedMaxSeconds);

/// <summary>附件读回（S4 D2 自包含快照）。</summary>
public sealed record Mdf4Attachment(string MimeType, string Comment, byte[] Data);

/// <summary>MDF4 文件读回结果。</summary>
public sealed record Mdf4FileData(
    DateTimeOffset StartTimeUtc,
    bool IsFinalized,
    IReadOnlyList<Mdf4ChannelData> Channels,
    IReadOnlyList<Mdf4GapEvent> GapEvents,
    IReadOnlyList<Mdf4Attachment> Attachments);

/// <summary>
/// S6-T1 自研最小 MDF 4.10 读取器（spec D1 定案）：只覆盖 <see cref="Mdf4StreamWriter"/> 写面
/// （##HD/DG/CG/CN/DL/DT/SD/AT/TX 布局与 writer 一致），未知块/不支持布局显式报错不静默。
/// <para>
/// 崩溃抢救语义：未 Finalize 文件（ID = "UnFinMF "）仍可读——DT 槽位按非零链接逐块解，
/// CG cycles / DL count 等未回写字段不参与校验。
/// </para>
/// </summary>
public static class Mdf4StreamReader
{
    private const int HdBlockOffset = 64;   // ID(64) 后即 HD
    private const int FirstDataGroupOffset = HdBlockOffset + 104; // 168，与 writer ComputeFirstDataGroupOffset 一致

    public static Mdf4FileData Read(string path)
        => Read(File.ReadAllBytes(path));

    public static Mdf4FileData Read(byte[] b)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (b.Length < FirstDataGroupOffset)
            throw new FormatException("不支持的 MDF4 文件：长度不足");

        var magic = Encoding.ASCII.GetString(b, 0, 8);
        var isFinalized = magic switch
        {
            "MDF     " => true,
            "UnFinMF " => false,
            _ => throw new FormatException($"不支持的文件标识：{magic.TrimEnd()}"),
        };
        var version = Encoding.ASCII.GetString(b, 8, 8);
        if (!version.StartsWith("4.1", StringComparison.Ordinal))
            throw new FormatException($"不支持的 MDF 版本：{version.TrimEnd()}");

        EnsureBlock(b, HdBlockOffset, "##HD");
        var firstDg = (long)U64(b, HdBlockOffset + 24);
        var hdAttachment = (long)U64(b, HdBlockOffset + 48);   // hd links: dg/fh/ch/at/ev/cm
        var startTimeNs = I64(b, HdBlockOffset + 72);
        var startTimeUtc = DateTimeOffset.UnixEpoch.AddTicks(startTimeNs / 100);

        var channels = new List<Mdf4ChannelData>();
        var gapEvents = new List<Mdf4GapEvent>();

        var dg = firstDg;
        while (dg != 0)
        {
            var dgOff = checked((int)dg);
            EnsureBlock(b, dgOff, "##DG");
            var cg = (long)U64(b, dgOff + 32);
            var dl = (long)U64(b, dgOff + 40);
            var group = ParseChannelGroup(b, cg);
            var records = ReadRawRecords(b, dl, group.Stride, isFinalized, group.CycleCount);

            if (group.HasVlsdChannel)
                gapEvents.AddRange(ParseGapEvents(b, group, records));
            else
                channels.Add(ParseChannelData(group, records));

            dg = (long)U64(b, dgOff + 24); // dg.next
        }

        var attachments = ParseAttachments(b, hdAttachment);
        return new Mdf4FileData(startTimeUtc, isFinalized, channels, gapEvents, attachments);
    }

    // ---------------- 块基元 ----------------

    private static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o, 8));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));
    private static long I64(byte[] b, int o) => BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(o, 8));

    private static void EnsureBlock(byte[] b, int offset, string expected)
    {
        if (offset + 4 > b.Length || Encoding.ASCII.GetString(b, offset, 4) != expected)
        {
            var found = offset + 4 <= b.Length ? Encoding.ASCII.GetString(b, offset, 4) : "<eof>";
            throw new FormatException($"MDF4 块校验失败：期望 {expected}，实际 {found}（未知块或不支持布局）");
        }
    }

    private static string ReadTx(byte[] b, long link)
    {
        if (link == 0)
            return string.Empty;
        var off = checked((int)link);
        EnsureBlock(b, off, "##TX");
        var blockLen = checked((int)U64(b, off + 8));
        var payload = b.AsSpan(off + 24, blockLen - 24);
        var nul = payload.IndexOf((byte)0);
        if (nul < 0)
            nul = payload.Length;
        return Encoding.ASCII.GetString(payload[..nul]);
    }

    /// <summary>SD 块内取一条 [u32 len][utf8] 字符串条目（writer AppendSd 口径）。</summary>
    private static string ReadSdEntry(byte[] b, long sdLink, uint entryOffset)
    {
        if (sdLink == 0)
            return string.Empty;
        var off = checked((int)sdLink);
        EnsureBlock(b, off, "##SD");
        var blockLen = checked((int)U64(b, off + 8));
        var payload = b.AsSpan(off + 24, blockLen - 24);
        var start = checked((int)entryOffset);
        if (start + 4 > payload.Length)
            throw new FormatException("SD 条目偏移越界");
        var len = BinaryPrimitives.ReadInt32LittleEndian(payload[start..]);
        if (start + 4 + len > payload.Length)
            throw new FormatException("SD 条目长度越界");
        return Encoding.UTF8.GetString(payload[(start + 4)..(start + 4 + len)]);
    }

    // ---------------- CG/CN 组面 ----------------

    private sealed record ChannelGroupInfo(
        int Stride, long CycleCount, bool HasVlsdChannel,
        CnInfo Master, CnInfo? Value, IReadOnlyList<CnInfo> All);

    private sealed record CnInfo(
        byte ChannelType, byte SyncType, byte DataType,
        uint ByteOffset, uint BitCount, uint Flags, uint InvalBit,
        long DataLink, string Name, string Unit);

    private static ChannelGroupInfo ParseChannelGroup(byte[] b, long cg)
    {
        var cgOff = checked((int)cg);
        EnsureBlock(b, cgOff, "##CG");
        var firstCn = (long)U64(b, cgOff + 32);
        var samplesNr = U32(b, cgOff + 96);
        var invalNr = U32(b, cgOff + 100);
        var cycleCount = (long)U64(b, cgOff + 80);

        var cns = new List<CnInfo>();
        var cn = firstCn;
        while (cn != 0)
        {
            var off = checked((int)cn);
            EnsureBlock(b, off, "##CN");
            cns.Add(new CnInfo(
                b[off + 88], b[off + 89], b[off + 90],
                U32(b, off + 92), U32(b, off + 96), U32(b, off + 100), U32(b, off + 104),
                (long)U64(b, off + 64),
                ReadTx(b, (long)U64(b, off + 40)),
                ReadTx(b, (long)U64(b, off + 72))));
            cn = (long)U64(b, off + 24);
        }

        var master = cns.FirstOrDefault(c => c.SyncType == 1)
            ?? throw new FormatException("CG 缺少 master 时间通道（sync=1）");
        if (master.ByteOffset != 0 || master.DataType != 4 || master.BitCount != 64)
            throw new FormatException("不支持的 master 通道布局（期望 byteOffset=0 的 FLOAT64）");

        var vlsdCount = cns.Count(c => c.ChannelType == 1);
        CnInfo? value = null;
        if (vlsdCount == 0)
        {
            value = cns.FirstOrDefault(c => c.ChannelType == 0 && c.SyncType == 0)
                ?? throw new FormatException("样本 CG 缺少值通道");
            if (value.DataType != 4 || value.BitCount != 64)
                throw new FormatException($"不支持的值通道布局（dataType={value.DataType}, bitCount={value.BitCount}）");
        }
        else if (cns.Count(c => c.ChannelType == 0 && c.SyncType == 0) > 1)
        {
            throw new FormatException("不支持的多值通道 CG");
        }

        var stride = checked((int)(samplesNr + invalNr));
        if (stride <= 0)
            throw new FormatException("CG 记录步长非法");
        return new ChannelGroupInfo(stride, cycleCount, vlsdCount > 0, master, value, cns);
    }

    // ---------------- DT/DL 数据面 ----------------

    private static List<byte[]> ReadRawRecords(byte[] b, long dl, int stride, bool isFinalized, long cycleCount)
    {
        var dlOff = checked((int)dl);
        EnsureBlock(b, dlOff, "##DL");
        var linksNr = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(dlOff + 16, 2));
        if (linksNr < 1)
            throw new FormatException("DL 链接数为 0");

        var records = new List<byte[]>();
        for (var slot = 1; slot < linksNr; slot++) // link[0] = dl.next；slot 1..n-1 = DT 槽位
        {
            var dt = U64(b, dlOff + 24 + 8 * slot);
            if (dt == 0)
                continue;
            var dtOff = checked((int)dt);
            EnsureBlock(b, dtOff, "##DT");
            var blockLen = checked((int)U64(b, dtOff + 8));
            var payloadLen = blockLen - 24;
            if (payloadLen % stride != 0)
                throw new FormatException($"DT 载荷 {payloadLen}B 不是记录步长 {stride}B 的整数倍");
            for (var rec = 0; rec < payloadLen; rec += stride)
                records.Add(b[(dtOff + 24 + rec)..(dtOff + 24 + rec + stride)]);
        }

        // Finalize 文件 cycles 已回写，数量必须一致；抢救路径（UnFinMF）cycles 不可信，跳过。
        if (isFinalized && records.Count != cycleCount)
            throw new FormatException($"记录数不一致：CG cycles={cycleCount}，实际读回 {records.Count}");
        return records;
    }

    // ---------------- 通道 / 事件 / 附件解析 ----------------

    private static Mdf4ChannelData ParseChannelData(ChannelGroupInfo group, List<byte[]> records)
    {
        var value = group.Value ?? throw new FormatException("样本 CG 缺少值通道");
        var invalPresent = (value.Flags & 0x2) != 0;
        var invalByteIndex = group.Stride - 1; // writer 固定布局：失效字节紧跟样本数据（16B 后）
        var samples = new List<Mdf4Sample>(records.Count);
        foreach (var rec in records)
        {
            var time = BitConverter.ToDouble(rec, (int)group.Master.ByteOffset);
            var raw = BitConverter.ToDouble(rec, (int)value.ByteOffset);
            var invalid = invalPresent && (rec[invalByteIndex] & (1 << (int)(value.InvalBit & 7))) != 0;
            samples.Add(new Mdf4Sample(time, raw, invalid));
        }
        return new Mdf4ChannelData(value.Name, value.Unit, samples);
    }

    private static List<Mdf4GapEvent> ParseGapEvents(byte[] b, ChannelGroupInfo group, List<byte[]> records)
    {
        var result = new List<Mdf4GapEvent>(records.Count);
        foreach (var rec in records)
        {
            var time = BitConverter.ToDouble(rec, (int)group.Master.ByteOffset);
            double expected = 0;
            string kind = "", cause = "", detail = "", receive = "";
            foreach (var cn in group.All)
            {
                if (cn.ChannelType == 1) // VLSD：记录存 SD 条目偏移（u32）
                {
                    var text = ReadSdEntry(b, cn.DataLink, BitConverter.ToUInt32(rec, (int)cn.ByteOffset));
                    switch (cn.ByteOffset)
                    {
                        case 16: kind = text; break;
                        case 20: cause = text; break;
                        case 24: detail = text; break;
                        case 28: receive = text; break;
                    }
                }
                else if (cn.SyncType == 0 && cn.ChannelType == 0 && cn.ByteOffset == 8)
                {
                    expected = BitConverter.ToDouble(rec, 8);
                }
            }
            result.Add(new Mdf4GapEvent(time, kind, cause, detail, receive, expected));
        }
        return result;
    }

    private static List<Mdf4Attachment> ParseAttachments(byte[] b, long atLink)
    {
        var result = new List<Mdf4Attachment>();
        var at = atLink;
        while (at != 0)
        {
            var off = checked((int)at);
            EnsureBlock(b, off, "##AT");
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(off + 56, 2));
            var zipType = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(off + 60, 2));
            if (zipType != 0)
                throw new FormatException($"不支持的附件压缩类型 {zipType}");
            var originalSize = checked((int)U64(b, off + 80));
            var embeddedSize = checked((int)U64(b, off + 88));
            var data = b[(off + 96)..(off + 96 + embeddedSize)];
            if ((flags & 0x4) != 0)
            {
#pragma warning disable CA5351
                // MDF 4.1 ATBLOCK 规范要求 MD5 作附件完整性校验（非安全用途）。
                if (!MD5.HashData(data).AsSpan().SequenceEqual(b.AsSpan(off + 64, 16)))
                    throw new FormatException("附件 MD5 校验失败");
#pragma warning restore CA5351
            }
            if (data.Length != originalSize)
                throw new FormatException($"附件尺寸不一致：original={originalSize}, embedded={data.Length}");
            result.Add(new Mdf4Attachment(
                ReadTx(b, (long)U64(b, off + 40)),
                ReadTx(b, (long)U64(b, off + 48)),
                data));
            at = (long)U64(b, off + 24);
        }
        return result;
    }
}
