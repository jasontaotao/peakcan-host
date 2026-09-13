using System.Globalization;
using Microsoft.Data.Sqlite;
using PeakCan.Host.Mobile.Core.Models;

namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>
/// SQLite-backed replay cache. The spec schema is extended with
/// <c>last_position_seconds</c> (recent-file playback position) and a
/// <c>pgn</c> VIRTUAL generated column (P7 J1939 filter push-down).
/// </summary>
public sealed class TraceCacheStore : ITraceCacheStore
{
    /// <summary>
    /// pgn 生成列表达式——与 <c>J1939Id.Pgn</c> 逐位一致
    /// （R/EDP&lt;&lt;17 | DP&lt;&lt;16 | PF&lt;&lt;8 | PDU2 才并入 PS）；非扩展帧为 NULL，
    /// 天然不被任何 IN 过滤命中。DDL 与旧库 ALTER 共用同一份文本，杜绝两份漂移。
    /// </summary>
    private const string PgnGeneratedSql = """
        CASE WHEN is_extended = 1 THEN
          ((((can_id & 536870911) >> 25) & 1) << 17)
          | ((((can_id & 536870911) >> 24) & 1) << 16)
          | ((((can_id & 536870911) >> 16) & 255) << 8)
          | (CASE WHEN (((can_id & 536870911) >> 16) & 255) < 240
                  THEN 0 ELSE (((can_id & 536870911) >> 8) & 255) END)
        END
        """;

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public TraceCacheStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        _connection = new SqliteConnection(builder.ToString());
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await _connection.OpenAsync(ct).ConfigureAwait(false);
            await ExecuteAsync($"""
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;

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
                  pgn INTEGER GENERATED ALWAYS AS ({PgnGeneratedSql}) VIRTUAL,
                  PRIMARY KEY (trace_id, idx)
                ) WITHOUT ROWID;

                CREATE INDEX IF NOT EXISTS idx_frames_ts ON frames(trace_id, timestamp);
                CREATE INDEX IF NOT EXISTS idx_frames_id ON frames(trace_id, can_id);
                -- P5 搜索/锚点复合索引：FindFrameAsync(First/Next) 的 can_id+idx / can_id+timestamp
                -- 排序走覆盖索引，避免 ORDER BY 全表 sort（旧 DB 由 IF NOT EXISTS 幂等补齐）
                CREATE INDEX IF NOT EXISTS idx_frames_cid_idx ON frames(trace_id, can_id, idx);
                CREATE INDEX IF NOT EXISTS idx_frames_cid_ts ON frames(trace_id, can_id, timestamp);
                """, ct).ConfigureAwait(false);

            // P7：旧库幂等补 pgn 生成列（SQLite 的 ALTER 只支持 VIRTUAL；新库上面 DDL 已含，此处跳过）
            if (!await HasFrameColumnAsync("pgn", ct).ConfigureAwait(false))
            {
                await ExecuteAsync(
                    $"ALTER TABLE frames ADD COLUMN pgn INTEGER GENERATED ALWAYS AS ({PgnGeneratedSql}) VIRTUAL;",
                    ct).ConfigureAwait(false);
            }

            // pgn 索引必须在生成列就位后创建（旧库 ALTER 前该列不存在，不能进上面的首屏脚本）
            await ExecuteAsync(
                "CREATE INDEX IF NOT EXISTS idx_frames_pgn_idx ON frames(trace_id, pgn, idx);",
                ct).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> GetOrCreateTraceAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        await ReadyAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await QuerySingleSummaryAsync(
                "SELECT * FROM traces WHERE source_name=$source_name AND file_size=$file_size",
                CreateSourceParameters(sourceName, fileSizeBytes), ct).ConfigureAwait(false);
            if (existing is not null) return existing.TraceId;

