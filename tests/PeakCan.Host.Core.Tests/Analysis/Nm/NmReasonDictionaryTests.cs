using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmReasonDictionaryTests
{
    private const string Minimal = """
    {
      "version": 1,
      "fields": [
        { "signal": "BMS_Wakeup_reasons", "can_id": "0x18FFFAF4", "extended": true }
      ]
    }
    """;

    [Fact]
    public void MinimalField_LoadsAsRawWithoutEntries()
    {
        var loaded = NmReasonDictionaryStore.Load(Minimal, "mem.nmreasons.json");
        var field = Assert.Single(loaded.Dictionary.Fields);
        Assert.Equal("BMS_Wakeup_reasons", field.Signal);
        Assert.Equal("0x18FFFAF4", field.CanIdHex);
        Assert.True(field.IsExtended);
        Assert.Equal(NmReasonKind.Raw, field.Kind);
        Assert.Null(field.Entries);
        Assert.Equal(64, loaded.Sha256.Length);
    }

    [Fact]
    public void MissingExtended_IsRejected()
    {
        var json = """
        { "version": 1, "fields": [ { "signal": "S", "can_id": "0x1" } ] }
        """;
        var ex = Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.Load(json, "mem.json"));
        Assert.Contains("extended", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownKind_IsRejected()
    {
        var json = """
        { "version": 1, "fields": [
          { "signal": "S", "can_id": "0x1", "extended": true, "kind": "bogus" } ] }
        """;
        Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.Load(json, "mem.json"));
    }

    [Fact]
    public void BitmaskEntryNotPowerOfTwo_IsRejected()
    {
        var json = """
        { "version": 1, "fields": [
          { "signal": "S", "can_id": "0x1", "extended": true,
            "kind": "bitmask", "entries": { "3": "two bits" } } ] }
        """;
        var ex = Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.Load(json, "mem.json"));
        Assert.Contains("2 的幂", ex.Message);
    }

    [Fact]
    public void NegativeEntry_IsRejected()
    {
        var json = """
        { "version": 1, "fields": [
          { "signal": "S", "can_id": "0x1", "extended": true,
            "entries": { "-1": "negative" } } ] }
        """;
        var ex = Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.Load(json, "mem.json"));
        Assert.Contains("负", ex.Message);
    }

    [Fact]
    public void DuplicateField_IsRejected()
    {
        var json = """
        { "version": 1, "fields": [
          { "signal": "S", "can_id": "0x1", "extended": true },
          { "signal": "S", "can_id": "0x1", "extended": true } ] }
        """;
        Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.Load(json, "mem.json"));
    }

    [Fact]
    public void BadJson_IsRejected()
    {
        Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.Load("{ not json", "mem.json"));
    }

    [Fact]
    public void RelativePath_ResolvesFromProfileDirectory()
    {
        var resolved = NmReasonDictionaryStore.ResolvePath(
            @"C:\projects\bms\matrix.nmprofile.json", "reasons.nmreasons.json");
        Assert.Equal(@"C:\projects\bms\reasons.nmreasons.json", resolved);
    }

    [Fact]
    public void AbsolutePath_IsKept()
    {
        var resolved = NmReasonDictionaryStore.ResolvePath(
            @"C:\projects\bms\matrix.nmprofile.json", @"D:\other\reasons.json");
        Assert.Equal(@"D:\other\reasons.json", resolved);
    }

    [Fact]
    public void MissingFile_Throws()
    {
        Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.LoadFrom(@"D:\does-not-exist\nmreasons.json"));
    }

    [Fact]
    public void NonCanonicalCanId_IsNormalized()
    {
        var json = """
        { "version": 1, "fields": [
          { "signal": "S", "can_id": "0x018FFFAF4", "extended": true } ] }
        """;
        var field = Assert.Single(
            NmReasonDictionaryStore.Load(json, "mem.json").Dictionary.Fields);
        Assert.Equal("0x18FFFAF4", field.CanIdHex);
    }

    [Fact]
    public void EquivalentCanIdSpellings_AreDuplicate()
    {
        var json = """
        { "version": 1, "fields": [
          { "signal": "S", "can_id": "0x1", "extended": true },
          { "signal": "S", "can_id": "0x01", "extended": true } ] }
        """;
        Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.Load(json, "mem.json"));
    }

    [Fact]
    public void NumericKind_IsRejected()
    {
        var json = """
        { "version": 1, "fields": [
          { "signal": "S", "can_id": "0x1", "extended": true, "kind": 3 } ] }
        """;
        Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.Load(json, "mem.json"));
    }

    [Fact]
    public void BlankEntryText_IsRejected()
    {
        var json = """
        { "version": 1, "fields": [
          { "signal": "S", "can_id": "0x1", "extended": true,
            "entries": { "48": "  " } } ] }
        """;
        var ex = Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.Load(json, "mem.json"));
        Assert.Contains("文本为空", ex.Message);
    }

    [Fact]
    public void Entries_LoadTextAndEmptyMapBecomesNull()
    {
        var withText = """
        { "version": 1, "fields": [
          { "signal": "S", "can_id": "0x1", "extended": true,
            "entries": { "48": "NM PDU 唤醒" } } ] }
        """;
        var field = Assert.Single(
            NmReasonDictionaryStore.Load(withText, "mem.json").Dictionary.Fields);
        Assert.NotNull(field.Entries);
        Assert.Equal("NM PDU 唤醒", field.Entries![48]);

        var empty = """
        { "version": 1, "fields": [
          { "signal": "S", "can_id": "0x1", "extended": true, "entries": {} } ] }
        """;
        var emptyField = Assert.Single(
            NmReasonDictionaryStore.Load(empty, "mem.json").Dictionary.Fields);
        Assert.Null(emptyField.Entries);
    }

    [Fact]
    public void EmptyFields_IsRejected()
    {
        var json = """{ "version": 1, "fields": [] }""";
        Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.Load(json, "mem.json"));
    }

    [Fact]
    public void UnsupportedVersion_IsRejected()
    {
        var json = """
        { "version": 99, "fields": [
          { "signal": "S", "can_id": "0x1", "extended": true } ] }
        """;
        Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.Load(json, "mem.json"));
    }

    [Fact]
    public void InvalidUtf8File_IsRejected()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.nmreasons.json");
        var prefix = System.Text.Encoding.UTF8.GetBytes(
            """{"version":1,"fields":[{"signal":"S","can_id":"0x1","extended":true}],"source":{"doc":""");
        var suffix = System.Text.Encoding.UTF8.GetBytes("\"}}");
        try
        {
            File.WriteAllBytes(path, prefix.Concat(new byte[] { 0xFF }).Concat(suffix).ToArray());
            var ex = Assert.Throws<NmReasonDictionaryException>(
                () => { NmReasonDictionaryStore.LoadFrom(path); });
            Assert.Contains("UTF-8", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Utf8BomFile_Loads()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.nmreasons.json");
        try
        {
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            File.WriteAllBytes(path,
                bom.Concat(System.Text.Encoding.UTF8.GetBytes(Minimal)).ToArray());
            var loaded = NmReasonDictionaryStore.LoadFrom(path);
            Assert.Single(loaded.Dictionary.Fields);
            // 哈希按含 BOM 的原始文件字节计算：来源身份不能被解码细节改写。
            Assert.Equal(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(path))),
                loaded.Sha256);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadFrom_HashesFileBytesAndKeepsPath()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.nmreasons.json");
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(Minimal);
            File.WriteAllBytes(path, bytes);
            var loaded = NmReasonDictionaryStore.LoadFrom(path);
            Assert.Equal(path, loaded.Path);
            Assert.Equal(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                loaded.Sha256);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
