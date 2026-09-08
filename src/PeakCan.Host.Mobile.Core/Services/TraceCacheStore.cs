using System.Globalization;
using Microsoft.Data.Sqlite;
using PeakCan.Host.Mobile.Core.Models;

namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>
/// SQLite-backed replay cache. The spec schema is extended with
/// <c>last_position_seconds</c> so the recent-file list can show playback position.
/// </summary>
public sealed class TraceCacheStore : ITraceCacheStore
{
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
            await ExecuteAsync("""
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
                  PRIMARY KEY (trace_id, idx)
                ) WITHOUT ROWID;

                CREATE INDEX IF NOT EXISTS idx_frames_ts ON frames(trace_id, timestamp);
                CREATE INDEX IF NOT EXISTS idx_frames_id ON frames(trace_id, can_id);
                """, ct).ConfigureAwait(false);
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
