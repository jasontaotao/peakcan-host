# 移动端 Trace Viewer（Android / .NET MAUI）设计

- 日期：2026-09-07
- 状态：已确认方向（用户 2026-09-07 批准设计六节）；v2 并入架构+产品双视角评审 5 项修订（UI 信息架构 / P1 范围 +3 / 可测性抽象层 / 状态机补全 / 表格渲染策略）
- 分支：`feature/mobile-trace-viewer`（从 `main` 拉出）
- 相关：[2026-09-04-trace-viewer-canoe-graph-parity-design.md](2026-09-04-trace-viewer-canoe-graph-parity-design.md)（桌面端 CANoe 图表对齐，与本项目交互模型一致）

## 1. 背景与目标

peakcan-host 桌面端已有完整 Trace Viewer（ASC/BLF 加载、DBC 解码、回放、图表）。用户需求：**在 Android 手机上离线查看已录制的 trace 文件**，交互模型对齐 Vector CANoe——点击回放后**一边加载一边在表格/图表上显示**，而不是先全量导入再看。

### 非目标（明确排除）

- 直连 PCAN-USB FD 硬件（PEAK 无 Android 驱动，技术上不可行）
- 实时数据（未来可做"PC 端 WebSocket 推送 + 手机远程显示"，独立项目）
- iOS / Windows 目标（只 `net10.0-android`）
- 桌面端 Trace Viewer 的完整功能平移（J1939 协议感知、Copilot 面板、会话库等）
- 应用商店上架（APK 内网 sideload 分发）

## 2. 关键约束（驱动架构的事实）

1. **典型 trace 文件经常超 100MB**（用户确认）。Android 应用堆预算通常 256-512MB。
2. 现有 Core 解析器是**全量物化**模式：`AscParser.ParseAsync` 先把整个文件读成 `List<string>` 再返回完整 `IReadOnlyList<ReplayFrame>`；`BlfParser` 同样全量累积后排序。100MB+ 文件照搬此模式在手机上必 OOM。
3. 可复用资产（全部已验证为纯 `net10.0`，无 Windows 依赖）：
   - `PeakCan.Host.Core/Replay/`：`AscParser`、`BlfParser` 的帧级解析逻辑、`ReplayFrame`、`ReplayState`、`IReplayClock`、`ReplayOptions`
   - `PeakCan.HIL.Core`（net10.0）：`DbcParser`（`PeakCan.HIL.Core.Dbc`）、`SignalDecoder`
4. 不可复用：`ReplayTimeline`（`internal`，绑定 `IReadOnlyList<ReplayFrame>` 全量列表）→ 移动端需要新的流式播放器。
5. 开发环境从零开始：无 MAUI workload、无 Android SDK、无 adb（2026-09-07 实测）。有 Android 12+ 真机可 USB 调试。

## 3. 架构决策（ADR 摘要）

### D1：流式回放为核心，SQLite 为回放缓存（已确认）

候选方案：

| 方案 | 结论 |
|---|---|
| **A. SQLite 预索引**（解析全文件落库再开放浏览） | 否：首帧延迟 = 全量解析时间（100MB 约几十秒），不符合 CANoe 交互 |
| **B. 内存全量**（照搬桌面） | 否：100MB+ 文件在 Android 上必 OOM（约束 2） |
| **C. 字节偏移索引 + 懒解析** | 否：每次过滤/图表需重扫原文件；工程复杂度不低 |
| **D. 流式回放 + SQLite 回放缓存** | **采用**：首帧 <1s；流过的帧后台落盘，支持回看/事后过滤 |

D = 用户提出的 CANoe 模式（点击回放后边加载边显示）+ SQLite 作为流过数据的沉淀层。SQLite 的角色是**回放缓存**而非前置索引，用户无感知。

### D2：Core 加 additive 流式 API，不动现有代码路径

现有 `AscParser.ParseAsync` 等 14 个调用点全部不变。新增独立的流式入口。桌面端零影响。

### D3：项目放 peakcan-host repo 内，单独 slnx