            await ExecuteAsync(
                "INSERT INTO traces(source_name,file_size,imported_at) VALUES($source_name,$file_size,$imported_at)",
                [
                    new("$source_name", sourceName),
                    new("$file_size", fileSizeBytes),
                    new("$imported_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                ], ct).ConfigureAwait(false);
            return await QueryInt64Async("SELECT last_insert_rowid();", ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendFramesAsync(long traceId, IReadOnlyList<CachedFrame> frames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0) return;
        await ReadyAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var sqliteTransaction = (SqliteTransaction)transaction;

            var insert = _connection.CreateCommand();
            insert.Transaction = sqliteTransaction;
            insert.CommandText = """
                INSERT OR REPLACE INTO frames(trace_id,idx,timestamp,can_id,is_extended,dlc,data)
                VALUES($trace_id,$idx,$timestamp,$can_id,$is_extended,$dlc,$data)
                """;
            insert.Parameters.AddRange(
            [
                new("$trace_id", traceId),
                new("$idx", DBNull.Value),
                new("$timestamp", DBNull.Value),
                new("$can_id", DBNull.Value),
                new("$is_extended", DBNull.Value),
                new("$dlc", DBNull.Value),
                new("$data", DBNull.Value),
            ]);

            foreach (var frame in frames)
            {
                insert.Parameters["$idx"].Value = frame.Index;
                insert.Parameters["$timestamp"].Value = frame.Timestamp;
                insert.Parameters["$can_id"].Value = frame.CanId;
                insert.Parameters["$is_extended"].Value = frame.IsExtended ? 1 : 0;
                insert.Parameters["$dlc"].Value = frame.Dlc;
                insert.Parameters["$data"].Value = frame.Data;
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var update = _connection.CreateCommand();
            update.Transaction = sqliteTransaction;
            update.CommandText = """
                UPDATE traces
                SET frame_count=MAX(frame_count,(SELECT MAX(idx)+1 FROM frames WHERE trace_id=$trace_id)),
                    duration=MAX(duration,(SELECT MAX(timestamp) FROM frames WHERE trace_id=$trace_id))
                WHERE trace_id=$trace_id
                """;
            update.Parameters.AddWithValue("$trace_id", traceId);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkCompletedAsync(long traceId, CancellationToken ct = default)
    {
        await ReadyAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecuteAsync("UPDATE traces SET complete=1 WHERE trace_id=$trace_id",
                [new("$trace_id", traceId)], ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateLastPositionAsync(long traceId, double seconds, CancellationToken ct = default)
    {
        await ReadyAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecuteAsync("UPDATE traces SET last_position_seconds=$seconds WHERE trace_id=$trace_id",
                [new("$seconds", Math.Max(0, seconds)), new("$trace_id", traceId)], ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TraceCacheSummary?> FindCompletedAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default)
    {
        await ReadyAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await QuerySingleSummaryAsync(
                "SELECT * FROM traces WHERE source_name=$source_name AND file_size=$file_size AND complete=1",
                CreateSourceParameters(sourceName, fileSizeBytes), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TraceCacheSummary?> GetTraceAsync(long traceId, CancellationToken ct = default)
    {
        await ReadyAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await QuerySingleSummaryAsync("SELECT * FROM traces WHERE trace_id=$trace_id",
                [new("$trace_id", traceId)], ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FramePage> GetFramesAsync(long traceId, FrameQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit <= 0) throw new ArgumentOutOfRangeException(nameof(query.Limit));
        await ReadyAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var where = "WHERE trace_id=$trace_id";
            var parameters = new List<SqliteParameter> { new("$trace_id", traceId) };

            if (query.CanIds is { Count: > 0 })
            {
                var names = query.CanIds.Select((_, i) => $"$can{i}").ToArray();
                where += $" AND can_id IN ({string.Join(',', names)})";
                parameters.AddRange(query.CanIds.Select((id, i) => new SqliteParameter(names[i], id)));
            }

            var forward = query.BeforeIndex is null;
            if (forward)
            {
                parameters.Add(new("$cursor", query.AfterIndex ?? -1));
                where += " AND idx > $cursor ORDER BY idx ASC";
            }
            else
            {
                parameters.Add(new("$cursor", query.BeforeIndex!.Value));
                where += " AND idx < $cursor ORDER BY idx DESC";
            }

            var sql = $"""
                SELECT idx,timestamp,can_id,is_extended,dlc,data
                FROM frames {where}
                LIMIT $limit
                """;
            parameters.Add(new("$limit", query.Limit + 1));

            await using var command = _connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
                command.Parameters.Add(parameter);

            var result = new List<CachedFrame>();
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result.Add(new CachedFrame(
                    reader.GetInt64(0),
                    reader.GetDouble(1),
                    (uint)reader.GetInt64(2),
                    reader.GetInt64(3) != 0,
                    reader.GetByte(4),
                    (byte[])reader.GetValue(5)));
            }

            var hasMore = result.Count > query.Limit;
            if (hasMore) result.RemoveAt(result.Count - 1);
            if (!forward) result.Reverse();
            return new FramePage(result, hasMore);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CachedFrame>> GetLatestFramesBeforeAsync(
        long traceId, double timestamp, CancellationToken ct = default)
    {
        if (double.IsNaN(timestamp)) throw new ArgumentOutOfRangeException(nameof(timestamp));
        await ReadyAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // timestamp 主选（ASC 流式与 BLF 2 秒重排窗口下都正确）、idx 仅破同刻并列。
            // BLF 重排下 idx 单选会取到"到达更晚但时间更早"的错位帧，但 idx 最大值也未必
            // 落在 timestamp 最大那一帧上（Task 2 测试 case 4：idx=0/t=5.0 与 idx=1/t=4.0，
            // 须返回 t=5.0 帧），因此分两层取：先定每 can_id 的 MAX(timestamp)，
            // 再在同一 (can_id, timestamp) 内取 MAX(idx)。
            var sql = """
                SELECT f.idx, f.timestamp, f.can_id, f.is_extended, f.dlc, f.data
                FROM frames f
                JOIN (
                    SELECT can_id, MAX(timestamp) AS mt
                    FROM frames WHERE trace_id=$traceId AND timestamp<=$ts
                    GROUP BY can_id
                ) x ON f.trace_id=$traceId AND f.can_id=x.can_id AND f.timestamp=x.mt
                JOIN (
                    SELECT can_id, timestamp, MAX(idx) AS mi
                    FROM frames WHERE trace_id=$traceId AND timestamp<=$ts
                    GROUP BY can_id, timestamp
                ) y ON f.trace_id=$traceId AND f.can_id=y.can_id
                     AND f.timestamp=y.timestamp AND f.idx=y.mi
                ORDER BY f.can_id;
                """;
            await using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddRange(
            [
                new("$traceId", traceId),
                new("$ts", timestamp),
            ]);
            var result = new List<CachedFrame>();
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result.Add(new CachedFrame(
                    reader.GetInt64(0),
                    reader.GetDouble(1),
                    (uint)reader.GetInt64(2),
                    reader.GetInt64(3) != 0,
                    reader.GetByte(4),
                    (byte[])reader.GetValue(5)));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CachedFrame?> FindFrameAsync(
        long traceId, uint canId, double? afterTimestamp,
        CacheSearchDirection direction, CancellationToken ct = default)
    {
        await ReadyAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // First=全缓存最早一帧；Next=timestamp 严格大于 afterTimestamp（必填，
            // 为 null 时按 First 处理）的最早一帧（t 后同刻并列取 idx 小者）。
            var sql = direction == CacheSearchDirection.First
                ? """
                  SELECT idx,timestamp,can_id,is_extended,dlc,data FROM frames
                  WHERE trace_id=$traceId AND can_id=$canId ORDER BY idx ASC LIMIT 1
                  """
                : """
                  SELECT idx,timestamp,can_id,is_extended,dlc,data FROM frames
                  WHERE trace_id=$traceId AND can_id=$canId AND timestamp>$after
                  ORDER BY timestamp ASC, idx ASC LIMIT 1
                  """;
            await using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddRange(
            [
                new("$traceId", traceId),
                new("$canId", canId),
                new("$after", afterTimestamp ?? double.MinValue),
            ]);
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            return new CachedFrame(
                reader.GetInt64(0),
                reader.GetDouble(1),
                (uint)reader.GetInt64(2),
                reader.GetInt64(3) != 0,
                reader.GetByte(4),
                (byte[])reader.GetValue(5));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TraceCacheSummary>> ListTracesAsync(int limit = 100, CancellationToken ct = default)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        await ReadyAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new List<TraceCacheSummary>();
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM traces ORDER BY imported_at DESC LIMIT $limit";
            command.Parameters.Add(new("$limit", limit));
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result.Add(new TraceCacheSummary(
                    reader.GetInt64(reader.GetOrdinal("trace_id")),
                    reader.GetString(reader.GetOrdinal("source_name")),
                    reader.GetInt64(reader.GetOrdinal("file_size")),
                    DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("imported_at")), CultureInfo.InvariantCulture),
                    reader.GetInt64(reader.GetOrdinal("frame_count")),
                    reader.GetDouble(reader.GetOrdinal("duration")),
                    reader.GetInt64(reader.GetOrdinal("complete")) != 0,
                    reader.GetDouble(reader.GetOrdinal("last_position_seconds"))));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
    private async Task ReadyAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!_initialized) await InitializeAsync(ct).ConfigureAwait(false);
    }

    private static SqliteParameter[] CreateSourceParameters(string sourceName, long fileSizeBytes) =>
    [
        new("$source_name", sourceName),
        new("$file_size", fileSizeBytes),
    ];

    private Task ExecuteAsync(string sql, CancellationToken ct = default) =>
        ExecuteAsync(sql, null, ct);

    /// <summary>Checks whether the frames table has the given column
    /// (legacy-database migration guard, runs before any ALTER). Must use
    /// <c>pragma_table_xinfo</c> — plain <c>table_info</c> omits generated
    /// (hidden) columns, which would re-trigger the ALTER on every open.</summary>
    private async Task<bool> HasFrameColumnAsync(string columnName, CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_xinfo('frames') WHERE name=$name;";
        command.Parameters.AddWithValue("$name", columnName);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return (long)result! > 0;
    }

    private async Task ExecuteAsync(string sql, IReadOnlyList<SqliteParameter>? parameters, CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var parameter in parameters)
                command.Parameters.Add(parameter);
        }
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<long> QueryInt64Async(string sql, CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return (long)result!;
    }

    private async Task<TraceCacheSummary?> QuerySingleSummaryAsync(
        string sql, IReadOnlyList<SqliteParameter> parameters, CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.Add(parameter);
        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        return new TraceCacheSummary(
            reader.GetInt64(reader.GetOrdinal("trace_id")),
            reader.GetString(reader.GetOrdinal("source_name")),
            reader.GetInt64(reader.GetOrdinal("file_size")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("imported_at")), CultureInfo.InvariantCulture),
            reader.GetInt64(reader.GetOrdinal("frame_count")),
            reader.GetDouble(reader.GetOrdinal("duration")),
            reader.GetInt64(reader.GetOrdinal("complete")) != 0,
            reader.GetDouble(reader.GetOrdinal("last_position_seconds")));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
