using PeakCan.Host.Infrastructure.Cli.Reporting;

namespace PeakCan.Host.Infrastructure.Tests.Cli.Reporting;

public class TrendTrackerTests
{
    private static string GetTempPath() => Path.Combine(Path.GetTempPath(), $"hil_trends_{Guid.NewGuid():N}.json");

    [Fact]
    public void TrendTracker_FirstRun_CreatesFileWithOneEntry()
    {
        var path = GetTempPath();
        try
        {
            var entry = new TrendEntry(DateTime.UtcNow, "Suite", 5, 5, 0, 100);
            TrendTracker.Record(entry, path);

            var entries = TrendTracker.Load(path);
            Assert.Single(entries);
            Assert.Equal("Suite", entries[0].SuiteName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void TrendTracker_ExistingFile_AppendsEntry()
    {
        var path = GetTempPath();
        try
        {
            TrendTracker.Record(new TrendEntry(DateTime.UtcNow, "S1", 5, 5, 0, 100), path);
            TrendTracker.Record(new TrendEntry(DateTime.UtcNow, "S2", 3, 2, 1, 50), path);

            var entries = TrendTracker.Load(path);
            Assert.Equal(2, entries.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void TrendTracker_Over100_RollsOldest()
    {
        var path = GetTempPath();
        try
        {
            for (int i = 0; i < 105; i++)
                TrendTracker.Record(new TrendEntry(DateTime.UtcNow, $"S{i}", i, i, 0, i), path);

            var entries = TrendTracker.Load(path);
            Assert.True(entries.Count <= 100, $"Expected <= 100, got {entries.Count}");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void TrendTracker_CorruptedJson_BackupsAndRebuilds()
    {
        var path = GetTempPath();
        try
        {
            // Write corrupt JSON
            File.WriteAllText(path, "{truncated");

            var entry = new TrendEntry(DateTime.UtcNow, "Suite", 5, 5, 0, 100);
            TrendTracker.Record(entry, path);

            var entries = TrendTracker.Load(path);
            Assert.Single(entries);
            Assert.True(File.Exists(path + ".corrupt-") || Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + ".corrupt-*").Length > 0,
                "Corrupt file should be backed up");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            // Clean up backup files
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + ".corrupt-*"))
                File.Delete(f);
        }
    }
}