- 新增 `src/PeakCan.Host.Mobile/`（MAUI，单 `net10.0-android` target）和 `src/PeakCan.Host.Mobile.Core/`（纯 `net10.0` VM/服务库，供 VM 单测）
- 新增 `PeakCan.Host.Mobile.slnx`（含 Mobile + Host.Core + HIL.Core）
- 主 `PeakCan.Host.slnx` **不收录** Mobile 项目——打开主解决方案的人不需要装 MAUI workload
- Mobile 对 Host.Core / HIL.Core 用 **ProjectReference**（同 repo，改 Core 时同步编译验证）

### D4：分期交付

全功能集（加载/表格/过滤/跳转/DBC/回放/图表）一次做完约 2-3 周，拆为 5 期、每期可用（见 §9）。

## 4. Core 新增 API（additive）

### 4.1 流式解析源抽象

```csharp
// src/PeakCan.Host.Core/Replay/Streaming/IStreamingTraceSource.cs
public interface IStreamingTraceSource
{
    // 打开（或重开）底层流，急切读完头部后返回惰性帧流。
    // Seek 时由播放器再次调用本方法获得新枚举。
    Task<StreamingTraceOpenResult> OpenAsync(CancellationToken ct = default);
}

public sealed class StreamingTraceOpenResult : IAsyncDisposable
{
    public DateTime? WallClockOrigin { get; init; }   // ASC date 行 / BLF 基准时间
    public bool TimestampsAreAbsolute { get; init; }  // ASC base 行
    public required IAsyncEnumerable<ReplayFrame> Frames { get; init; }
    public long? SourceLengthBytes { get; init; }     // SeekProgress 需要流长度
    public required StreamingParseStats Stats { get; init; }
    public Stream? SourceStream { get; init; }        // session 拥有该流

    public async ValueTask DisposeAsync()
    {
        if (SourceStream is not null)
            await SourceStream.DisposeAsync();
    }
}
```

`OpenAsync()` 返回一个独立的 streaming session。session 结束、取消或异常时必须由消费者 `await using` 释放；`AscStreamingSource` 把底层 Stream 放入 `SourceStream`，由 session 统一负责关闭。

### 4.2 ASC 实现

```csharp
// src/PeakCan.Host.Core/Replay/Streaming/AscStreamingSource.cs
public sealed class AscStreamingSource : IStreamingTraceSource
{
    // 工厂注入：() => File.OpenRead(path)。移动端给 app 私有目录的缓存副本路径。
    // 流式路径不使用 ReplayOptions.MaxFileSizeBytes；移动端 P1 不设硬 cap，UI 负责在 >500MB 时确认。
    public AscStreamingSource(Func<Stream> streamFactory, ILogger? logger = null);
}
```

- 逐行惰性解析，**不物化** `List<string>`；帧产出逻辑复用现有 `AscParser` 的行解析路径（`DataLineParserFlow.TryParseDataLine` 等，按需在 Core 内部提为共享 internal 助手）
- 畸形行：沿用"跳过 + 计数 + 日志"语义，计数进 `StreamingParseStats.SkippedLines`
- **帧序**：流式保持文件序，不重排。批量解析器的 `frames.Sort` 是防御性排序；一致性对拍（§8）仅对时间有序 fixture 断言逐帧相等，乱序 fixture 的流式行为单独约定为"按文件序产出"并在该测试里显式断言

### 4.3 流式回放引擎

```csharp
// src/PeakCan.Host.Core/Replay/Streaming/StreamingTracePlayer.cs
public sealed class StreamingTracePlayer : IDisposable
{
    public StreamingTracePlayer(IStreamingTraceSource source, IReplayClock? clock = null, ILogger? logger = null);

    public ReplayState State { get; }              // 复用现有枚举
    public double CurrentTimestamp { get; }
    public double Speed { get; }                   // clamp [0.1, 100]；UI 档位 P1 实现时对齐桌面端现有档位

    public event Action<ReplayFrame>? FrameEmitted;            // 播放器线程；UI 自行 marshal
    public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;  // 复用现有 Args（含 Error）
    public event Action<double>? SeekProgress;                 // 快进扫描进度 0..1（按流位置估算）

    public Task PlayAsync(CancellationToken ct = default);
    public void Pause();
    public void Resume();
    public void SetSpeed(double multiplier);
    public Task SeekAsync(double timestamp, CancellationToken ct = default);
    public void Stop();
}
```

- **不提供 `Loop`**（YAGNI：手机端查看场景不需要 A/B 循环；桌面端该能力留在 `ReplayTimeline`）

