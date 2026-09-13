using FluentAssertions;
using Microsoft.Data.Sqlite;
using PeakCan.Host.Core.J1939;
using PeakCan.Host.Mobile.Core.Services;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class TraceCacheStoreTests
{
    private static CachedFrame Frame(long index, double timestamp, uint id = 0x100)
        => new(index, timestamp, id, false, 2, [1, 2]);

    [Fact]
    public async Task Initialize_Is_Idempotent_And_Creates_Trace()
    {
        await using var store = new TraceCacheStore(":memory:");
        await store.InitializeAsync();

        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.InitializeAsync();

        id.Should().BeGreaterThan(0);
        var summary = await store.GetTraceAsync(id);
        summary!.SourceName.Should().Be("a.asc");
        summary.FileSizeBytes.Should().Be(100);
        summary.Complete.Should().BeFalse();
    }

    [Fact]
    public async Task GetOrCreate_Reuses_Same_Name_And_Size()
    {
        await using var store = new TraceCacheStore(":memory:");
        var first = await store.GetOrCreateTraceAsync("a.asc", 100);
        var second = await store.GetOrCreateTraceAsync("a.asc", 100);
        var other = await store.GetOrCreateTraceAsync("a.asc", 101);

        second.Should().Be(first);
        other.Should().NotBe(first);
    }

    [Fact]
    public async Task AppendFrames_Updates_FrameCount_And_Duration()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);

        await store.AppendFramesAsync(id, [Frame(0, 0), Frame(1, 0.5)]);
        await store.AppendFramesAsync(id, [Frame(2, 1.25)]);

        var summary = await store.GetTraceAsync(id);
        summary!.FrameCount.Should().Be(3);
        summary.Duration.Should().Be(1.25);
    }

    [Fact]
    public async Task Completed_Trace_Can_Be_Found_By_Name_And_Size()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        (await store.FindCompletedAsync("a.asc", 100)).Should().BeNull();

        await store.MarkCompletedAsync(id);
        await store.UpdateLastPositionAsync(id, 12.5);

        var found = await store.FindCompletedAsync("a.asc", 100);
        found!.TraceId.Should().Be(id);
        found.Complete.Should().BeTrue();
        found.LastPositionSeconds.Should().Be(12.5);
    }

    [Fact]
    public async Task GetFrames_Pages_Forward_And_Filters_CanIds()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        var frames = Enumerable.Range(0, 181)
            .Select(i => Frame(i, i * 0.01, i % 2 == 0 ? 0x100u : 0x200u))
            .ToArray();
        await store.AppendFramesAsync(id, frames);

        var first = await store.GetFramesAsync(id, new FrameQuery(AfterIndex: -1, Limit: 80));
        first.Frames.Should().HaveCount(80);
        first.Frames[0].Index.Should().Be(0);
        first.Frames[^1].Index.Should().Be(79);
        first.HasMore.Should().BeTrue();

        var filtered = await store.GetFramesAsync(id, new FrameQuery(AfterIndex: -1, CanIds: new HashSet<uint> { 0x200 }, Limit: 3));
        filtered.Frames.Select(f => f.Index).Should().Equal([1L, 3L, 5L]);
        filtered.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task GetFrames_Pages_Backward_In_Chronological_Order()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        var frames = Enumerable.Range(0, 100).Select(i => Frame(i, i)).ToArray();
        await store.AppendFramesAsync(id, frames);

        var previous = await store.GetFramesAsync(id, new FrameQuery(BeforeIndex: 80, Limit: 50));

        previous.Frames.Select(f => f.Index).Should().Equal(Enumerable.Range(30, 50).Select(i => (long)i));
        previous.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task ListTraces_Returns_Newest_First()
    {
        await using var store = new TraceCacheStore(":memory:");
        var first = await store.GetOrCreateTraceAsync("first.asc", 1);
        var second = await store.GetOrCreateTraceAsync("second.asc", 2);

        var list = await store.ListTracesAsync(2);

        list.Select(t => t.TraceId).Should().Equal([second, first]);
    }

    [Fact]
    public async Task GetLatestFramesBefore_ReturnsPerIdLatestAtOrBeforeTimestamp()
    {
        // Arrange: 0x100 三帧（t=1.0/3.0/3.0，其中 3.0 同刻两帧）；0x200 一帧 t=2.0
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.AppendFramesAsync(id,
        [
            Frame(0, 1.0, 0x100),
            Frame(1, 3.0, 0x100),
            Frame(2, 3.0, 0x100),
            Frame(3, 2.0, 0x200),
        ]);

        var result = await store.GetLatestFramesBeforeAsync(id, 3.0);

        result.Should().HaveCount(2);
        result.Select(f => f.CanId).Should().Equal([0x100u, 0x200u]); // 按 can_id 升序
        result[0].CanId.Should().Be(0x100);
        result[0].Timestamp.Should().Be(3.0);
        result[0].Index.Should().Be(2); // 同刻两帧取 idx 较大者
        result[1].CanId.Should().Be(0x200);
        result[1].Timestamp.Should().Be(2.0);
    }

    [Fact]
    public async Task GetLatestFramesBefore_BeforeAnyFrame_ReturnsEmpty()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.AppendFramesAsync(id, [Frame(0, 1.0, 0x100)]);

        var result = await store.GetLatestFramesBeforeAsync(id, 0.5);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetLatestFramesBefore_OtherTraceNotIncluded()
    {
        await using var store = new TraceCacheStore(":memory:");
        var first = await store.GetOrCreateTraceAsync("a.asc", 100);
        var second = await store.GetOrCreateTraceAsync("b.asc", 200);
        await store.AppendFramesAsync(first, [Frame(0, 1.0, 0x100)]);
        await store.AppendFramesAsync(second, [Frame(0, 5.0, 0x500)]);

        var result = await store.GetLatestFramesBeforeAsync(first, 10.0);

        result.Should().ContainSingle(f => f.CanId == 0x100);
        result.Should().NotContain(f => f.CanId == 0x500);
    }

    [Fact]
    public async Task GetLatestFramesBefore_ReorderedArrival_PicksByTimestampNotIdx()
    {
        // BLF 重排语义：id=0x300 先写 t=5.0（idx=0），再写 t=4.0（idx=1）——
        // 到达更晚但时间更早。ts=5.0 必须返回 t=5.0 帧（timestamp 主选，idx 仅破同刻并列）。
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.AppendFramesAsync(id, [Frame(0, 5.0, 0x300)]);
        await store.AppendFramesAsync(id, [Frame(1, 4.0, 0x300)]);

        var result = await store.GetLatestFramesBeforeAsync(id, 5.0);

        var frame = result.Should().ContainSingle().Subject;
        frame.Timestamp.Should().Be(5.0);
        frame.Index.Should().Be(0);
    }

    [Fact]
    public async Task FindFrame_First_ReturnsEarliestByIndex()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.AppendFramesAsync(id,
        [
            Frame(0, 3.0, 0x100),
            Frame(1, 1.0, 0x100),
            Frame(2, 2.0, 0x100),
        ]);

        var result = await store.FindFrameAsync(id, 0x100, null, CacheSearchDirection.First);

        result.Should().NotBeNull();
        result!.Index.Should().Be(0);
        result.Timestamp.Should().Be(3.0);
    }

    [Fact]
    public async Task FindFrame_Next_ReturnsEarliestStrictlyAfter()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.AppendFramesAsync(id,
        [
            Frame(0, 1.0, 0x100),
            Frame(1, 3.0, 0x100),
            Frame(2, 3.0, 0x100),
            Frame(3, 5.0, 0x100),
        ]);

        var result = await store.FindFrameAsync(id, 0x100, 1.0, CacheSearchDirection.Next);

        result.Should().NotBeNull();
        result!.Timestamp.Should().Be(3.0);
        result.Index.Should().Be(1); // 同刻取 idx 小者
    }

    [Fact]
    public async Task FindFrame_Next_NoMatch_ReturnsNull()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.AppendFramesAsync(id, [Frame(0, 1.0, 0x100)]);

        var result = await store.FindFrameAsync(id, 0x100, 5.0, CacheSearchDirection.Next);

        result.Should().BeNull();
    }

    [Fact]
    public async Task FindFrame_UnknownId_ReturnsNull()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.AppendFramesAsync(id, [Frame(0, 1.0, 0x100)]);

        var result = await store.FindFrameAsync(id, 0x200, null, CacheSearchDirection.First);

        result.Should().BeNull();
    }

    // --- P7 Task 2：pgn 生成列与幂等迁移 ---

    private const uint LegacyExtendedId = 0x18EF01FF;

    private static string TempDbPath() => Path.Combine(Path.GetTempPath(), $"pgntest_{Guid.NewGuid():N}.db");

    private static void DeleteDb(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static CachedFrame ExtFrame(long index, uint canId)
        => new(index, index * 0.01, canId, true, 8, new byte[8]);

    private static async Task<long> ScalarAsync(string path, string sql)
    {
        await using var probe = new SqliteConnection($"Data Source={path};Pooling=False");
        await probe.OpenAsync();
        using var command = probe.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>构造 P6 时代旧 schema（frames 无 pgn 生成列）并写入一条扩展帧。</summary>
    private static async Task CreateLegacyDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS traces (
              trace_id INTEGER PRIMARY KEY,
              source_name TEXT NOT NULL,
              file_size INTEGER NOT NULL,
              imported_at TEXT NOT NULL,
              frame_count INTEGER NOT NULL DEFAULT 0,
              duration REAL NOT NULL DEFAULT 0,
              complete INTEGER NOT NULL DEFAULT 0,
              last_position_seconds REAL NOT NULL DEFAULT 0,
              UNIQUE(source_name, file_size)
            );
            CREATE TABLE IF NOT EXISTS frames (
              trace_id INTEGER NOT NULL REFERENCES traces(trace_id),
              idx INTEGER NOT NULL,
              timestamp REAL NOT NULL,
              can_id INTEGER NOT NULL,
              is_extended INTEGER NOT NULL,
              dlc INTEGER NOT NULL,
              data BLOB NOT NULL,
              PRIMARY KEY (trace_id, idx)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS idx_frames_ts ON frames(trace_id, timestamp);
            CREATE INDEX IF NOT EXISTS idx_frames_id ON frames(trace_id, can_id);
            CREATE INDEX IF NOT EXISTS idx_frames_cid_idx ON frames(trace_id, can_id, idx);
            CREATE INDEX IF NOT EXISTS idx_frames_cid_ts ON frames(trace_id, can_id, timestamp);
            """;
        await create.ExecuteNonQueryAsync();

        using var insertTrace = connection.CreateCommand();
        insertTrace.CommandText =
            "INSERT INTO traces(source_name,file_size,imported_at) VALUES('legacy.asc',1,'2026-01-01T00:00:00.0000000+00:00');";
        await insertTrace.ExecuteNonQueryAsync();

        using var insertFrame = connection.CreateCommand();
        insertFrame.CommandText =
            "INSERT INTO frames(trace_id,idx,timestamp,can_id,is_extended,dlc,data) VALUES(1,0,1.0,$id,1,8,$data);";
        insertFrame.Parameters.AddWithValue("$id", LegacyExtendedId);
        insertFrame.Parameters.AddWithValue("$data", new byte[8]);
        await insertFrame.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Sqlite_Version_Supports_Generated_Columns()
    {
        // spec §2.2 prerequisite: generated columns require SQLite ≥ 3.31 (constant regression guard)
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        var version = Version.Parse((string)(await command.ExecuteScalarAsync())!);

        version.Should().BeGreaterThanOrEqualTo(new Version(3, 31));
    }

    [Fact]
    public async Task NewDatabase_HasPgnColumnAndIndex()
    {
        var path = TempDbPath();
        try
        {
            await using (var store = new TraceCacheStore(path))
                await store.InitializeAsync();

            (await ScalarAsync(path, "SELECT COUNT(*) FROM pragma_table_xinfo('frames') WHERE name='pgn';"))
                .Should().Be(1);
            (await ScalarAsync(path, "SELECT COUNT(*) FROM pragma_index_list('frames') WHERE name='idx_frames_pgn_idx';"))
                .Should().Be(1);
        }
        finally { DeleteDb(path); }
    }

    [Fact]
    public async Task LegacyDatabase_MigratesOnOpen_Idempotent()
    {
        var path = TempDbPath();
        try
        {
            await CreateLegacyDatabaseAsync(path);

            // Legacy trace reusable: opening the old database does not rebuild the trace table
            await using (var store = new TraceCacheStore(path))
            {
                await store.InitializeAsync();
                (await store.GetOrCreateTraceAsync("legacy.asc", 1)).Should().Be(1);
            }

            // Opening the same file a second time must not add the column again (a duplicate ALTER would throw duplicate column name)
            await using (var second = new TraceCacheStore(path))
                await second.InitializeAsync();
            (await ScalarAsync(path, "SELECT COUNT(*) FROM pragma_table_xinfo('frames') WHERE name='pgn';"))
                .Should().Be(1);

            // VIRTUAL generated columns compute on the fly for old rows as well: legacy rows immediately get the correct PGN
            (await ScalarAsync(path, "SELECT pgn FROM frames WHERE trace_id=1 AND idx=0;"))
                .Should().Be((long)new J1939Id(LegacyExtendedId).Pgn);
        }
        finally { DeleteDb(path); }
    }

    [Fact]
    public async Task PgnExpression_Matches_J1939Id_Fuzz()
    {
        var path = TempDbPath();
        try
        {
            var frames = new List<CachedFrame>();
            long index = 0;
            var random = new Random(20260913);
            // Random extended IDs (covering priority/R-EDP/DP/PF/PS/SA full domain)
            for (var i = 0; i < 1000; i++)
                frames.Add(ExtFrame(index++, (uint)random.Next(0, 1 << 29)));
            // PDU1/PDU2 boundary: PF=0xEF (PS is not part of PGN) and PF=0xF0 (PS is part of PGN), DP=0/1 two forms
            foreach (var id in new[] { 0x00EF01FFu, 0x01EF01FFu, 0x00F001FFu, 0x01F001FFu, 0x18EF01FFu, 0x18FF50E5u })
                frames.Add(ExtFrame(index++, id));
            // Forms carrying the IDE reserved bit at bit31 (the expression must strip bits first)
            for (var i = 0; i < 10; i++)
                frames.Add(ExtFrame(index++, (uint)random.Next(0, 1 << 29) | 0x80000000u));
            // Non-extended frames: pgn must be NULL
            for (var i = 0; i < 50; i++)
                frames.Add(new CachedFrame(index++, i * 0.01, (uint)random.Next(0, 0x800), false, 8, new byte[8]));

            long traceId;
            await using (var store = new TraceCacheStore(path))
            {
                await store.InitializeAsync();
                traceId = await store.GetOrCreateTraceAsync("fuzz.asc", 1);
                await store.AppendFramesAsync(traceId, frames);
            }

            await using var probe = new SqliteConnection($"Data Source={path};Pooling=False");
            await probe.OpenAsync();
            using var query = probe.CreateCommand();
            query.CommandText = "SELECT idx,can_id,is_extended,pgn FROM frames WHERE trace_id=$id ORDER BY idx;";
            query.Parameters.AddWithValue("$id", traceId);
            using var reader = await query.ExecuteReaderAsync();

            var checkedCount = 0;
            while (await reader.ReadAsync())
            {
                var canId = (uint)reader.GetInt64(1);
                if (reader.GetBoolean(2))
                {
                    reader.GetInt64(3).Should().Be((long)new J1939Id(canId & J1939Id.Raw29Mask).Pgn,
                        $"can_id=0x{canId:X8} PGN should match J1939Id bit by bit");
                }
                else
                {
                    reader.IsDBNull(3).Should().BeTrue("non-extended frame pgn must be NULL");
                }
                checkedCount++;
            }
            checkedCount.Should().Be(frames.Count);
        }
        finally { DeleteDb(path); }
    }
}