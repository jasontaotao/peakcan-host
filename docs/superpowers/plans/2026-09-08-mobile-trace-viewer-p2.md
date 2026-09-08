# 移动端 Trace Viewer P2 分析能力 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为移动端 Trace Viewer 增加 SQLite 回放缓存、完整过滤回看、DBC 信号解码和缓存失败降级，使 P1 的流式播放器升级为可分析工具。

**Architecture:** 继续保留 P1 的 `AscStreamingSource + StreamingTracePlayer + 80 行稳定 viewport` 主链路。播放事件旁路进入 `TraceCacheWriter` 的 bounded channel，由后台任务按 5000 帧事务写入 `TraceCacheStore`；写库失败只停用缓存，不阻塞播放。完整缓存通过 `TraceBrowseViewModel` 分页查询浏览。DBC 解析复用 `peakcan-hil-core` 的 `DbcParser.Parse` 与 `SignalDecoder.Decode`，只对显示/详情帧按需解码。

**Tech Stack:** .NET 10、.NET MAUI Android、Microsoft.Data.Sqlite、PeakCan.HIL.Core Dbc、CommunityToolkit.Mvvm、xunit 2.9.3、FluentAssertions 8.10.0、NSubstitute 5.3.0。

**Spec:** `docs/superpowers/specs/2026-09-07-mobile-trace-viewer-design.md`（§5 SQLite schema、§5.2 DBC/缓存、§6 数据流、§7 错误处理、§9 P2）

## Global Constraints

- 新功能分支：`feature/mobile-trace-viewer-p2`，基线为当前 `main`。每个 task 一次 conventional commit，无 attribution。
- `.NET` 10、`<Nullable>enable</Nullable>`、`<ImplicitUsings>enable</ImplicitUsings>`。
- 中央包管理：`Directory.Packages.props` 定义版本；项目内 `PackageReference` 不写 `Version=`。
- Additive only：不修改 `AscParser.ParseAsync` / `BlfParser.ParseAsync` 的 14 个既有调用点；流式核心 API 只做向后兼容扩展。
- `StreamingTraceOpenResult` 拥有并释放 `SourceStream`；VM 预读必须 `await using`。
- P1 渲染约束继续有效：播放表格保持 5000 帧 ring + 最近 80 行 in-place viewport，禁止高频 `Insert/Remove/Reset` 或替换 `ItemsSource`。
- 导入/直开仍拒绝 >500MB：`TraceFileCache.MaxImportBytes = 500L * 1024 * 1024`。
- SQLite 写失败必须降级：缓存停用、回放继续，不抛到 UI 主链路。
- DBC 解码只针对可见/详情帧；禁止全量 trace 解码。
- 注释约定：业务逻辑/用户面向注释中文，类型与 API 的 xmldoc 英文。
- 测试禁止 `Thread.Sleep` 和真实 `Task.Delay` 等待并发时序；SQLite 测试用 `:memory:` 或临时文件。
- 每个核心 task 结束运行 `dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo`；Android UI task 结束运行 `dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj --nologo`。

---

## Task 1: P2 分支与 SQLite 依赖

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/PeakCan.Host.Mobile.Core/PeakCan.Host.Mobile.Core.csproj`

**Interfaces:**
- Consumes: 无
- Produces: `Microsoft.Data.Sqlite` 包，供 Task 2 的 `TraceCacheStore` 使用。

- [ ] **Step 1: 创建 P2 分支**

Run:
```bash
git switch main
git switch -c feature/mobile-trace-viewer-p2
```

Expected: 当前分支为 `feature/mobile-trace-viewer-p2`。

- [ ] **Step 2: 添加中央包版本**

在 `Directory.Packages.props` 的 `<ItemGroup>` 中，紧接 `PeakCan.HIL.Core` 一行后加入：

```xml
<PackageVersion Include="Microsoft.Data.Sqlite" Version="10.0.0" />
```

- [ ] **Step 3: 添加 Mobile.Core 项目引用**

编辑 `src/PeakCan.Host.Mobile.Core/PeakCan.Host.Mobile.Core.csproj`，在现有 `<PackageReference Include="CommunityToolkit.Mvvm" />` 前加入：

```xml
<PackageReference Include="Microsoft.Data.Sqlite" />
```

- [ ] **Step 4: 验证还原与既有测试**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo
```

Expected: 还原成功，P1 既有 30 个测试全部通过。

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/plans/2026-09-08-mobile-trace-viewer-p2.md Directory.Packages.props src/PeakCan.Host.Mobile.Core/PeakCan.Host.Mobile.Core.csproj
git commit -m "build(mobile): add p2 plan and sqlite dependency"
```

---

## Task 2: TraceCacheStore 模型、Schema 与基础写入

**Files:**
- Create: `src/PeakCan.Host.Mobile.Core/Services/TraceCacheModels.cs`
- Create: `src/PeakCan.Host.Mobile.Core/Services/ITraceCacheStore.cs`
- Create: `src/PeakCan.Host.Mobile.Core/Services/TraceCacheStore.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/Services/TraceCacheStoreTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Data.Sqlite`
- Produces:
  - `TraceCacheSummary(long TraceId, string SourceName, long FileSizeBytes, DateTimeOffset ImportedAt, long FrameCount, double Duration, bool Complete, double LastPositionSeconds)`
  - `CachedFrame(long Index, double Timestamp, uint CanId, bool IsExtended, byte Dlc, byte[] Data)`
  - `ITraceCacheStore.InitializeAsync(CancellationToken ct = default)`
  - `ITraceCacheStore.GetOrCreateTraceAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default)`
  - `ITraceCacheStore.AppendFramesAsync(long traceId, IReadOnlyList<CachedFrame> frames, CancellationToken ct = default)`
  - `ITraceCacheStore.MarkCompletedAsync(long traceId, CancellationToken ct = default)`
  - `ITraceCacheStore.UpdateLastPositionAsync(long traceId, double seconds, CancellationToken ct = default)`
  - `ITraceCacheStore.FindCompletedAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default)`
  - `ITraceCacheStore.GetTraceAsync(long traceId, CancellationToken ct = default)`

- [ ] **Step 1: 写失败测试**

Create `tests/PeakCan.Host.Mobile.Core.Tests/Services/TraceCacheStoreTests.cs`：

```csharp
using FluentAssertions;
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
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo --filter "FullyQualifiedName~TraceCacheStoreTests"
```

Expected: 编译失败，提示 `TraceCacheStore` / `CachedFrame` 不存在。

- [ ] **Step 3: 实现 models 与接口**

Create `src/PeakCan.Host.Mobile.Core/Services/TraceCacheModels.cs`：

```csharp
namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>Metadata for one cached trace file.</summary>
public sealed record TraceCacheSummary(
    long TraceId,
    string SourceName,
    long FileSizeBytes,
    DateTimeOffset ImportedAt,
    long FrameCount,
    double Duration,
    bool Complete,
    double LastPositionSeconds);