- **预读缓冲**：`StreamingTracePlayer` 为每次 streaming session 创建容量 8192 的 `Channel<ReplayFrame>`。一个后台 producer 调用 `session.Frames` 解析帧并写入 channel；播放 consumer 从 channel 读取后调速 emit。channel 满时 producer 因 backpressure 停止解析。session 通过 `await using` 释放。
- **Seek(t)**：取消当前枚举 → `source.OpenAsync()` 重开 → 快进扫描（解析但不 emit，直到 `frame.Timestamp >= t`）→ 从该帧恢复播放。100MB 文本快进约数秒。快进扫描过程中上报 0..1 进度；遇到第一个目标帧或到达 EOF 时必须上报 `1.0`。Seek 完成后**回到进入前状态**（Playing→续播，Paused→停在该帧）。
- **PlaybackEnded**：仅在 EOF 或播放失败时触发；用户调用 `Stop()` 不触发该事件。`State` 仍变为 `Stopped`。
- 暂停/恢复/倍速语义对齐 `ReplayTimeline`（时钟用 `IReplayClock`，测试注入假时钟）。`SetSpeed` clamp 到 `[0.1, 100]`。

### 4.4 BLF（Phase 4）

`BlfStreamingSource`：顺序解压 LogContainer，**窗口重排缓冲**（2 秒时间窗内排序后产出），时间基准取**首帧时间戳**（批量解析器取全局最小值——语义有微小差异，对查看无影响，实现时在 xmldoc 记录）。Phase 4 前移动端不支持 BLF，UI 对 .blf 显示"后续版本支持"。

## 5. Mobile 项目组件

### 5.1 UI 信息架构

产品定位：**"快速确认 + 发现线索"工具**（现场收到微信发来的 trace，30 秒内确认"那个报文发没发出来"），深度分析回桌面。三个页面 + 一个下沉面板：

1. **文件页**：最近文件列表（名称/大小/帧数/时长/上次播放到哪）+ "打开文件"按钮；微信/文件管理器"用其他应用打开"经 intent-filter 直接落进 app
2. **表格 Tab**（核心）：顶部播放控制条（播放/暂停、倍速、时间轴 slider、当前时间/总时长）+ 过滤 chip 栏 + 帧表格（时间/ID/Data 3-4 列，等宽字体）
3. **图表 Tab（P3）**：底部导航切换；信号选择抽屉（DBC 消息树选 1-2 个）；曲线随播放生长 + 时间游标；横屏自动全屏
4. **帧详情面板**：点表格行 → 底部弹出该帧完整信息（原始字节 + DBC 全信号解码值）

**时间轴量程**：流式模式下总时长初始未知 → 打开文件后**后台只读扫描**全部时间戳（不建帧对象，100MB 约几秒），扫完 slider 获得量程、显示进度百分比；扫描期间 slider 显示 "??:??" 且不可拖。

### 5.2 项目结构与组件

```
src/PeakCan.Host.Mobile.Core/         # net10.0 纯逻辑库，可独立单测
├── Models/       FrameRow · FrameRingBuffer
├── Services/     DurationScanner · TraceFileCache（P2 加 TraceCacheStore）
├── Platform/     IUiDispatcher · IFilePickerGateway · IStreamingSourceFactory
└── ViewModels/   TraceSessionViewModel

src/PeakCan.Host.Mobile/              # net10.0-android MAUI app，薄 UI + Platform 实现
├── Views/        FilesPage · TracePage · FrameDetailSheet（ChartPage 为 P3）
├── Platform/     PlatformUiDispatcher · MauiFilePickerGateway · AscStreamingSourceFactory · TracePageFactory
└── MauiProgram.cs

PeakCan.Host.Mobile.Core.Tests/       # net10.0 xunit
```

> v2 修订：不再让 MAUI app 双 target `net10.0-android;net10.0`。VM/服务放进 `Mobile.Core`，MAUI app 保持单 Android target。`Mobile.slnx` 包含 Mobile、Mobile.Core、Host.Core 和测试项目；当 sibling `peakcan-hil-core` 项目存在时也加入 HIL.Core，否则通过 `Host.Core` 的 NuGet fallback 解析。

