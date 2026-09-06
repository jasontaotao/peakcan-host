using System.IO;
using System.Text;
using FluentAssertions;
using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

/// <summary>
/// P0-3 (2026-09-06)：DbcDocumentCache 按 (path, mtime, size) 缓存解析结果，
/// 避免每次 HIL run 重建 host 时重复解析同一 DBC 文件。
/// </summary>
public sealed class DbcDocumentCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"peakcan-dbc-cache-{Guid.NewGuid():N}");

    public DbcDocumentCacheTests()
    {
        Directory.CreateDirectory(_dir);
        DbcDocumentCache.Clear();
    }

    public void Dispose()
    {
        DbcDocumentCache.Clear();
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private string WriteDbc(string name, int messageCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VERSION \"\"");
        for (int i = 0; i < messageCount; i++)
        {
            var id = 0x100 + i;
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"BO_ {id} MSG{i}: 8 Vector__XXX");
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $" SG_ Sig{i} : 0|8@1+ (1,0) [0|255] \"\" Vector__XXX");
        }
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    [Fact]
    public void Load_Same_Unchanged_File_Returns_Same_Instance()
    {
        var path = WriteDbc("a.dbc", messageCount: 3);

        var first = DbcDocumentCache.Load(path);
        var second = DbcDocumentCache.Load(path);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void Load_Modified_File_Returns_Fresh_Parse()
    {
        var path = WriteDbc("b.dbc", messageCount: 3);
        var first = DbcDocumentCache.Load(path);

        // 重写为不同消息数并显式推进 mtime（同秒内写入时 tick 可能不变）。
        File.WriteAllText(path, new StringBuilder()
            .AppendLine("VERSION \"\"")
            .AppendLine("BO_ 200 MSG: 8 Vector__XXX")
            .AppendLine(" SG_ Sig : 0|8@1+ (1,0) [0|255] \"\" Vector__XXX")
            .ToString());
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));

        var second = DbcDocumentCache.Load(path);

        second.Should().NotBeSameAs(first);
        second.Messages.Should().HaveCount(1);
    }

    [Fact]
    public void Load_Invalid_Dbc_Throws_InvalidOperationException()
    {
        var path = Path.Combine(_dir, "bad.dbc");
        // BO_ 的 ID 位要求十进制/十六进制数字字面量，标识符触发解析失败。
        File.WriteAllText(path, "VERSION \"\"\nBO_ badIdentifier MSG: 8 Vector__XXX\n");

        var act = () => DbcDocumentCache.Load(path);

        act.Should().Throw<InvalidOperationException>().WithMessage("*DBC parse failed*");
    }

    [Fact]
    public void Load_Missing_File_Throws_FileNotFoundException()
    {
        var act = () => DbcDocumentCache.Load(Path.Combine(_dir, "missing.dbc"));

        act.Should().Throw<FileNotFoundException>();
    }
}
