using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class AscFrameSinkFactoryTests
{
    private static string GetTempDir() => Path.Combine(Path.GetTempPath(), $"hil_sink_{Guid.NewGuid():N}");

    [Fact]
    public void Create_ProducesNamedFile_AtExpectedPath()
    {
        var dir = GetTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var factory = new AscFrameSinkFactory(dir, "20260815_120000_000");
            using var sink = factory.Create("Brake_Test", 2);
            Assert.NotNull(sink);
            Assert.True(File.Exists(Path.Combine(dir, "Brake_Test_2_20260815_120000_000.asc")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Create_LongName_TruncatesTo100()
    {
        var dir = GetTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var factory = new AscFrameSinkFactory(dir, "TS");
            using var sink = factory.Create(new string('中', 200), 0);
            var file = Directory.GetFiles(dir, "*.asc").Single();
            var fileName = Path.GetFileNameWithoutExtension(file);
            Assert.True(fileName.Length <= 100 + "_0_TS".Length, $"file name too long: {fileName.Length}");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Create_MissingDirectory_ReturnsNull_NoThrow()
    {
        var factory = new AscFrameSinkFactory(Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}"), "TS");
        Assert.Null(factory.Create("Case", 0));   // A8: 降级 null，不抛
    }
}