- **TraceSessionViewModel** 状态机：`Empty → Ready → Playing ⇄ Paused → Ended/Failed`；Seek 过程用 `IsSeekBusy` 表达，结束后回到进入前状态。`Ready` = 已打开未播放，进入时**预读第一屏 ~200 帧**填表；从 Ready 开始正式播放时清空预读 ring，避免重复 ingest。播放中修改 ID filter 也清空 ring，保证表格只保留过滤后的新帧。环形缓冲容量 5000 帧（≈1MB 内存）。UX 后果显式化：**P1 阶段表格只保留最近 5000 帧**，更早的帧随播放被淘汰；暂停回看完整数据要等 P2 的 SQLite 缓存
- **UI 节流与渲染策略**：播放器帧率可达数千/秒，禁止逐帧刷 UI——按 50ms 窗口批量 marshal；表格只渲染可视区（~30 行），数据源为环形数组 + 批量 `Reset` 通知，禁止逐行 `NotifyCollectionChanged`（MAUI CollectionView 逐行快速更新在 Android 上必卡）
- **可测性抽象层**：VM 不直接依赖 MAUI Essentials——`IUiDispatcher`（主线程 marshal）、`IFilePickerGateway`（选文件/缓存目录）定义在纯 net10.0 可见的位置，MAUI 实现放 `Platform/`。这是"net10.0 target 供 VM 单测"成立的前提
- **播放跟随语义**：默认自动跟随最新帧；用户上滑脱离跟随，浮出"↓回到最新"按钮（IM 语义，零学习成本）
- **文件直开**：`MainActivity` 注册 `.asc`/`.blf` intent-filter（`pathPattern`），微信/文件管理器"用其他应用打开"直接进 app
- **文件入口**：MAUI `FilePicker`（Android SAF）→ 拷贝到 `FileSystem.CacheDirectory` 一次（100MB 约 1-3s，显示进度）→ 之后用普通 seekable `FileStream`（支持 Seek 重开）。同名同尺寸文件直接复用缓存副本
- **DBC**（P2）：选 .dbc 文件 → `DbcParser.Parse` → 可见帧用 `SignalDecoder.Decode` 按需解码（只解码屏幕上的行，不全量解码）
- **图表**（P3）：LiveCharts2（SkiaSharp，MAUI 官方支持）；降采样 = min/max 桶（每像素列 1 桶），保留最近 30 万原始点供当前窗口

### SQLite schema（P2）

```sql
CREATE TABLE traces (
  trace_id INTEGER PRIMARY KEY,
  source_name TEXT NOT NULL,
  file_size  INTEGER NOT NULL,
  imported_at TEXT NOT NULL,
  frame_count INTEGER NOT NULL DEFAULT 0,
  duration    REAL NOT NULL DEFAULT 0,
  complete    INTEGER NOT NULL DEFAULT 0   -- 1 = 已流到文件尾
);
CREATE TABLE frames (
  trace_id INTEGER NOT NULL REFERENCES traces(trace_id),
  idx INTEGER NOT NULL,
  timestamp REAL NOT NULL,
  can_id INTEGER NOT NULL,
  is_extended INTEGER NOT NULL,
  dlc INTEGER NOT NULL,
  data BLOB NOT NULL,
  PRIMARY KEY (trace_id, idx)
) WITHOUT ROWID;
CREATE INDEX idx_frames_ts ON frames(trace_id, timestamp);
CREATE INDEX idx_frames_id ON frames(trace_id, can_id);
```

- 写入：后台队列批量 insert，5000 帧/事务
- 匹配重开：`source_name + file_size` 相同且 `complete=1` → 跳过拷贝直接进"完整浏览"模式（冲突概率可接受，查看工具不要求强一致）
- 用 `Microsoft.Data.Sqlite`（不用 EF Core，YAGNI）

## 6. 数据流（播放主链路）

```
FilePicker → 拷贝到 app 缓存 → FileStream
  → AscStreamingSource.OpenAsync → header + 惰性帧流
  → StreamingTracePlayer（预读 8192 帧缓冲）
      ├─→ 50ms 批量 marshal → 帧表格（环形 5000）          [P1]
      ├─→ SignalDecoder 按需解码可见行 → 信号列            [P2]
      ├─→ min/max 桶降采样 → LiveCharts2 曲线实时生长      [P3]
      └─→ TraceCacheStore 后台批量写                       [P2]
Seek(t) → 停枚举 → 重开 stream → 快进扫描（报进度）→ 续播    [P1]
打开文件后并行：DurationScanner 只读扫描全部时间戳（不建帧对象）→ slider 获得量程 [P1]
```

