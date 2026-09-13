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

            // 同一文件二次打开不得重复加列（重复 ALTER 会抛 duplicate column name）
            await using (var second = new TraceCacheStore(path))
                await second.InitializeAsync();
            (await ScalarAsync(path, "SELECT COUNT(*) FROM pragma_table_xinfo('frames') WHERE name='pgn';"))
                .Should().Be(1);

            // VIRTUAL 生成列对旧行按需计算：迁移后旧行立即读到正确 PGN
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
            // 随机扩展 ID（覆盖 priority/R-EDP/DP/PF/PS/SA 全域）
            for (var i = 0; i < 1000; i++)
                frames.Add(ExtFrame(index++, (uint)random.Next(0, 1 << 29)));
            // PDU1/PDU2 边界：PF=0xEF（PS 不入 PGN）与 PF=0xF0（PS 并入 PGN），DP=0/1 两种形态
            foreach (var id in new[] { 0x00EF01FFu, 0x01EF01FFu, 0x00F001FFu, 0x01F001FFu, 0x18EF01FFu, 0x18FF50E5u })
                frames.Add(ExtFrame(index++, id));
            // bit31 携带 IDE 约定位的形态（表达式必须先剥位）
            for (var i = 0; i < 10; i++)
                frames.Add(ExtFrame(index++, (uint)random.Next(0, 1 << 29) | 0x80000000u));
            // 非扩展帧：pgn 必须为 NULL
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
                        $"can_id=0x{canId:X8} 的 PGN 必须与 J1939Id 逐位一致");
                }
                else
                {
                    reader.IsDBNull(3).Should().BeTrue("非扩展帧 pgn 必须为 NULL");
                }
                checkedCount++;
            }
            checkedCount.Should().Be(frames.Count);
        }
        finally { DeleteDb(path); }
    }

    // --- P7 Task 3：FrameQuery.PgnAllowList tri-state 下推 ---

    /// <summary>PDU1 形态（PF=0xEF，PS 不入 PGN）。</summary>
    private const uint MatchId = 0x18EF01FF;
    /// <summary>另一 PGN 的 PDU2 形态。</summary>
    private const uint OtherId = 0x18FF10E5;
    /// <summary>应被过滤排除的第三种 PGN。</summary>
    private const uint MissId = 0x18FF20E5;

    private static HashSet<uint> PgnOf(params uint[] ids) =>
        new(ids.Select(id => new J1939Id(id & J1939Id.Raw29Mask).Pgn));

    [Fact]
    public async Task PgnFilter_Null_NoFilter()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.AppendFramesAsync(id, [ExtFrame(0, MatchId), ExtFrame(1, MissId), Frame(2, 0.02)]);

        var page = await store.GetFramesAsync(id, new FrameQuery(AfterIndex: -1, PgnAllowList: null, Limit: 10));

        page.Frames.Should().HaveCount(3);
        page.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task TriState_EmptySet_RejectsAll()
    {
        // parser tri-state：空集 = all-invalid 全拒。store 不得把空集当作"无过滤"
        //（否则 all-invalid 过滤输入会显示全部帧——spec §2.4 回归锁）。
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.AppendFramesAsync(id, [ExtFrame(0, MatchId), Frame(1, 0.01)]);

        (await store.GetFramesAsync(id, new FrameQuery(AfterIndex: -1, CanIds: new HashSet<uint>(), Limit: 10)))
            .Frames.Should().BeEmpty();
        (await store.GetFramesAsync(id, new FrameQuery(AfterIndex: -1, PgnAllowList: new HashSet<uint>(), Limit: 10)))
            .Frames.Should().BeEmpty();
        (await store.GetFramesAsync(id, new FrameQuery(AfterIndex: -1,
                CanIds: new HashSet<uint>(), PgnAllowList: new HashSet<uint>(), Limit: 10)))
            .Frames.Should().BeEmpty();
    }

    [Fact]
    public async Task PgnFilter_Populated_OnlyExtendedMatches()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.AppendFramesAsync(id, [ExtFrame(0, MatchId), Frame(1, 0.01), ExtFrame(2, MissId)]);

        var page = await store.GetFramesAsync(id,
            new FrameQuery(AfterIndex: -1, PgnAllowList: PgnOf(MatchId), Limit: 10));

        page.Frames.Select(f => f.Index).Should().Equal([0L]);
    }

    [Fact]
    public async Task CanIds_And_Pgn_OR_Semantics()
    {
        // OR 语义（与旧 PassesBrowseFilter 逐字一致）：(ID 命中) OR (扩展帧且 PGN 命中)。
        // 双集合非空时走 C# 双分支归并（spec §2.6：不生成单条 OR SQL）。
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.AppendFramesAsync(id,
        [
            Frame(0, 0.00),          // 0x100 → ID 命中
            ExtFrame(1, MatchId),    // PGN 命中
            ExtFrame(2, MissId),     // 都不中 → 排除
            Frame(3, 0.03),          // ID 命中
            ExtFrame(4, MatchId),
        ]);

        var forward = await store.GetFramesAsync(id, new FrameQuery(AfterIndex: -1,
            CanIds: new HashSet<uint> { 0x100 }, PgnAllowList: PgnOf(MatchId, OtherId), Limit: 80));
        forward.Frames.Select(f => f.Index).Should().Equal([0L, 1L, 3L, 4L]);
        forward.HasMore.Should().BeFalse();

        var afterCursor = await store.GetFramesAsync(id, new FrameQuery(AfterIndex: 1,
            CanIds: new HashSet<uint> { 0x100 }, PgnAllowList: PgnOf(MatchId), Limit: 80));
        afterCursor.Frames.Select(f => f.Index).Should().Equal([3L, 4L]);

        var backward = await store.GetFramesAsync(id, new FrameQuery(BeforeIndex: 5,
            CanIds: new HashSet<uint> { 0x100 }, PgnAllowList: PgnOf(MatchId), Limit: 80));
        backward.Frames.Select(f => f.Index).Should().Equal([0L, 1L, 3L, 4L]); // 降序取回后翻正
        backward.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task PgnFilter_Paging_HasMore_Honest()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        // 90 个命中帧 + 10 个不命中帧 → 第一页 80 行 HasMore=true，第二页 10 行 HasMore=false
        var frames = new List<CachedFrame>();
        for (var i = 0; i < 90; i++) frames.Add(ExtFrame(i, MatchId));
        for (var i = 90; i < 100; i++) frames.Add(ExtFrame(i, MissId));
        await store.AppendFramesAsync(id, frames);

        var query = new FrameQuery(PgnAllowList: PgnOf(MatchId), Limit: 80);
        var page1 = await store.GetFramesAsync(id, query);
        page1.Frames.Should().HaveCount(80);
        page1.HasMore.Should().BeTrue();

        var page2 = await store.GetFramesAsync(id, query with { AfterIndex = page1.Frames[^1].Index });
        page2.Frames.Should().HaveCount(10);
        page2.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task PgnFilter_SparseHits_SinglePage_NoFalseHasMore()
    {
        // 旧内存后过滤的失真场景：800 帧仅 3 帧命中——旧路径整页拉 80 行内存过滤后
        // 剩 3 行却报告 HasMore=true。SQL 下推后必须如实返回 false。
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        var frames = new List<CachedFrame>();
        for (var i = 0; i < 800; i++)
            frames.Add(i is 100 or 400 or 700 ? ExtFrame(i, MatchId) : ExtFrame(i, MissId));
        await store.AppendFramesAsync(id, frames);

        var page = await store.GetFramesAsync(id,
            new FrameQuery(AfterIndex: -1, PgnAllowList: PgnOf(MatchId), Limit: 80));

        page.Frames.Select(f => f.Index).Should().Equal([100L, 400L, 700L]);
        page.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task ExplainQueryPlan_SingleFilter_UsesMatchingIndex_NoTempBTree()
    {
        var path = TempDbPath();
        try
        {
            await using (var store = new TraceCacheStore(path))
                await store.InitializeAsync();

            var pgnSql = TraceCacheStore.BuildFramePageSql(
                1, new FrameQuery(AfterIndex: -1, PgnAllowList: PgnOf(MatchId), Limit: 80),
                out var pgnParameters);
            var idSql = TraceCacheStore.BuildFramePageSql(
                1, new FrameQuery(AfterIndex: -1, CanIds: new HashSet<uint> { 0x100 }, Limit: 80),
                out var idParameters);

            // pgn 分支由 INDEXED BY 强制走复合索引（spec §2.6 兜底）；ORDER BY idx 的
            // TEMP B-TREE 排序输入仅为命中行（有界），属可接受计划，不断言其缺席。
            var pgnPlan = await ExplainAsync(path, pgnSql, pgnParameters);
            pgnPlan.Should().Contain("idx_frames_pgn_idx");
            // ID 分支只要求是 SEARCH（未退化全表 SCAN）
            var idPlan = await ExplainAsync(path, idSql, idParameters);
            idPlan.Should().Contain("SEARCH").And.NotContain("SCAN");
        }
        finally { DeleteDb(path); }
    }

    // --- P7 Task 4：GetFramesForCanIdAsync 窗口查询 ---

    private static CachedFrame ExtFrameAt(long index, double timestamp, uint canId)
        => new(index, timestamp, canId, true, 8, new byte[8]);

    [Fact]
    public async Task WindowQuery_ClosedInterval_BothEndsInclusive_ExcludesOtherIds()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.AppendFramesAsync(id,
        [
            ExtFrameAt(0, 1.0, MatchId),
            ExtFrameAt(1, 2.0, MatchId),   // 恰为 tStart（闭区间含端点）
            ExtFrameAt(2, 3.0, MatchId),   // 恰为 tEnd
            ExtFrameAt(3, 4.0, MatchId),
            ExtFrameAt(4, 2.5, OtherId),   // 窗口内但不同 ID → 排除
        ]);

        var page = await store.GetFramesForCanIdAsync(id, MatchId, tStart: 2.0, tEnd: 3.0);

        page.Frames.Select(f => f.Index).Should().Equal([1L, 2L]);
        page.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task WindowQuery_OrderedByTimestamp_ThenIdx()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        // 写入顺序与时间序刻意错开（BLF 重排语义）；同刻两帧按 idx 升序（对齐
        // GetLatestFramesBeforeAsync 的并列语义）。
        await store.AppendFramesAsync(id,
        [
            ExtFrameAt(0, 2.0, MatchId),
            ExtFrameAt(1, 1.0, MatchId),
            ExtFrameAt(2, 2.0, MatchId),
        ]);

        var page = await store.GetFramesForCanIdAsync(id, MatchId, tStart: null, tEnd: null);

        page.Frames.Select(f => f.Index).Should().Equal([1L, 0L, 2L]);
    }

    [Fact]
    public async Task WindowQuery_LimitPlusOne_SetsHasMore()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.AppendFramesAsync(id,
            Enumerable.Range(0, 4).Select(i => ExtFrameAt(i, 1.0 + i, MatchId)).ToArray());

        var page = await store.GetFramesForCanIdAsync(id, MatchId, tStart: null, tEnd: null, limit: 3);

        page.Frames.Should().HaveCount(3);
        page.HasMore.Should().BeTrue();
    }

    private static async Task<string> ExplainAsync(string path, string sql, IReadOnlyList<SqliteParameter> parameters)
    {
        await using var probe = new SqliteConnection($"Data Source={path};Pooling=False");
        await probe.OpenAsync();
        await using var command = probe.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var parameter in parameters)
            command.Parameters.Add(new SqliteParameter(parameter.ParameterName, parameter.Value));
        using var reader = await command.ExecuteReaderAsync();
        var details = new List<string>();
        while (await reader.ReadAsync())
            details.Add(reader.GetString(3)); // detail 列
        return string.Join("\n", details);
    }
}