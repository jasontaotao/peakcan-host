using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using System.Text;

string[] dlls = new[] {
    @"D:\claude_proj2\peakcan-host\src\PeakCan.Host.Core\bin\Debug\net10.0\PeakCan.Host.Core.dll",
    @"D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\bin\Debug\net10.0-windows\PeakCan.Host.Infrastructure.dll",
    @"D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\bin\Debug\net10.0-windows\PeakCan.Host.dll",
};

foreach (var path in dlls)
{
    Console.WriteLine($"\n===== {Path.GetFileName(path)} =====");
    var fi = new FileInfo(path);
    Console.WriteLine($"Path:     {path}");
    Console.WriteLine($"Size:     {fi.Length} bytes");
    Console.WriteLine($"Modified: {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss.fff}");

    byte[] data = File.ReadAllBytes(path);

    // ==== User string heap: scan for SMOKE in UTF-16LE ====
    byte[] nb = Encoding.Unicode.GetBytes("SMOKE");
    int smokeIdx = 0;
    for (int i = 0; i <= data.Length - nb.Length; i++)
    {
        bool ok = true;
        for (int j = 0; j < nb.Length; j++) if (data[i + j] != nb[j]) { ok = false; break; }
        if (!ok) continue;
        smokeIdx++;
        // Decode the enclosing #US entry
        string full = TryReadUsEntry(data, i) ?? "<decode-failed>";
        // Strip any trailing 0xFFFD / replacement
        Console.WriteLine($"  SMOKE HIT #{smokeIdx} fileOff=0x{i:X}: {full}");
    }
    Console.WriteLine($"  -- SMOKE total: {smokeIdx}");

    // ==== Custom attributes + version info via PEReader ====
    using var fs = File.OpenRead(path);
    using var pe = new PEReader(fs);
    var md = pe.GetMetadataReader();
    var ad = md.GetAssemblyDefinition();
    Console.WriteLine($"AssemblyDef.Version (AssemblyVersion): {ad.Version}");

    Console.WriteLine("--- Custom attributes on AssemblyDefinition ---");
    foreach (var ah in md.GetCustomAttributes(EntityHandle.AssemblyDefinition))
    {
        var attr = md.GetCustomAttribute(ah);
        var ctor = attr.Constructor;
        string typeName = "?";
        string ns = "";
        try
        {
            if (ctor.Kind == HandleKind.MemberReference)
            {
                var mr = md.GetMemberReference((MemberReferenceHandle)ctor);
                if (mr.Parent.Kind == HandleKind.TypeReference)
                {
                    var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                    typeName = md.GetString(tr.Name);
                    ns = md.GetString(tr.Namespace);
                }
            }
            else if (ctor.Kind == HandleKind.MethodDefinition)
            {
                var mdh = md.GetMethodDefinition((MethodDefinitionHandle)ctor);
                var declType = mdh.GetDeclaringType();
                var td = md.GetTypeDefinition((TypeDefinitionHandle)declType);
                typeName = md.GetString(td.Name);
                ns = md.GetString(td.Namespace);
            }
        }
        catch { }
        var fullName = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";

        // The first fixed arg of these version attributes is a string (a #US token followed by UTF-16LE bytes).
        // Read the blob ourselves: skip prolog (2 bytes 0x00 0x01) + SerString header (compressed length) + UTF-16LE bytes.
        string val = "<unreadable>";
        try
        {
            var blobReader = md.GetBlobReader(attr.Value);
            // Skip the prolog (2 bytes 0x00 0x01)
            blobReader.ReadByte();
            blobReader.ReadByte();
            // Read the SerString compressed length
            int b0 = blobReader.ReadByte();
            int strLen;
            if ((b0 & 0x80) == 0) { strLen = b0; }
            else if ((b0 & 0xC0) == 0x80) { int b1 = blobReader.ReadByte(); strLen = ((b0 & 0x3F) << 8) | b1; }
            else { int b1 = blobReader.ReadByte(); int b2 = blobReader.ReadByte(); int b3 = blobReader.ReadByte(); strLen = ((b0 & 0x1F) << 24) | (b1 << 16) | (b2 << 8) | b3; }
            var strBytes = blobReader.ReadBytes(strLen);
            val = Encoding.Unicode.GetString(strBytes);
        }
        catch (Exception ex) { val = $"<decode err: {ex.GetType().Name}>"; }

        Console.WriteLine($"  {fullName}: {val}");
    }

    // PE header timestamp (linker timestamp)
    try
    {
        var coff = pe.PEHeaders.CoffHeader;
        uint tds = (uint)coff.TimeDateStamp;
        Console.WriteLine($"PE COFF TimeDateStamp: {tds}  ({DateTimeOffset.FromUnixTimeSeconds(tds):yyyy-MM-dd HH:mm:ss} UTC)");
    }
    catch (Exception ex) { Console.WriteLine($"PE timestamp read failed: {ex.Message}"); }
}

// Helper: scan backward from `hitOff` to find a #US compressed-length prefix
// whose entry contains `hitOff` and decode the UTF-16LE string.
static string? TryReadUsEntry(byte[] data, int hitOff)
{
    int backLim = Math.Max(0, hitOff - 4096);
    for (int k = hitOff; k >= backLim; k--)
    {
        // try interpretations of k as the START of a length prefix
        for (int prefixLen = 1; prefixLen <= 4; prefixLen++)
        {
            if (prefixLen == 3) continue; // reserved
            int p = k - (prefixLen - 1);
            if (p < backLim) continue;
            int adv; int declaredLen;
            if (prefixLen == 1)
            {
                if ((data[p] & 0x80) != 0) continue;
                declaredLen = data[p]; adv = 1;
            }
            else if (prefixLen == 2)
            {
                byte a = data[p];
                if ((a & 0xC0) != 0x80) continue;
                int c = data[p + 1];
                declaredLen = ((a & 0x3F) << 8) | c;
                adv = 2;
            }
            else // 4
            {
                byte a = data[p];
                if ((a & 0xE0) != 0xC0) continue;
                int b1 = data[p + 1];
                int b2 = data[p + 2];
                int b3 = data[p + 3];
                declaredLen = ((a & 0x1F) << 24) | (b1 << 16) | (b2 << 8) | b3;
                adv = 4;
            }
            if (declaredLen <= 0 || declaredLen > 65535) continue;
            int dataStart = p + adv;
            int dataEnd = dataStart + declaredLen;
            if (dataEnd > data.Length) continue;
            if (hitOff < dataStart || hitOff >= dataEnd) continue;
            // validity check: must be UTF-16LE chars in printable range or common punctuation
            int charCount = declaredLen / 2;
            bool ok = true;
            for (int c = 0; c < charCount; c++)
            {
                int ch = data[dataStart + c * 2] | (data[dataStart + c * 2 + 1] << 8);
                if (ch == 0) { ok = false; break; }
            }
            if (!ok) continue;
            var bytes = new byte[declaredLen];
            Buffer.BlockCopy(data, dataStart, bytes, 0, declaredLen);
            return Encoding.Unicode.GetString(bytes);
        }
    }
    return null;
}