## 7. 错误处理

| 场景 | 行为 |
|---|---|
| 畸形数据行 | 跳过 + 计数，UI 顶部"已跳过 N 行"摘要条（沿用桌面语义） |
| 流中途 IO 错 / SAF 权限失效 | `PlaybackEndedEventArgs.Error` + 用户可读 Snackbar 提示 |
| SQLite 写失败 | 降级：缓存停用、回放继续，不阻塞主链路 |
| 超大文件 | 流式后内存与文件大小解耦，不设硬 cap；>500MB 弹确认（提示拷贝/扫描耗时） |
| 播放中文件被外部删除 | 下一次 Read 抛 IO → 同上 Error 路径 |
| 切后台/熄屏 | `App.OnSleep` 接线：暂停播放 + 冻结时钟基准，回前台可续播 |

## 8. 测试策略

- **Host.Core.Tests（xunit，现有 harness）**：
  - 流式 vs 批量解析**结果一致性对拍**：现有 ASC fixture（`tests/PeakCan.Host.App.Tests/Fixtures/Can/*.asc`，通过 linked content 引入 Core.Tests）加人工乱序 fixture。时间有序文件逐帧相等；乱序文件单独断言“流式按文件序、批量 parser 按时间排序”。
  - `StreamingTracePlayer`：注入假 `IReplayClock` 和可控 source/channel，测调速、暂停/恢复、Seek 快进落点、SeekProgress=1、EOF、失败路径。测试不得使用 `Thread.Sleep` 或真实 `Task.Delay` 等待并发时序。
- **Mobile VM 单测**（纯 `net10.0` target）：状态机迁移、环形缓冲溢出淘汰、过滤变更清空旧 ring、50ms 节流批量、SQLite 缓存读写（P2 内存模式 `:memory:`）。
- **真机验收 checklist（手动）**：见 §10 验收标准
- 覆盖率目标沿用仓库标准：Core 新增代码 ≥80%

## 9. 分期计划

| Phase | 内容 | 出口标准 |
|---|---|---|
| **P0** 环境+骨架 | 装 maui-android workload + Android SDK + JDK17；建 Mobile 项目 + slnx；真机部署空壳 | 空 app 在真机跑起来，`adb` 可见 |
| **P1** 核心闭环 | Core 流式 ASC API + StreamingTracePlayer；文件入口（FilePicker + intent-filter 直开）；播放/暂停/倍速/Seek；帧表格（环形缓冲+节流+跟随语义）；播放中 ID 过滤（emit 谓词 + 清空旧 ring）；后台总时长扫描 | 冷导入 <5s；缓存命中后点击播放首帧 <2s；1x 播放流畅 |
| **P2** 分析能力 | DBC 加载 + 信号列；SQLite 回放缓存；暂停回看；播完后完整过滤浏览（SQL `WHERE can_id`） | 加载 DBC 后信号列正确；重开秒开 |
| **P3** 图表 | LiveCharts2 集成；1-2 信号曲线随回放生长；降采样 | 曲线与表格时间游标同步 |
| **P4** BLF | `BlfStreamingSource`（窗口重排缓冲） | .blf 可流式回放 |

实施计划（writing-plans）先做 P0+P1，后续 Phase 各自再出计划。

## 10. 验收标准（性能预算）

| 指标 | 目标 | 测量方式 |
|---|---|---|
| 冷导入耗时（首次 100MB ASC） | <5s | 点击文件到 TracePage 显示 ready；拷贝进度可见 |
| 首帧延迟（缓存命中，点击播放到首行） | <2s | 真机秒表；不包含首次拷贝 |
| 1x 播放 | 不丢帧、不卡顿 | 播放器 `FramesEmitted` vs 扫描 FrameCount + 目测 |
| 内存 | 稳态 TOTAL PSS <300MB | `adb shell dumpsys meminfo <pkg>` 取 TOTAL PSS |
| Seek 到文件中点（100MB） | <5s + 进度反馈 | 真机秒表 |
| 表格滚动 | 播放中滚动不卡 | 目测 60fps 级 |