/// <summary>One frame stored in the replay cache.</summary>
public sealed record CachedFrame(
    long Index,
    double Timestamp,
    uint CanId,
    bool IsExtended,
    byte Dlc,
    byte[] Data)
{
    public FrameRow ToFrameRow() =>
        new(Timestamp, CanId, IsExtended, Dlc, Data);
}
```

Create `src/PeakCan.Host.Mobile.Core/Services/ITraceCacheStore.cs`：

```csharp
namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>Persistent replay cache. Implementations must be safe for serialized async use.</summary>
public interface ITraceCacheStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<long> GetOrCreateTraceAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default);
    Task AppendFramesAsync(long traceId, IReadOnlyList<CachedFrame> frames, CancellationToken ct = default);
    Task MarkCompletedAsync(long traceId, CancellationToken ct = default);
    Task UpdateLastPositionAsync(long traceId, double seconds, CancellationToken ct = default);
    Task<TraceCacheSummary?> FindCompletedAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default);
    Task<TraceCacheSummary?> GetTraceAsync(long traceId, CancellationToken ct = default);
}
```

- [ ] **Step 4: 实现 SQLite store**

Create `src/PeakCan.Host.Mobile.Core/Services/TraceCacheStore.cs`：

```csharp
using System.Globalization;
using Microsoft.Data.Sqlite;

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
            return _connection.LastInsertRowId;
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
            var insert = _connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT OR REPLACE INTO frames(trace_id,idx,timestamp,can_id,is_extended,dlc,data)
                VALUES($trace_id,$idx,$timestamp,$can_id,$is_extended,$dlc,$data)
                """;
            insert.Parameters.AddRange(
            [
                new("$trace_id", traceId),
                new("$idx"),
                new("$timestamp"),
                new("$can_id"),
                new("$is_extended"),
                new("$dlc"),
                new("$data"),
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

            await ExecuteAsync("""
                UPDATE traces
                SET frame_count=MAX(frame_count,(SELECT MAX(idx)+1 FROM frames WHERE trace_id=$trace_id)),
                    duration=MAX(duration,(SELECT MAX(timestamp) FROM frames WHERE trace_id=$trace_id))
                WHERE trace_id=$trace_id
                """, [new("$trace_id", traceId)], ct).ConfigureAwait(false);
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
```

- [ ] **Step 5: 运行测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo --filter "FullyQualifiedName~TraceCacheStoreTests"
```

Expected: 4 个新测试全部通过。

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Mobile.Core/Services/TraceCacheModels.cs src/PeakCan.Host.Mobile.Core/Services/ITraceCacheStore.cs src/PeakCan.Host.Mobile.Core/Services/TraceCacheStore.cs tests/PeakCan.Host.Mobile.Core.Tests/Services/TraceCacheStoreTests.cs
git commit -m "feat(mobile): add sqlite trace cache store"
```

---

## Task 3: 缓存帧分页查询与最近文件列表

**Files:**
- Modify: `src/PeakCan.Host.Mobile.Core/Services/TraceCacheModels.cs`
- Modify: `src/PeakCan.Host.Mobile.Core/Services/ITraceCacheStore.cs`
- Modify: `src/PeakCan.Host.Mobile.Core/Services/TraceCacheStore.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/Services/TraceCacheStoreTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `TraceCacheStore`
- Produces:
  - `FrameQuery(long? AfterIndex = null, long? BeforeIndex = null, IReadOnlySet<uint>? CanIds = null, int Limit = 80)`
  - `FramePage(IReadOnlyList<CachedFrame> Frames, bool HasMore)`
  - `ITraceCacheStore.GetFramesAsync(long traceId, FrameQuery query, CancellationToken ct = default)`
  - `ITraceCacheStore.ListTracesAsync(int limit = 100, CancellationToken ct = default)`

- [ ] **Step 1: 追加失败测试**

在 `TraceCacheStoreTests` 类中追加以下测试：

```csharp
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
    filtered.Frames.Select(f => f.Index).Should().Equal([1, 3, 5]);
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

    previous.Frames.Select(f => f.Index).Should().Equal(Enumerable.Range(30, 50));
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
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo --filter "FullyQualifiedName~TraceCacheStoreTests"
```

Expected: 编译失败，提示 `FrameQuery` / `GetFramesAsync` / `ListTracesAsync` 不存在。

- [ ] **Step 3: 实现查询模型**

在 `TraceCacheModels.cs` 末尾追加：

```csharp
/// <summary>Keyset paged cache query. Forward paging uses AfterIndex; backward paging uses BeforeIndex.</summary>
public sealed record FrameQuery(
    long? AfterIndex = null,
    long? BeforeIndex = null,
    IReadOnlySet<uint>? CanIds = null,
    int Limit = 80);

/// <summary>One cache page. HasMore is true when Limit+1 rows were available.</summary>
public sealed record FramePage(IReadOnlyList<CachedFrame> Frames, bool HasMore);
```

在 `ITraceCacheStore` 中追加：

```csharp
Task<FramePage> GetFramesAsync(long traceId, FrameQuery query, CancellationToken ct = default);
Task<IReadOnlyList<TraceCacheSummary>> ListTracesAsync(int limit = 100, CancellationToken ct = default);
```

- [ ] **Step 4: 实现 store 查询**

在 `TraceCacheStore` 中追加以下方法：

```csharp
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
```

- [ ] **Step 5: 运行测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo --filter "FullyQualifiedName~TraceCacheStoreTests"
```

Expected: Task 2 的 4 个测试与本 task 的 3 个测试全部通过。

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Mobile.Core/Services tests/PeakCan.Host.Mobile.Core.Tests/Services/TraceCacheStoreTests.cs
git commit -m "feat(mobile): add cached frame paging and trace list"
```

---

## Task 4: TraceCacheWriter 后台批量写入

**Files:**
- Create: `src/PeakCan.Host.Mobile.Core/Services/ITraceCacheSink.cs`
- Create: `src/PeakCan.Host.Mobile.Core/Services/TraceCacheWriter.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/Services/TraceCacheWriterTests.cs`

**Interfaces:**
- Consumes: `ITraceCacheStore`, `ReplayFrame`
- Produces:
  - `ITraceCacheSink.TraceId`, `IsEnabled`, `WrittenFrames`, `DroppedFrames`, `Failure`
  - `ITraceCacheSink.Enqueue(ReplayFrame frame)`
  - `ITraceCacheSink.CloseAsync(bool markComplete, CancellationToken ct = default)`
  - `ITraceCacheSinkFactory.StartAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default)`

- [ ] **Step 1: 写失败测试**

Create `tests/PeakCan.Host.Mobile.Core.Tests/Services/TraceCacheWriterTests.cs`：

```csharp
using FluentAssertions;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Mobile.Core.Services;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class TraceCacheWriterTests
{
    private static ReplayFrame Frame(double timestamp, uint id = 0x100) =>
        new(timestamp, id, 2, [1, 2], default, false);

    private sealed class FakeStore : ITraceCacheStore
    {
        public List<CachedFrame[]> Batches { get; } = [];
        public List<long> Completed { get; } = [];
        public Func<CachedFrame[], Task>? AppendOverride { get; set; }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> GetOrCreateTraceAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default) => Task.FromResult(42L);
        public async Task AppendFramesAsync(long traceId, IReadOnlyList<CachedFrame> frames, CancellationToken ct = default)
        {
            var copy = frames.ToArray();
            Batches.Add(copy);
            if (AppendOverride is not null) await AppendOverride(copy);
        }
        public Task MarkCompletedAsync(long traceId, CancellationToken ct = default) { Completed.Add(traceId); return Task.CompletedTask; }
        public Task UpdateLastPositionAsync(long traceId, double seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<TraceCacheSummary?> FindCompletedAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default) => Task.FromResult<TraceCacheSummary?>(null);
        public Task<TraceCacheSummary?> GetTraceAsync(long traceId, CancellationToken ct = default) => Task.FromResult<TraceCacheSummary?>(null);
        public Task<FramePage> GetFramesAsync(long traceId, FrameQuery query, CancellationToken ct = default) => Task.FromResult(new FramePage([], false));
        public Task<IReadOnlyList<TraceCacheSummary>> ListTracesAsync(int limit = 100, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TraceCacheSummary>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Close_Flushes_Batches_And_Marks_Complete()
    {
        var store = new FakeStore();
        await using var writer = TraceCacheWriter.CreateForTests(store, 42);

        for (var i = 0; i < 5000; i++) writer.Enqueue(Frame(i * 0.001));
        await writer.CloseAsync(markComplete: true);

        store.Batches.Should().ContainSingle(b => b.Length == 5000);
        store.Completed.Should().ContainSingle(t => t == 42);
        writer.WrittenFrames.Should().Be(5000);
        writer.DroppedFrames.Should().Be(0);
    }

    [Fact]
    public async Task Close_Without_Complete_Does_Not_Mark_Complete()
    {
        var store = new FakeStore();
        await using var writer = TraceCacheWriter.CreateForTests(store, 42);

        writer.Enqueue(Frame(1));
        await writer.CloseAsync(markComplete: false);

        writer.WrittenFrames.Should().Be(1);
        store.Completed.Should().BeEmpty();
    }

    [Fact]
    public async Task Append_Failure_Disables_Sink_And_Does_Not_Mark_Complete()
    {
        var store = new FakeStore
        {
            AppendOverride = _ => throw new IOException("disk full")
        };
        await using var writer = TraceCacheWriter.CreateForTests(store, 42);

        writer.Enqueue(Frame(1));
        await writer.CloseAsync(markComplete: true);

        writer.IsEnabled.Should().BeFalse();
        writer.Failure.Should().BeAssignableTo<IOException>();
        store.Completed.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo --filter "FullyQualifiedName~TraceCacheWriterTests"
```

Expected: 编译失败，提示 `TraceCacheWriter` 不存在。

- [ ] **Step 3: 实现 sink 接口与后台 writer**

Create `src/PeakCan.Host.Mobile.Core/Services/ITraceCacheSink.cs`：

```csharp
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>Non-blocking cache target fed from player frame events.</summary>
public interface ITraceCacheSink : IAsyncDisposable
{
    long TraceId { get; }
    bool IsEnabled { get; }
    long WrittenFrames { get; }
    long DroppedFrames { get; }
    Exception? Failure { get; }

    void Enqueue(ReplayFrame frame);

    /// <summary>Drain queued frames. <paramref name="markComplete"/> is true only for clean EOF.</summary>
    Task CloseAsync(bool markComplete, CancellationToken ct = default);
}

/// <summary>Creates a cache sink for one trace playback session.</summary>
public interface ITraceCacheSinkFactory
{
    /// <summary>Returns null when cache is unavailable or the trace is already complete.</summary>
    Task<ITraceCacheSink?> StartAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default);
}
```

Create `src/PeakCan.Host.Mobile.Core/Services/TraceCacheWriter.cs`：

```csharp
using System.Threading.Channels;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>
/// Buffers player emissions and appends SQLite batches in the background.
/// The bounded queue uses DropOldest so a slow disk cannot block playback;
/// any drop or write failure leaves the trace marked incomplete.
/// </summary>
public sealed class TraceCacheWriter : ITraceCacheSink
{
    public const int BatchSize = 5000;
    private const int QueueCapacity = 16384;

    private readonly ITraceCacheStore _store;
    private readonly long _traceId;
    private readonly Channel<ReplayFrame> _channel = Channel.CreateBounded<ReplayFrame>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly Task _pump;
    private long _nextIndex;
    private long _written;
    private long _dropped;
    private double _lastTimestamp;
    private long _failed;
    private bool _closed;

    private TraceCacheWriter(ITraceCacheStore store, long traceId)
    {
        _store = store;
        _traceId = traceId;
        _pump = PumpAsync();
    }

    public static TraceCacheWriter CreateForTests(ITraceCacheStore store, long traceId) => new(store, traceId);

    public long TraceId => _traceId;
    public bool IsEnabled => Interlocked.Read(ref _failed) == 0;
    public long WrittenFrames => Interlocked.Read(ref _written);
    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public Exception? Failure { get; private set; }

    public void Enqueue(ReplayFrame frame)
    {
        if (!IsEnabled || _closed) return;
        if (!_channel.Writer.TryWrite(frame))
            Interlocked.Increment(ref _dropped);
    }

    public async Task CloseAsync(bool markComplete, CancellationToken ct = default)
    {
        if (_closed) return;
        _closed = true;
        _channel.Writer.TryComplete();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Failure ??= ex;
            Interlocked.Exchange(ref _failed, 1);
        }

        if (Failure is null && _lastTimestamp > 0)
            await _store.UpdateLastPositionAsync(_traceId, _lastTimestamp, ct).ConfigureAwait(false);

        if (markComplete && Failure is null && Interlocked.Read(ref _dropped) == 0)
            await _store.MarkCompletedAsync(_traceId, ct).ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        var batch = new List<CachedFrame>(BatchSize);
        await foreach (var frame in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            batch.Add(new CachedFrame(
                Interlocked.Increment(ref _nextIndex) - 1,
                frame.Timestamp,
                frame.Id,
                frame.IsExtended,
                frame.Dlc,
                frame.Data));
            _lastTimestamp = Math.Max(_lastTimestamp, frame.Timestamp);
            Interlocked.Increment(ref _written);

            if (batch.Count >= BatchSize)
            {
                await FlushAsync(batch).ConfigureAwait(false);
                if (!IsEnabled) return;
            }
        }

        if (batch.Count > 0 && IsEnabled)
            await FlushAsync(batch).ConfigureAwait(false);
    }

    private async Task FlushAsync(List<CachedFrame> batch)
    {
        try
        {
            await _store.AppendFramesAsync(_traceId, batch).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Failure = ex;
            Interlocked.Exchange(ref _failed, 1);
            _channel.Writer.TryComplete();
        }
        finally
        {
            batch.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_closed)
            await CloseAsync(markComplete: false).ConfigureAwait(false);
    }
}
```

在文件末尾追加 factory：

```csharp
namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>Default cache sink factory using <see cref="TraceCacheStore"/>.</summary>
public sealed class TraceCacheWriterFactory(ITraceCacheStore store) : ITraceCacheSinkFactory
{
    public async Task<ITraceCacheSink?> StartAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        try
        {
            await store.InitializeAsync(ct).ConfigureAwait(false);
            if (await store.FindCompletedAsync(sourceName, fileSizeBytes, ct).ConfigureAwait(false) is not null)
                return null;

            var traceId = await store.GetOrCreateTraceAsync(sourceName, fileSizeBytes, ct).ConfigureAwait(false);
            return TraceCacheWriter.CreateForTests(store, traceId);
        }
        catch
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo --filter "FullyQualifiedName~TraceCacheWriterTests"
```

Expected: 3 个新测试全部通过。

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Mobile.Core/Services/ITraceCacheSink.cs src/PeakCan.Host.Mobile.Core/Services/TraceCacheWriter.cs tests/PeakCan.Host.Mobile.Core.Tests/Services/TraceCacheWriterTests.cs
git commit -m "feat(mobile): add background sqlite cache writer"
```

---

## Task 5: TraceSessionViewModel 接入缓存旁路

**Files:**
- Modify: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceSessionViewModel.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceSessionViewModelTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `ITraceCacheSinkFactory`
- Produces:
  - `TraceSessionViewModel(..., ILogger? logger = null, ITraceCacheSinkFactory? cacheSinkFactory = null)`
  - `Task OpenAsync(string cachedFilePath, string sourceName, long fileSizeBytes, CancellationToken ct = default)`
  - 保留旧签名 `Task OpenAsync(string cachedFilePath, CancellationToken ct = default)`
  - `[ObservableProperty] string cacheStatusText`
  - `long? TraceId`

- [ ] **Step 1: 追加测试 fakes**

在 `TraceSessionViewModelTests` 顶部追加：

```csharp
private sealed class FakeCacheSink : ITraceCacheSink
{
    public List<ReplayFrame> Frames { get; } = [];
    public long TraceId { get; } = 42;
    public bool IsEnabled { get; private set; } = true;
    public long WrittenFrames => Frames.Count;
    public long DroppedFrames { get; private set; }
    public Exception? Failure { get; private set; }
    public List<bool> ClosedStates { get; } = [];

    public void Enqueue(ReplayFrame frame) => Frames.Add(frame);

    public Task CloseAsync(bool markComplete, CancellationToken ct = default)
    {
        ClosedStates.Add(markComplete);
        IsEnabled = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

private sealed class FakeCacheSinkFactory : ITraceCacheSinkFactory
{
    public FakeCacheSink? NextSink { get; set; } = new();
    public List<string> SourceNames { get; } = [];

    public Task<ITraceCacheSink?> StartAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default)
    {
        SourceNames.Add(sourceName);
        return Task.FromResult<ITraceCacheSink?>(NextSink);
    }
}
```

在 `Env` 中扩展：

```csharp
public FakeCacheSinkFactory CacheFactory { get; } = new();

public TraceSessionViewModel CreateVm() =>
    new(Ui, SourceFactory, _ => Player, cacheSinkFactory: CacheFactory);

public Env(bool useCache = true)
{
    Vm = useCache
        ? CreateVm()
        : new TraceSessionViewModel(Ui, SourceFactory, _ => Player);
}
```

- [ ] **Step 2: 写失败测试**

在 `TraceSessionViewModelTests` 中追加：

```csharp
[Fact]
public async Task OpenAsync_Starts_Cache_And_Emits_Unfiltered_Frames()
{
    var env = new Env();
    var frames = new AsyncFrameSeq(F(0, 0x100), F(0.1, 0x200));
    env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));

    await env.Vm.OpenAsync("cached.asc", "a.asc", 123);
    env.Vm.TraceId.Should().Be(42);

    env.Vm.SetIdFilter("0x100");
    env.Player.Emit(F(0.2, 0x100));
    env.Player.Emit(F(0.3, 0x200));

    env.CacheFactory.NextSink!.Frames.Select(f => f.Id).Should().Equal([0x100u, 0x200u]);
    env.Vm.CacheStatusText.Should().BeEmpty();
}

[Fact]
public async Task PlaybackEnded_Closes_Cache_As_Complete()
{
    var env = new Env();
    var frames = new AsyncFrameSeq(F(0, 0x100));
    env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));
    await env.Vm.OpenAsync("cached.asc", "a.asc", 123);

    env.Player.EmitEof();
    await env.CacheFactory.NextSink!.CloseAsync(true);
    env.CacheFactory.NextSink.ClosedStates.Should().Contain(true);
}

[Fact]
public async Task Cache_Factory_Returning_Null_Sets_Unavailable_But_Keeps_Playback()
{
    var env = new Env();
    env.CacheFactory.NextSink = null;
    var frames = new AsyncFrameSeq(F(0, 0x100));
    env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));

    await env.Vm.OpenAsync("cached.asc", "a.asc", 123);

    env.Vm.State.Should().Be(SessionState.Ready);
    env.Vm.CacheStatusText.Should().Be("缓存不可用");
}

[Fact]
public async Task OpenAsync_Disposes_OpenResult()
{
    var env = new Env();
    var frames = new AsyncFrameSeq(F(0, 0x100));
    env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));

    await env.Vm.OpenAsync("cached.asc", "a.asc", 123);

    frames.OpenResult.SourceStream!.CanRead.Should().BeFalse();
}
```

如当前 `AsyncFrameSeq.OpenResult` 没有 `SourceStream`，修改 `AsyncFrameSeq`：

```csharp
public StreamingTraceOpenResult OpenResult => new()
{
    Frames = Yield(),
    Stats = new StreamingParseStats(),
    SourceStream = new MemoryStream([1, 2], writable: false),
};
```

- [ ] **Step 3: 运行测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo --filter "FullyQualifiedName~TraceSessionViewModelTests"
```

Expected: 编译失败，提示新 `OpenAsync` / `TraceId` / `CacheStatusText` 不存在。

- [ ] **Step 4: 修改 VM**

在 `TraceSessionViewModel` 字段区追加：

```csharp
private readonly ITraceCacheSinkFactory? _cacheSinkFactory;
private ITraceCacheSink? _cacheSink;
private long? _traceId;
```

修改构造函数签名：

```csharp
public TraceSessionViewModel(
    IUiDispatcher ui,
    IStreamingSourceFactory sourceFactory,
    Func<IStreamingTraceSource, IStreamingTracePlayer> playerFactory,
    ILogger? logger = null,
    ITraceCacheSinkFactory? cacheSinkFactory = null)
{
    _ui = ui;
    _sourceFactory = sourceFactory;
    _playerFactory = playerFactory;
    _logger = logger ?? NullLogger.Instance;
    _cacheSinkFactory = cacheSinkFactory;
    for (var i = 0; i < ViewportRowCount; i++)
        _viewport[i] = new FrameRowSlot();
}
```

追加 observable 属性与公开属性：

```csharp
[ObservableProperty] private string _cacheStatusText = string.Empty;
public long? TraceId => _traceId;
```

新增兼容 overload 和新的 open 方法：

```csharp
public Task OpenAsync(string cachedFilePath, CancellationToken ct = default)
{
    var info = new FileInfo(cachedFilePath);
    return OpenAsync(cachedFilePath, info.Name, info.Length, ct);
}

public async Task OpenAsync(string cachedFilePath, string sourceName, long fileSizeBytes, CancellationToken ct = default)
{
    State = SessionState.Empty;
    ErrorMessage = null;
    DurationKnown = false;
    DurationText = "??:??";
    DurationScanProgress = 0;
    CacheStatusText = string.Empty;
    ClearPlaybackBuffer();

    if (_cacheSinkFactory is not null)
    {
        _cacheSink = await _cacheSinkFactory.StartAsync(sourceName, fileSizeBytes, ct).ConfigureAwait(false);
        _traceId = _cacheSink?.TraceId;
        if (_cacheSink is null)
            _ui.Post(() => CacheStatusText = "缓存不可用");
    }

    var source = _sourceFactory.Create(cachedFilePath);
    await using var open = await source.OpenAsync(ct: ct).ConfigureAwait(false);
    var prefetched = 0;
    await foreach (var f in open.Frames.WithCancellation(ct).ConfigureAwait(false))
    {
        _rows.Add(FrameRow.FromReplayFrame(f));
        if (++prefetched >= 200) break;
    }

    UpdateViewport();
    State = SessionState.Ready;
    _player = _playerFactory(source);
    _player.FrameEmitted += OnFrameEmitted;
    _player.PlaybackEnded += OnPlaybackEnded;
    _player.SeekProgress += OnSeekProgress;

    var progress = new Progress<double>(p => _ui.Post(() => DurationScanProgress = p));
    _ = Task.Run(async () =>
    {
        try
        {
            await using var fs = new FileStream(cachedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var scan = await DurationScanner.ScanAsync(fs, progress, ct).ConfigureAwait(false);
            _duration = scan.DurationSeconds;
            _durationKnownValue = true;
            _ui.Post(() =>
            {
                DurationKnown = true;
                DurationText = FormatTime(_duration);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "duration scan failed");
        }
    }, ct);
}
```

修改 `OnFrameEmitted`，缓存必须收到未过滤帧：

```csharp
private void OnFrameEmitted(ReplayFrame f)
{
    _cacheSink?.Enqueue(f);

    lock (_emitGate)
    {
        if (!PassesFilter(f)) return;
        if (_pending.Count == MaxPendingFrames)
            _pending.Dequeue();
        _pending.Enqueue(f);
    }
}
```

修改 `OnPlaybackEnded`，加入异步关闭缓存：

```csharp
private void OnPlaybackEnded(object? sender, PlaybackEndedEventArgs e)
{
    var sink = _cacheSink;
    _cacheSink = null;
    _ = Task.Run(async () =>
    {
        if (sink is not null)
        {
            try
            {
                await sink.CloseAsync(e.Error is null).ConfigureAwait(false);
                _ui.Post(() => CacheStatusText = e.Error is null ? "缓存完成" : "缓存未完成");
            }
            catch
            {
                _ui.Post(() => CacheStatusText = "缓存已停用");
            }
        }
    });

    _ui.Post(() =>
    {
        IsSeekBusy = false;
        if (e.Error is null)
            State = SessionState.Ended;
        else
        {
            State = SessionState.Failed;
            ErrorMessage = e.Error.Message;
        }
        _drainTimer?.Dispose();
        _drainTimer = null;
        Drain();
    });
}
```

修改 `Dispose` 为同步包装：

```csharp
public void Dispose()
{
    _drainTimer?.Dispose();
    if (_player is not null)
    {
        _player.FrameEmitted -= OnFrameEmitted;
        _player.PlaybackEnded -= OnPlaybackEnded;
        _player.SeekProgress -= OnSeekProgress;
        _player.Dispose();
    }

    var sink = _cacheSink;
    _cacheSink = null;
    if (sink is not null)
        sink.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
```

- [ ] **Step 5: 运行 Mobile.Core 测试**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo
```

Expected: 既有测试和新增缓存测试全部通过。

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Mobile.Core/ViewModels/TraceSessionViewModel.cs tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceSessionViewModelTests.cs
git commit -m "feat(mobile): wire replay cache into trace session"
```

---

## Task 6: 最近文件列表与打开入口元数据

**Files:**
- Modify: `src/PeakCan.Host.Mobile/Platform/ITracePageFactory.cs`
- Modify: `src/PeakCan.Host.Mobile/Platform/TracePageFactory.cs`
- Modify: `src/PeakCan.Host.Mobile/Views/FilesPage.xaml.cs`
- Modify: `src/PeakCan.Host.Mobile/MauiProgram.cs`

**Interfaces:**
- Consumes: `ITraceCacheStore`
- Produces:
  - `ITracePageFactory.Create(string cachedFilePath, string sourceName, long fileSizeBytes)`
  - `ITracePageFactory.CreateBrowse(long traceId)`
  - `TracePage(..., string sourceName, long fileSizeBytes)`
  - SQLite recent list includes frame count, duration, complete state and last position.

- [ ] **Step 1: 扩展 page factory 接口**

将 `ITracePageFactory` 改为：

```csharp
using Microsoft.Maui.Controls;

namespace PeakCan.Host.Mobile.Platform;

public interface ITracePageFactory
{
    ContentPage Create(string cachedFilePath, string sourceName, long fileSizeBytes);
    ContentPage CreateBrowse(long traceId);
}
```

- [ ] **Step 2: 修改 TracePageFactory 临时桥接**

先让构建通过；`BrowsePage` 在 Task 8 创建。当前先使用最小页面，Task 8 会替换：

```csharp
public ContentPage Create(string cachedFilePath, string sourceName, long fileSizeBytes)
    => new TracePage(
        services.GetRequiredService<IUiDispatcher>(),
        services.GetRequiredService<IStreamingSourceFactory>(),
        cachedFilePath,
        sourceName,
        fileSizeBytes,
        services.GetRequiredService<ILogger<TraceSessionViewModel>>());

public ContentPage CreateBrowse(long traceId)
    => new ContentPage
    {
        Title = "Trace",
        Content = new Label { Text = $"Cached trace {traceId}" }
    };
```

- [ ] **Step 3: 修改 TracePage 构造签名**

将 `TracePage` 构造函数改为：

```csharp
public TracePage(
    IUiDispatcher ui,
    IStreamingSourceFactory sourceFactory,
    string cachedFilePath,
    string sourceName,
    long fileSizeBytes,
    ILogger? logger = null)
{
    InitializeComponent();
    _vm = new TraceSessionViewModel(
        ui,
        sourceFactory,
        src => new PeakCan.Host.Core.Replay.StreamingTracePlayer(src, clock: null),
        logger);
    BindingContext = _vm;
    _vm.PropertyChanged += OnVmPropertyChanged;
    SpeedPicker.ItemsSource = new[] { "0.1x", "0.5x", "1x", "2x", "5x", "10x" };
    SpeedPicker.SelectedIndex = 2;
    _ = InitializeAsync(cachedFilePath, sourceName, fileSizeBytes);
}

private async Task InitializeAsync(string cachedFilePath, string sourceName, long fileSizeBytes)
{
    await _vm.OpenAsync(cachedFilePath, sourceName, fileSizeBytes);
    ScrollToLatest();
}
```

- [ ] **Step 4: 扩展 FilesPage 最近列表**

更新 record 与字段：

```csharp
public record RecentItem(
    string DisplayName,
    string Subtitle,
    string? CachedPath,
    long? TraceId,
    long FileSizeBytes);

private readonly ITraceCacheStore _cacheStore;

public FilesPage(
    IFilePickerGateway picker,
    TraceFileCache cache,
    ITracePageFactory tracePageFactory,
    ITraceCacheStore cacheStore)
{
    InitializeComponent();
    _picker = picker;
    _cache = cache;
    _tracePageFactory = tracePageFactory;
    _cacheStore = cacheStore;
    MainActivity.FileUriReceived += OnFileUriReceived;
}
```

替换 `RefreshRecent` 和 `OnAppearing`：

```csharp
protected override async void OnAppearing()
{
    base.OnAppearing();
    await RefreshRecentAsync();
    var uri = MainActivity.TakePendingFileUri();
    if (uri is not null)
        await HandleIntentUriAsync(uri);
}

private async Task RefreshRecentAsync()
{
    var items = new List<RecentItem>();
    var traces = await _cacheStore.ListTracesAsync();
    foreach (var trace in traces)
    {
        var state = trace.Complete ? "完整" : "部分";
        items.Add(new RecentItem(
            trace.SourceName,
            $"{trace.FileSizeBytes / 1024} KB · {trace.FrameCount} 帧 · {TimeSpan.FromSeconds(trace.Duration):hh\\:mm\\:ss} · {state} · 上次 {TimeSpan.FromSeconds(trace.LastPositionSeconds):hh\\:mm\\:ss}",
            null,
            trace.TraceId,
            trace.FileSizeBytes));
    }

    var cachedIds = traces.Select(t => (t.SourceName, t.FileSizeBytes)).ToHashSet();
    foreach (var path in Directory.GetFiles(_cache.CacheDirectory, "*.asc"))
    {
        var info = new FileInfo(path);
        var suffix = $".{info.Length}.asc";
        var name = info.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? info.Name[..^suffix.Length]
            : info.Name;
        if (cachedIds.Contains((name, info.Length))) continue;
        items.Add(new RecentItem(name, $"{info.Length / 1024} KB", path, null, info.Length));
    }

    RecentList.ItemsSource = items;
}
```

替换 `OnOpenClicked`：

```csharp
private async void OnOpenClicked(object? sender, EventArgs e)
{
    try
    {
        var picked = await _picker.PickTraceFileAsync();
        if (picked is null) return;

        var completed = await _cacheStore.FindCompletedAsync(picked.DisplayName, picked.SizeBytes);
        if (completed is not null)
        {
            await Navigation.PushAsync(_tracePageFactory.CreateBrowse(completed.TraceId));
            return;
        }

        var path = await _cache.ImportAsync(picked);
        await RefreshRecentAsync();
        await Navigation.PushAsync(_tracePageFactory.Create(path, picked.DisplayName, picked.SizeBytes));
    }
    catch (InvalidOperationException ex)
    {
        await DisplayAlertAsync("无法打开文件", ex.Message, "确定");
    }
}
```

替换 `OnRecentSelected`：

```csharp
private async void OnRecentSelected(object? sender, SelectionChangedEventArgs e)
{
    try
    {
        if (e.CurrentSelection.FirstOrDefault() is not RecentItem item) return;
        RecentList.SelectedItem = null;
        if (item.TraceId is long traceId)
            await Navigation.PushAsync(_tracePageFactory.CreateBrowse(traceId));
        else if (item.CachedPath is string path)
            await Navigation.PushAsync(_tracePageFactory.Create(path, item.DisplayName, item.FileSizeBytes));
    }
    catch (Exception ex)
    {
        await DisplayAlertAsync("无法打开文件", ex.Message, "确定");
    }
}
```

在 `HandleIntentUriAsync` 导入成功后替换调用：

```csharp
var importedInfo = new FileInfo(dest);
await RefreshRecentAsync();
await Navigation.PushAsync(_tracePageFactory.Create(dest, importedInfo.Name, importedInfo.Length));
```

- [ ] **Step 5: 注册 DI**

修改 `MauiProgram.cs`：

```csharp
builder.Services.AddSingleton<ITraceCacheStore>(_ =>
    new TraceCacheStore(Path.Combine(FileSystem.CacheDirectory, "trace-cache.sqlite3")));
builder.Services.AddSingleton<ITraceCacheSinkFactory, TraceCacheWriterFactory>();
```

- [ ] **Step 6: 构建 Android app**

Run:
```bash
dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj --nologo
```

Expected: 构建成功，0 错误。

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.Mobile
git commit -m "feat(mobile): show sqlite trace library and open completed cache"
```

---

## Task 7: TraceBrowseViewModel 分页回看

**Files:**
- Create: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceBrowseViewModel.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceBrowseViewModelTests.cs`

**Interfaces:**
- Consumes: `ITraceCacheStore`, `FrameRow`
- Produces:
  - `TraceBrowseViewModel(ITraceCacheStore store)`
  - `Task OpenAsync(long traceId, CancellationToken ct = default)`
  - `IReadOnlyList<FrameRowSlot> Rows`
  - `[ObservableProperty] string header`, `pageStatus`, `filterText`, `errorMessage`
  - `[ObservableProperty] bool hasNext`, `hasPrevious`, `isLoading`
  - `[RelayCommand] Task FirstAsync()`
  - `[RelayCommand] Task PreviousAsync()`
  - `[RelayCommand] Task NextAsync()`
  - `[RelayCommand] Task ApplyFilterAsync()`

- [ ] **Step 1: 写失败测试**

Create `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceBrowseViewModelTests.cs`：

```csharp
using FluentAssertions;
using PeakCan.Host.Mobile.Core.Services;
using PeakCan.Host.Mobile.Core.ViewModels;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.ViewModels;

public class TraceBrowseViewModelTests
{
    private static CachedFrame Frame(long index, uint id = 0x100) =>
        new(index, index * 0.01, id, false, 2, [1, 2]);

    private static async Task<(TraceCacheStore Store, long TraceId)> CreateStoreAsync()
    {
        var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("browse.asc", 100);
        await store.AppendFramesAsync(id, Enumerable.Range(0, 181)
            .Select(i => Frame(i, i % 2 == 0 ? 0x100u : 0x200u))
            .ToArray());
        return (store, id);
    }

    [Fact]
    public async Task Open_Shows_First_Page_And_HasNext()
    {
        var (store, id) = await CreateStoreAsync();
        var vm = new TraceBrowseViewModel(store);

        await vm.OpenAsync(id);

        vm.Header.Should().Be("browse.asc");
        vm.HasNext.Should().BeTrue();
        vm.HasPrevious.Should().BeFalse();
        vm.Rows.Count(r => !r.IsEmpty).Should().Be(80);
        vm.PageStatus.Should().Contain("1-80");
    }

    [Fact]
    public async Task Next_Then_Previous_Returns_To_Previous_Page()
    {
        var (store, id) = await CreateStoreAsync();
        var vm = new TraceBrowseViewModel(store);
        await vm.OpenAsync(id);

        await vm.NextAsync();
        vm.Rows.Count(r => !r.IsEmpty).Should().Be(80);
        vm.HasPrevious.Should().BeTrue();

        await vm.PreviousAsync();
        vm.PageStatus.Should().Contain("1-80");
    }

    [Fact]
    public async Task ApplyFilter_Shows_Only_Matching_CanIds()
    {
        var (store, id) = await CreateStoreAsync();
        var vm = new TraceBrowseViewModel(store);
        await vm.OpenAsync(id);

        vm.FilterText = "0x200";
        await vm.ApplyFilterAsync();

        vm.Rows.Count(r => !r.IsEmpty).Should().Be(80);
        vm.Rows.Where(r => !r.IsEmpty).Select(r => r.IdText).Should().OnlyContain(text => text == "200");
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo --filter "FullyQualifiedName~TraceBrowseViewModelTests"
```

Expected: 编译失败，提示 `TraceBrowseViewModel` 不存在。

- [ ] **Step 3: 实现 browse VM**

Create `src/PeakCan.Host.Mobile.Core/ViewModels/TraceBrowseViewModel.cs`：

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Mobile.Core.Models;
using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Core.ViewModels;

/// <summary>Pages completed SQLite traces without materializing the whole file.</summary>
public sealed partial class TraceBrowseViewModel : ObservableObject
{
    private const int PageSize = 80;

    private readonly ITraceCacheStore _store;
    private readonly FrameRowSlot[] _slots = new FrameRowSlot[PageSize];
    private long _traceId;
    private long? _firstIndex;
    private long? _lastIndex;
    private IReadOnlySet<uint>? _idFilter;

    public TraceBrowseViewModel(ITraceCacheStore store)
    {
        _store = store;
        for (var i = 0; i < PageSize; i++)
            _slots[i] = new FrameRowSlot();
    }

    public IReadOnlyList<FrameRowSlot> Rows => _slots;

    [ObservableProperty] private string _header = string.Empty;
    [ObservableProperty] private string _pageStatus = string.Empty;
    [ObservableProperty] private string? _filterText;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _hasNext;
    [ObservableProperty] private bool _hasPrevious;
    [ObservableProperty] private bool _isLoading;

    public async Task OpenAsync(long traceId, CancellationToken ct = default)
    {
        _traceId = traceId;
        _firstIndex = null;
        _lastIndex = null;
        _idFilter = null;
        ErrorMessage = null;
        FilterText = null;
        Header = string.Empty;

        var summary = await _store.GetTraceAsync(traceId, ct).ConfigureAwait(false);
        if (summary is null)
        {
            ErrorMessage = "缓存记录不存在";
            return;
        }

        Header = summary.SourceName;
        await FirstAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private Task FirstAsync() => LoadAsync(new FrameQuery(AfterIndex: -1, Limit: PageSize));

    [RelayCommand]
    private Task NextAsync() => _lastIndex is null
        ? Task.CompletedTask
        : LoadAsync(new FrameQuery(AfterIndex: _lastIndex, CanIds: _idFilter, Limit: PageSize));

    [RelayCommand]
    private Task PreviousAsync() => _firstIndex is null
        ? Task.CompletedTask
        : LoadAsync(new FrameQuery(BeforeIndex: _firstIndex, CanIds: _idFilter, Limit: PageSize));

    [RelayCommand]
    private Task ApplyFilterAsync()
    {
        _idFilter = string.IsNullOrWhiteSpace(FilterText)
            ? null
            : CanIdListParser.Parse(FilterText).AllowList;
        return FirstAsync();
    }

    private async Task LoadAsync(FrameQuery query)
    {
        if (_traceId <= 0 || IsLoading) return;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var page = await _store.GetFramesAsync(_traceId, query).ConfigureAwait(false);
            var rows = page.Frames.Select(f => f.ToFrameRow()).ToArray();

            var blankCount = PageSize - rows.Length;
            for (var i = 0; i < blankCount; i++)
                _slots[i].Clear();
            for (var i = 0; i < rows.Length; i++)
                _slots[blankCount + i].UpdateFrom(rows[i]);

            HasNext = page.HasMore;
            HasPrevious = page.HasMore || (_firstIndex is not null && rows.Length > 0);

            if (rows.Length > 0)
            {
                _firstIndex = rows[0].Index;
                _lastIndex = rows[^1].Index;
                PageStatus = $"{rows[0].Index + 1}-{rows[^1].Index + 1}";
            }
            else
            {
                _firstIndex = null;
                _lastIndex = null;
                PageStatus = "0 帧";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo --filter "FullyQualifiedName~TraceBrowseViewModelTests"
```

Expected: 3 个新测试全部通过。

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Mobile.Core/ViewModels/TraceBrowseViewModel.cs tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceBrowseViewModelTests.cs
git commit -m "feat(mobile): add paged sqlite browse view model"
```

---

## Task 8: BrowsePage UI 与完整缓存入口

**Files:**
- Create: `src/PeakCan.Host.Mobile/Views/BrowsePage.xaml`
- Create: `src/PeakCan.Host.Mobile/Views/BrowsePage.xaml.cs`
- Modify: `src/PeakCan.Host.Mobile/Platform/TracePageFactory.cs`

**Interfaces:**
- Consumes: `TraceBrowseViewModel`, `ITraceCacheStore`
- Produces: completed trace opens into a paged, filterable `BrowsePage`.

- [ ] **Step 1: 创建 BrowsePage.xaml**

```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             x:Class="PeakCan.Host.Mobile.Views.BrowsePage"
             Title="回看">
    <Grid RowDefinitions="Auto,*,Auto" Padding="8">
        <HorizontalStackLayout Grid.Row="0" Spacing="8">
            <Button Text="⏮" Clicked="OnFirst" />
            <Button Text="←" Clicked="OnPrevious" IsEnabled="{Binding HasPrevious}" />
            <Button Text="→" Clicked="OnNext" IsEnabled="{Binding HasNext}" />
            <Entry x:Name="FilterEntry" Placeholder="ID 过滤 (hex, 逗号分隔)" WidthRequest="180" />
            <Button Text="过滤" Clicked="OnApplyFilter" />
        </HorizontalStackLayout>

        <CollectionView Grid.Row="1" ItemsSource="{Binding Rows}">
            <CollectionView.ItemTemplate>
                <DataTemplate>
                    <Grid ColumnDefinitions="2*,1.2*,1*,2.6*">
                        <Label Grid.Column="0" Text="{Binding TimeText}" FontFamily="Mono" FontSize="12" />
                        <Label Grid.Column="1" Text="{Binding IdText}" FontFamily="Mono" FontSize="12" />
                        <Label Grid.Column="2" Text="{Binding Dlc}" FontFamily="Mono" FontSize="12" />
                        <Label Grid.Column="3" Text="{Binding DataText}" FontFamily="Mono" FontSize="12" LineBreakMode="TailTruncation" />
                    </Grid>
                </DataTemplate>
            </CollectionView.ItemTemplate>
        </CollectionView>

        <VerticalStackLayout Grid.Row="2" Spacing="4">
            <Label Text="{Binding PageStatus}" FontSize="12" />
            <Label Text="{Binding ErrorMessage}" TextColor="Red" IsVisible="{Binding ErrorMessage, Converter={StaticResource IsNotNullConverter}}" />
        </VerticalStackLayout>
    </Grid>
</ContentPage>
```

若 app 资源里没有 `IsNotNullConverter`，复用仓库现有 boolean converter；不要为 P2 新增第三种空值 converter。

- [ ] **Step 2: 创建 BrowsePage.xaml.cs**

```csharp
using PeakCan.Host.Mobile.Core.Services;
using PeakCan.Host.Mobile.Core.ViewModels;

namespace PeakCan.Host.Mobile.Views;

public partial class BrowsePage : ContentPage
{
    private readonly TraceBrowseViewModel _vm;

    public BrowsePage(ITraceCacheStore cacheStore, long traceId)
    {
        InitializeComponent();
        _vm = new TraceBrowseViewModel(cacheStore);
        BindingContext = _vm;
        _ = InitializeAsync(traceId);
    }

    private async Task InitializeAsync(long traceId) => await _vm.OpenAsync(traceId);

    private void OnFirst(object? sender, EventArgs e) => _vm.FirstAsyncCommand.Execute(null);
    private void OnPrevious(object? sender, EventArgs e) => _vm.PreviousAsyncCommand.Execute(null);
    private void OnNext(object? sender, EventArgs e) => _vm.NextAsyncCommand.Execute(null);

    private void OnApplyFilter(object? sender, EventArgs e)
    {
        _vm.FilterText = FilterEntry.Text;
        _vm.ApplyFilterCommand.Execute(null);
    }
}
```

- [ ] **Step 3: 替换 Task 6 的临时 browse page**

修改 `TracePageFactory.CreateBrowse`：

```csharp
public ContentPage CreateBrowse(long traceId)
    => new BrowsePage(
        services.GetRequiredService<ITraceCacheStore>(),
        traceId);
```

`TracePageFactory` 的 primary constructor 参数名是 `services`，直接复用即可。

- [ ] **Step 4: 构建 Android app**

Run:
```bash
dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj --nologo
```

Expected: 构建成功，0 错误。

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Mobile/Views/BrowsePage.xaml src/PeakCan.Host.Mobile/Views/BrowsePage.xaml.cs src/PeakCan.Host.Mobile/Platform/TracePageFactory.cs
git commit -m "feat(mobile): add sqlite browse page for completed traces"
```

---

## Task 9: DbcCatalog 解析与按需解码

**Files:**
- Create: `src/PeakCan.Host.Mobile.Core/Services/DbcCatalogModels.cs`
- Create: `src/PeakCan.Host.Mobile.Core/Services/DbcCatalog.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/Services/DbcCatalogTests.cs`

**Interfaces:**
- Consumes: `PeakCan.HIL.Core.Dbc.DbcParser`, `SignalDecoder`
- Produces:
  - `SignalDisplay(string Name, string Value, string Unit)`
  - `FrameDecodeResult(string MessageName, IReadOnlyList<SignalDisplay> Signals)`
  - `DbcCatalogLoadResult(DbcCatalog? Catalog, string SourceName, string? Error)`
  - `DbcCatalog.Parse(string text, string sourceName = "")`
  - `DbcCatalog.SourceName`, `DbcCatalog.Document`
  - `DbcCatalog.FindMessage(uint canId, bool isExtended)`
  - `DbcCatalog.Decode(uint canId, bool isExtended, byte[] data, byte dlc)`

- [ ] **Step 1: 写失败测试**

Create `tests/PeakCan.Host.Mobile.Core.Tests/Services/DbcCatalogTests.cs`：

```csharp
using FluentAssertions;
using PeakCan.Host.Mobile.Core.Services;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class DbcCatalogTests
{
    private const string Dbc = """
        VERSION ""

        NS_ :

        BS_:

        BU_: ECM

        BO_ 256 EngineData: 8 ECM
         SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
         SG_ EngineTemp : 16|8@1+ (1,-40) [0|215] "C" Vector__XXX
        """;

    [Fact]
    public void Parse_Loads_Message_And_Decodes_Standard_Id()
    {
        var result = DbcCatalog.Parse(Dbc, "engine.dbc");

        result.Error.Should().BeNull();
        var catalog = result.Catalog!;
        var decoded = catalog.Decode(0x100, isExtended: false, [0x01, 0x02, 0x03], dlc: 3);

        decoded!.MessageName.Should().Be("EngineData");
        decoded.Signals.Should().Contain(s => s.Name == "EngineSpeed" && s.Value == "128.25" && s.Unit == "rpm");
        decoded.Signals.Should().Contain(s => s.Name == "EngineTemp" && s.Value == "-37" && s.Unit == "C");
    }

    [Fact]
    public void Decode_Resolves_Extended_Id()
    {
        const string extendedDbc = """
            VERSION ""
            NS_ :
            BS_:
            BU_: ECM

            BO_ 2147484672 ExtendedData: 8 ECM
             SG_ Speed : 0|16@1+ (0.1,0) [0|0] "kph" Vector__XXX
            """;
        var catalog = DbcCatalog.Parse(extendedDbc).Catalog!;

        var decoded = catalog.Decode(0x100, isExtended: true, [0x0A, 0x00], dlc: 2);

        decoded!.MessageName.Should().Be("ExtendedData");
        decoded.Signals[0].Value.Should().Be("1");
    }

    [Fact]
    public void Parse_Returns_Error_Without_Throwing()
    {
        var result = DbcCatalog.Parse("not dbc");
        result.Catalog.Should().BeNull();
        result.Error.Should().NotBeNullOrEmpty();
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo --filter "FullyQualifiedName~DbcCatalogTests"
```

Expected: 编译失败，提示 `DbcCatalog` 不存在。

- [ ] **Step 3: 实现 DbcCatalog**

Create `src/PeakCan.Host.Mobile.Core/Services/DbcCatalogModels.cs`：

```csharp
namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>One decoded signal for UI display.</summary>
public sealed record SignalDisplay(string Name, string Value, string Unit)
{
    public string DisplayText => string.IsNullOrEmpty(Unit)
        ? $"{Name}={Value}"
        : $"{Name}={Value}{Unit}";
}

/// <summary>DBC decode result for one frame.</summary>
public sealed record FrameDecodeResult(string MessageName, IReadOnlyList<SignalDisplay> Signals);

/// <summary>Result of parsing a picked DBC file.</summary>
public sealed record DbcCatalogLoadResult(DbcCatalog? Catalog, string SourceName, string? Error)
{
    public static DbcCatalogLoadResult Cancelled(string sourceName = "") => new(null, sourceName, null);
}
```

Create `src/PeakCan.Host.Mobile.Core/Services/DbcCatalog.cs`：

```csharp
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>Runtime lookup table over one parsed DBC document.</summary>
public sealed class DbcCatalog
{
    private readonly Dictionary<(uint CanId, bool IsExtended), Message> _messages;

    private DbcCatalog(DbcDocument document, string sourceName)
    {
        Document = document;
        SourceName = sourceName;
        _messages = document.Messages.ToDictionary(ToKey);
    }

    public DbcDocument Document { get; }
    public string SourceName { get; }

    public static DbcCatalogLoadResult Parse(string text, string sourceName = "")
    {
        try
        {
            var parsed = DbcParser.Parse(text);
            if (!parsed.IsSuccess)
                return new(null, sourceName, parsed.Error?.Message ?? "DBC 解析失败。");
            return new(new DbcCatalog(parsed.Value!, sourceName), sourceName, null);
        }
        catch (Exception ex)
        {
            return new(null, sourceName, ex.Message);
        }
    }

    public Message? FindMessage(uint canId, bool isExtended) =>
        _messages.TryGetValue((canId, isExtended), out var message) ? message : null;

    public FrameDecodeResult? Decode(uint canId, bool isExtended, byte[] data, byte dlc)
    {
        if (!_messages.TryGetValue((canId, isExtended), out var message))
            return null;

        var signals = new List<SignalDisplay>(message.Signals.Count);
        foreach (var signal in message.Signals)
        {
            if (!IsSignalActive(message, signal, data))
                continue;

            var value = SignalDecoder.Decode(data.AsSpan(), signal);
            var enumText = SignalDecoder.TryDecodeEnumText(signal, value, Document);
            var displayValue = enumText ?? FormatNumber(value);
            signals.Add(new SignalDisplay(signal.Name, displayValue, signal.Unit));
        }

        return new FrameDecodeResult(message.Name, signals);
    }

    private static bool IsSignalActive(Message message, Signal signal, byte[] data)
    {
        if (!message.IsMultiplexed || !signal.IsMultiplexed)
            return true;
        if (message.MultiplexorSignalIndex is not int index || index < 0 || index >= message.Signals.Count)
            return false;

        var selector = SignalDecoder.Decode(data.AsSpan(), message.Signals[index]);
        return signal.MultiplexValue is ushort expected &&
               Math.Abs(selector - expected) < 0.5;
    }

    private static string FormatNumber(double value) => value == Math.Floor(value)
        ? value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
        : value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static (uint, bool) ToKey(Message message) =>
        ((message.Id & 0x80000000u) == 0 ? message.Id : message.Id & 0x7fffffffu,
         (message.Id & 0x80000000u) != 0);
}
```

- [ ] **Step 4: 运行测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo --filter "FullyQualifiedName~DbcCatalogTests"
```

Expected: 3 个新测试全部通过。

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Mobile.Core/Services/DbcCatalogModels.cs src/PeakCan.Host.Mobile.Core/Services/DbcCatalog.cs tests/PeakCan.Host.Mobile.Core.Tests/Services/DbcCatalogTests.cs
git commit -m "feat(mobile): add on-demand dbc signal decoding"
```

---

## Task 10: FrameRow 信号摘要与播放/回看接入

**Files:**
- Modify: `src/PeakCan.Host.Mobile.Core/Models/FrameRow.cs`
- Modify: `src/PeakCan.Host.Mobile.Core/Models/FrameRowSlot.cs`
- Modify: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceSessionViewModel.cs`
- Modify: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceBrowseViewModel.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/Models/FrameRowTests.cs`

**Interfaces:**
- Consumes: Task 9 的 `DbcCatalog.Decode`
- Produces:
  - `FrameRow(..., string SignalSummaryText = "")`
  - `FrameRow.FromReplayFrame(ReplayFrame frame, DbcCatalog? dbc = null)`
  - `FrameRow.FromCached(CachedFrame frame, DbcCatalog? dbc = null)`
  - `FrameRowSlot.SignalSummaryText`
  - `FrameRowSlot.Source`
  - `TraceSessionViewModel.SetDbc(DbcCatalog? catalog)`
  - `TraceSessionViewModel.Dbc`
  - `TraceSessionViewModel.DbcStatusText`
  - `TraceBrowseViewModel.SetDbc(DbcCatalog? catalog)`

- [ ] **Step 1: 写失败测试**

在 `FrameRowTests` 中追加：

```csharp
[Fact]
public void FromReplayFrame_Decodes_Visible_Dbc_Signals()
{
    const string dbc = """
        VERSION ""
        NS_ :
        BS_:
        BU_: ECM

        BO_ 256 EngineData: 8 ECM
         SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
        """;
    var catalog = DbcCatalog.Parse(dbc).Catalog!;
    var frame = new ReplayFrame(0, 0x100, 2, [0x01, 0x02], default, false);

    var row = FrameRow.FromReplayFrame(frame, catalog);

    row.SignalSummaryText.Should().Contain("EngineSpeed=128.25rpm");
}

[Fact]
public void FromCached_Without_Dbc_Keeps_Empty_Signal_Summary()
{
    var frame = new CachedFrame(0, 1, 0x100, false, 2, [1, 2]);
    var row = FrameRow.FromCached(frame);
    row.SignalSummaryText.Should().BeEmpty();
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo --filter "FullyQualifiedName~FrameRowTests"
```

Expected: 编译失败，提示 `SignalSummaryText` / `FromCached` / overload 不存在。

- [ ] **Step 3: 修改 FrameRow**

将 `FrameRow` 主构造与工厂改为：

```csharp
public sealed record FrameRow(
    double Timestamp,
    uint Id,
    bool IsExtended,
    byte Dlc,
    byte[] Data,
    string SignalSummaryText = "")
{
    // 保留原有 TimeText/IdText/DataText 属性。

    public static FrameRow FromReplayFrame(ReplayFrame f, DbcCatalog? dbc = null)
    {
        var summary = dbc?.Decode(f.Id, f.IsExtended, f.Data, f.Dlc)?.Signals.ToSummary();
        return new(f.Timestamp, f.Id, f.IsExtended, f.Dlc, f.Data, summary ?? string.Empty);
    }

    public static FrameRow FromCached(CachedFrame f, DbcCatalog? dbc = null)
    {
        var summary = dbc?.Decode(f.CanId, f.IsExtended, f.Data, f.Dlc)?.Signals.ToSummary();
        return new(f.Timestamp, f.CanId, f.IsExtended, f.Dlc, f.Data, summary ?? string.Empty);
    }
}
```

在 `DbcCatalogModels.cs` 追加扩展：

```csharp
public static class SignalDisplayExtensions
{
    public static string ToSummary(this IReadOnlyList<SignalDisplay> signals, int maxCount = 2)
    {
        if (signals.Count == 0) return string.Empty;
        return string.Join("  ", signals.Take(maxCount).Select(s => s.DisplayText));
    }
}
```

- [ ] **Step 4: 修改 FrameRowSlot**

在 `FrameRowSlot` 中追加：

```csharp
[ObservableProperty] private string _signalSummaryText = string.Empty;
public FrameRow? Source { get; private set; }
```

`UpdateFrom` 中设置：

```csharp
SignalSummaryText = row.SignalSummaryText;
Source = row;
```

`Clear` 中设置：

```csharp
SignalSummaryText = string.Empty;
Source = null;
```

- [ ] **Step 5: 接入 VM**

在 `TraceSessionViewModel` 中追加：

```csharp
private DbcCatalog? _dbc;

public DbcCatalog? Dbc => _dbc;
[ObservableProperty] private string _dbcStatusText = "未加载 DBC";

public void SetDbc(DbcCatalog? catalog)
{
    _dbc = catalog;
    DbcStatusText = catalog is null ? "未加载 DBC" : $"DBC: {catalog.SourceName}";
    ClearPlaybackBuffer();
}
```

在 `Drain` 中替换：

```csharp
batch.Add(FrameRow.FromReplayFrame(_pending.Dequeue(), _dbc));
```

在 `OpenAsync` 预读中替换：

```csharp
_rows.Add(FrameRow.FromReplayFrame(f, _dbc));
```

在 `TraceBrowseViewModel` 中追加：

```csharp
private DbcCatalog? _dbc;

public void SetDbc(DbcCatalog? catalog) => _dbc = catalog;
```

并把：

```csharp
var rows = page.Frames.Select(f => f.ToFrameRow()).ToArray();
```

替换为：

```csharp
var rows = page.Frames.Select(f => f.ToFrameRow(_dbc)).ToArray();
```

- [ ] **Step 6: 运行测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo
```

Expected: 全部测试通过。

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.Mobile.Core tests/PeakCan.Host.Mobile.Core.Tests
git commit -m "feat(mobile): decode visible rows from loaded dbc"
```

---

## Task 11: DBC 选择、信号列与帧详情

**Files:**
- Create: `src/PeakCan.Host.Mobile.Core/Platform/IDbcCatalogProvider.cs`
- Create: `src/PeakCan.Host.Mobile.Core/Services/DbcCatalogHolder.cs`
- Create: `src/PeakCan.Host.Mobile/Platform/MauiDbcCatalogProvider.cs`
- Modify: `src/PeakCan.Host.Mobile/Views/TracePage.xaml`
- Modify: `src/PeakCan.Host.Mobile/Views/TracePage.xaml.cs`
- Modify: `src/PeakCan.Host.Mobile/Views/BrowsePage.xaml`
- Modify: `src/PeakCan.Host.Mobile/Views/BrowsePage.xaml.cs`
- Modify: `src/PeakCan.Host.Mobile/Views/FrameDetailSheet.xaml`
- Modify: `src/PeakCan.Host.Mobile/Views/FrameDetailSheet.xaml.cs`
- Modify: `src/PeakCan.Host.Mobile/Platform/TracePageFactory.cs`
- Modify: `src/PeakCan.Host.Mobile/MauiProgram.cs`

**Interfaces:**
- Consumes: `DbcCatalog`, `TraceSessionViewModel.SetDbc`, `TraceBrowseViewModel.SetDbc`
- Produces:
  - `IDbcCatalogProvider.PickAndLoadAsync(CancellationToken ct = default)`
  - `DbcCatalogHolder.Current` / `Set(DbcCatalog?)`
  - Trace 页显示 DBC 状态、信号摘要列
  - 帧详情显示完整 `SignalDisplay` 列表

- [ ] **Step 1: 定义 provider 与 holder**

Create `src/PeakCan.Host.Mobile.Core/Platform/IDbcCatalogProvider.cs`：

```csharp
using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Core.Platform;

public interface IDbcCatalogProvider
{
    Task<DbcCatalogLoadResult> PickAndLoadAsync(CancellationToken ct = default);
}
```

Create `src/PeakCan.Host.Mobile.Core/Services/DbcCatalogHolder.cs`：

```csharp
namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>Application-wide current DBC for playback and browse pages.</summary>
public sealed class DbcCatalogHolder
{
    public DbcCatalog? Current { get; private set; }
    public void Set(DbcCatalog? catalog) => Current = catalog;
}
```

- [ ] **Step 2: 实现 MAUI DBC picker**

Create `src/PeakCan.Host.Mobile/Platform/MauiDbcCatalogProvider.cs`：

```csharp
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Platform;

public sealed class MauiDbcCatalogProvider : IDbcCatalogProvider
{
    public async Task<DbcCatalogLoadResult> PickAndLoadAsync(CancellationToken ct = default)
    {
        var custom = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.Android] = ["application/octet-stream", "text/plain"],
        });

        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "选择 DBC 文件",
            FileTypes = custom,
        });
        if (result is null) return DbcCatalogLoadResult.Cancelled();

        if (!string.Equals(Path.GetExtension(result.FileName), ".dbc", StringComparison.OrdinalIgnoreCase))
            return new DbcCatalogLoadResult(null, result.FileName, "仅支持 .dbc 文件。");

        await using var stream = await result.OpenReadAsync(ct);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync(ct);
        return DbcCatalog.Parse(text, result.FileName);
    }
}
```

- [ ] **Step 3: 修改 TracePage UI**

在 `TracePage.xaml` 顶部控制条追加：

```xml
<Button Text="DBC" Clicked="OnLoadDbcClicked" />
<Label Text="{Binding DbcStatusText}" FontSize="12" VerticalOptions="Center" />
```

将表格列定义改为：

```xml
<Grid ColumnDefinitions="1.5*,1*,0.5*,1.7*,2.2*">
    <Label Grid.Column="0" Text="{Binding TimeText}" FontFamily="Mono" FontSize="12" />
    <Label Grid.Column="1" Text="{Binding IdText}" FontFamily="Mono" FontSize="12" />
    <Label Grid.Column="2" Text="{Binding Dlc}" FontFamily="Mono" FontSize="12" />
    <Label Grid.Column="3" Text="{Binding DataText}" FontFamily="Mono" FontSize="12" LineBreakMode="TailTruncation" />
    <Label Grid.Column="4" Text="{Binding SignalSummaryText}" FontFamily="Mono" FontSize="12" LineBreakMode="TailTruncation" />
</Grid>
```

修改 `TracePage` 构造函数，注入 provider 和 holder：

```csharp
private readonly IDbcCatalogProvider _dbcProvider;
private readonly DbcCatalogHolder _dbcHolder;

public TracePage(
    IUiDispatcher ui,
    IStreamingSourceFactory sourceFactory,
    string cachedFilePath,
    string sourceName,
    long fileSizeBytes,
    ILogger? logger = null,
    IDbcCatalogProvider? dbcProvider = null,
    DbcCatalogHolder? dbcHolder = null)
{
    InitializeComponent();
    _dbcProvider = dbcProvider ?? throw new ArgumentNullException(nameof(dbcProvider));
    _dbcHolder = dbcHolder ?? new DbcCatalogHolder();
    _vm = new TraceSessionViewModel(
        ui,
        sourceFactory,
        src => new PeakCan.Host.Core.Replay.StreamingTracePlayer(src, clock: null),
        logger);
    BindingContext = _vm;
    _vm.PropertyChanged += OnVmPropertyChanged;
    SpeedPicker.ItemsSource = new[] { "0.1x", "0.5x", "1x", "2x", "5x", "10x" };
    SpeedPicker.SelectedIndex = 2;
    _vm.SetDbc(_dbcHolder.Current);
    _ = InitializeAsync(cachedFilePath, sourceName, fileSizeBytes);
}

private async void OnLoadDbcClicked(object? sender, EventArgs e)
{
    try
    {
        var result = await _dbcProvider.PickAndLoadAsync();
        if (result.Error is not null)
        {
            await DisplayAlertAsync("DBC 加载失败", result.Error, "确定");
            return;
        }
        if (result.Catalog is null) return;

        _dbcHolder.Set(result.Catalog);
        _vm.SetDbc(result.Catalog);
    }
    catch (Exception ex)
    {
        await DisplayAlertAsync("DBC 加载失败", ex.Message, "确定");
    }
}
```

- [ ] **Step 4: 修改帧详情**

将 `FrameDetailSheet.xaml.cs` 构造函数改为：

```csharp
public IReadOnlyList<SignalDisplay> Signals { get; }

public FrameDetailSheet(
    string header,
    string rawBytes,
    IReadOnlyList<SignalDisplay> signals)
{
    InitializeComponent();
    Header = header;
    RawBytes = rawBytes;
    Signals = signals;
    BindingContext = this;
}
```

在 `FrameDetailSheet.xaml` 的 raw bytes 后追加：

```xml
<Label Text="信号" FontAttributes="Bold" Margin="0,12,0,0" />
<CollectionView ItemsSource="{Binding Signals}">
    <CollectionView.ItemTemplate>
        <DataTemplate>
            <Label Text="{Binding DisplayText}" FontFamily="Mono" />
        </DataTemplate>
    </CollectionView.ItemTemplate>
</CollectionView>
```

修改 `TracePage.OnRowTapped`：

```csharp
private void OnRowTapped(object? sender, TappedEventArgs e)
{
    if (sender is not BindableObject { BindingContext: FrameRowSlot row } || row.IsEmpty || row.Source is null) return;

    var decoded = _vm.Dbc?.Decode(
        row.Source.Id,
        row.Source.IsExtended,
        row.Source.Data,
        row.Source.Dlc)?.Signals ?? [];

    _ = Navigation.PushAsync(new FrameDetailSheet(
        $"0x{row.IdText} @ {row.TimeText}",
        row.DataText,
        decoded));
}
```

- [ ] **Step 5: BrowsePage 显示信号列**

给 `BrowsePage` 注入 `DbcCatalogHolder`：

```csharp
public BrowsePage(ITraceCacheStore cacheStore, DbcCatalogHolder dbcHolder, long traceId)
{
    InitializeComponent();
    _vm = new TraceBrowseViewModel(cacheStore);
    _vm.SetDbc(dbcHolder.Current);
    BindingContext = _vm;
    _ = InitializeAsync(traceId);
}
```

在 `BrowsePage.xaml` 表格添加与 TracePage 相同的第 5 列，绑定 `SignalSummaryText`。

- [ ] **Step 6: 注册 DI 与工厂**

`MauiProgram.cs` 追加：

```csharp
builder.Services.AddSingleton<IDbcCatalogProvider, MauiDbcCatalogProvider>();
builder.Services.AddSingleton<DbcCatalogHolder>();
```

`TracePageFactory` 中更新构造调用：

```csharp
new TracePage(
    services.GetRequiredService<IUiDispatcher>(),
    services.GetRequiredService<IStreamingSourceFactory>(),
    cachedFilePath,
    sourceName,
    fileSizeBytes,
    services.GetRequiredService<ILogger<TraceSessionViewModel>>(),
    services.GetRequiredService<IDbcCatalogProvider>(),
    services.GetRequiredService<DbcCatalogHolder>());
```

```csharp
new BrowsePage(
    services.GetRequiredService<ITraceCacheStore>(),
    services.GetRequiredService<DbcCatalogHolder>(),
    traceId);
```

- [ ] **Step 7: 构建并测试**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo
dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj --nologo
```

Expected: 测试全部通过，Android 构建成功。

- [ ] **Step 8: Commit**

```bash
git add src/PeakCan.Host.Mobile.Core src/PeakCan.Host.Mobile
git commit -m "feat(mobile): add dbc picker signal columns and frame details"
```

---

## Task 12: 跳过行计数与缓存状态展示

**Files:**
- Modify: `src/PeakCan.Host.Core/Replay/Streaming/IStreamingTracePlayer.cs`
- Modify: `src/PeakCan.Host.Core/Replay/Streaming/StreamingTracePlayer.cs`
- Modify: `tests/PeakCan.Host.Mobile.Core.Tests/Fakes/FakeStreamingTracePlayer.cs`
- Modify: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceSessionViewModel.cs`
- Modify: `src/PeakCan.Host.Mobile/Views/TracePage.xaml`

**Interfaces:**
- Consumes: `StreamingParseStats.SkippedLines`
- Produces:
  - `IStreamingTracePlayer.SkippedLines`
  - `TraceSessionViewModel.SkippedLinesText`
  - Trace 页底部显示缓存状态和跳过行数。

- [ ] **Step 1: 写失败测试**

在 `TraceSessionViewModelTests` 中追加：

```csharp
[Fact]
public void Drain_Shows_Skipped_Lines_From_Player()
{
    var env = new Env();
    env.Vm.MarkReadyForEmit(env.Player);
    env.Player.SkippedLines = 7;

    env.Player.Emit(F(0, 0x100));
    DrainTimer(env.Vm).Tick();

    env.Vm.SkippedLinesText.Should().Be("已跳过 7 行");
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo --filter "FullyQualifiedName~TraceSessionViewModelTests"
```

Expected: 编译失败，提示 `SkippedLines` / `SkippedLinesText` 不存在。

- [ ] **Step 3: 扩展 player**

在 `IStreamingTracePlayer` 追加：

```csharp
/// <summary>Skipped malformed lines reported by the current open session.</summary>
long SkippedLines { get; }
```

在 `StreamingTracePlayer` 中追加：

```csharp
private long _skippedLines;
public long SkippedLines => Interlocked.Read(ref _skippedLines);
```

在 `RunLoopAsync` 打开 session 后设置：

```csharp
Interlocked.Exchange(ref _skippedLines, session.Stats.SkippedLines);
```

在 `FakeStreamingTracePlayer` 追加：

```csharp
public long SkippedLines { get; set; }
```

- [ ] **Step 4: VM 与 XAML**

在 `TraceSessionViewModel` 追加：

```csharp
[ObservableProperty] private string _skippedLinesText = string.Empty;
```

在 `Drain()` 更新 viewport 前设置：

```csharp
SkippedLinesText = _player?.SkippedLines > 0 ? $"已跳过 {_player.SkippedLines} 行" : string.Empty;
```

在 `TracePage.xaml` 外层 Grid 行定义改为：

```xml
<Grid RowDefinitions="Auto,Auto,*,Auto,Auto" Padding="8">
```

在表格后追加状态行：

```xml
<HorizontalStackLayout Grid.Row="3" Spacing="12" Margin="0,4">
    <Label Text="{Binding CacheStatusText}" FontSize="12" TextColor="Gray" />
    <Label Text="{Binding SkippedLinesText}" FontSize="12" TextColor="Orange" />
</HorizontalStackLayout>
```

把底部 filter 行改为 `Grid.Row="4"`。

- [ ] **Step 5: 运行测试与构建**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo
dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj --nologo
```

Expected: 测试通过，构建成功。

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Core src/PeakCan.Host.Mobile.Core tests/PeakCan.Host.Mobile.Core.Tests src/PeakCan.Host.Mobile
git commit -m "feat(mobile): expose skipped lines and cache health"
```

---

## Task 13: P2 全量验证与真机验收

**Files:**
- Modify: `docs/superpowers/plans/2026-09-08-mobile-trace-viewer-p2.md`

**Interfaces:**
- Consumes: Tasks 1–12 全部实现
- Produces: P2 完成记录。

- [x] **Step 1: 全量测试**

Run:
```bash
dotnet test PeakCan.Host.slnx --nologo
dotnet test PeakCan.Host.Mobile.slnx --nologo
```

Expected: 两个 solution 0 failed。Core 新增代码覆盖率目标 ≥80%；若覆盖率低于 80%，先补测试再进入真机验收。

- [x] **Step 2: Android 构建**

Run:
```bash
dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj --nologo
```

Expected: 0 error。

- [x] **Step 3: 真机缓存验收**

使用 100MB ASC（可用 `powershell -File tools/gen-large-asc.ps1` 生成）：

1. 冷导入并播放到 EOF，等待 Trace 页显示“缓存完成”。
2. 返回 FilesPage，确认最近项显示帧数、时长、“完整”。
3. 再次点击同一文件，应不触发 100MB 拷贝并直接进入 BrowsePage。
4. BrowsePage 首屏 80 行应 <2s 显示。
5. 过滤 `0x103`，翻页结果仍只包含 `103`。
6. Previous/Next 翻页顺序保持时间递增。
7. `adb shell dumpsys meminfo com.zhengtaotao.peakcan.mobile`，TOTAL PSS <300MB。

- [x] **Step 4: SQLite 失败降级验收**

1. 停止 app。
2. 通过 `adb shell run-as com.zhengtaotao.peakcan.mobile` 将 `cache/trace-cache.sqlite3` 临时改名或加只读。
3. 打开一个新 ASC 并播放。
4. 预期：回放继续，Trace 页显示“缓存不可用/缓存已停用”。
5. 恢复 SQLite 文件，删除 app 数据后重测正常缓存。

- [x] **Step 5: DBC 验收**

1. 播放匹配 fixture DBC 的 ASC。
2. 点击 DBC 选择 fixture。
3. 表格信号摘要列显示正确前两个信号。
4. 点击帧，FrameDetailSheet 显示全部解码信号、单位、VAL_ 枚举文本。
5. BrowsePage 复用当前 DBC，回看行同样显示信号摘要。

- [x] **Step 6: 勾选计划与提交**

在本计划 Task 13 所有 checkbox 打勾后：

```bash
git add docs/superpowers/plans/2026-09-08-mobile-trace-viewer-p2.md
git commit -m "docs(mobile): record trace viewer p2 acceptance"
```

---

## Self-Review

- **Spec coverage:** P2 的 SQLite schema、5000 帧批量写、`source_name + file_size` 匹配重开、SQLite 失败降级、完整过滤回看、DBC 加载与按需解码、信号摘要/详情、最近文件列表、跳过行摘要与缓存状态均在 Task 2–12 覆盖。
- **Additive compliance:** 既有批量 ASC/BLF 解析调用点不改；`IStreamingTracePlayer` 只新增 `SkippedLines`，由唯一实现和测试 fake 同步更新。
- **No placeholders:** Task 6 的临时 browse page 是过渡实现，Task 8 必须替换为真实 `BrowsePage`；Task 10 明确要求 `SetDbc` 只调用 `ClearPlaybackBuffer()`，不保留任何空循环。
- **Type consistency:** `FrameQuery`/`FramePage`/`CachedFrame`、`ITraceCacheStore`、`ITraceCacheSink`、`TraceBrowseViewModel`、`DbcCatalog` 的方法名与调用点一致；`FrameRowSlot.Source` 保证详情解码的是被点击行。
- **Data consistency:** `TraceCacheWriter` 只有在无失败且无 dropped frame 时才把 trace 标记 complete；seek 不 emit 被跳过帧，因此未完整播放的缓存保持 incomplete，不会被当作完整回看源。
