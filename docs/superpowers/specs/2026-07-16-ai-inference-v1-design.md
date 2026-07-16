# Trace Viewer 内置 AI 推理 v1 设计

> 状态：v1 设计（**P0 = 本地证据 + AnalysisSession + 面板；ILlmProvider 接口预留，无 Provider 实现**）
>
> 基础评估：`docs/superpowers/plans/2026-07-16-trace-ai-analysis-preliminary-assessment.md`（已锁定 14 条硬边界 + 11 项事实坐实）
>
> 触发条件：v3.51.0 MINOR BLF parser 已 shipped（commit `c1d0933` + `6abaf4b` + `3b8b8f5` + 5 个 source commits + squash merge）。v3.51.0 完成了 BLF 真实 Vector 99,976 帧的 IO 契约验证，可推进 v1 AI 推理。

## 目标

在 Trace Viewer 中加入 AI 辅助故障分析 v1。本地确定性证据 + 候选关联 + UI 报告。不引入跨网络依赖、不引入 API Key 处理；为 PATCH v1.x 接 DeepSeek 等 LLM Provider 预留接口骨架。

## 当前代码证据（已坐实）

- Trace Viewer 主界面：`src/PeakCan.Host.App/Views/TraceViewerView.xaml:1-340`（右侧已有 `TabControl` 含 `Watch List` + `Sampling Table`，AI 面板作第三个 Tab 同级）。
- Trace Viewer VM：`src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` + 11 partials in `TraceViewerViewModel/`。
- 绿/蓝锚点：`GreenLineAnchorFlow.cs:80-86` (`RefreshAtAnchor`)、`BlueLineAnchorFlow.cs:79-85` (`RefreshAtAnchorBlue`)。
- 双锚结果：`WatchedSignalRow.LatestValue` (L113) + `BlueLatestValue` (L144) + `DeltaValue = BlueLatestValue - LatestValue` (L167-170) + `LatestText` + `BlueText` (v3.50.5 + `SignalDecoder.TryDecodeEnumText` 走通) + `DeltaText` (v3.50.5 enum "—" 退化)。
- `SignalKey` 格式：`WatchedSignalRow.cs:263-266` `{idHex}.{signalName}[.{sourceId}]`，跨源寻址已有键。
- 多源 overlay：`src/PeakCan.Host.App/Services/Trace/ITraceSessionRegistry.cs:1-17`，palette 容量 10。
- 帧模型：`src/PeakCan.Host.Core/Replay/ReplayFrame.cs:1-11` 不可变 record，`Timestamp is seconds from recording start`，`double`。
- 帧复制边界：`ITraceSessionRegistry.GetFrames` 每次返回 fresh `ReplayFrame[]`（`TraceSessionRegistry.cs:122-133`），浅 copy，`byte[] Data` 不复制；source unload 后安全返回 `Array.Empty<ReplayFrame>()`（`:122-125`）。
- ITraceViewerService sister：`src/PeakCan.Host.Core/Replay/ITraceViewerService.cs:1-46`，**INSPECTION ONLY**，不带 `IReplayFrameSink`（`TraceViewerServiceTests.cs:28-43` 反射守住）。
- DBC 解码：`src/PeakCan.Host.Core/Dbc/SignalDecoder.cs:21-115`（bit extraction + scale/offset + enum text 走 `TryDecodeEnumText` L141-152）。AI evidence 不重新解码。
- v3.51.0 BLF parser 落地：`BlfParser.cs` + `BlfParser/` subdirectory，输出 `IReadOnlyList<ReplayFrame>`，与 ASC 同单位。
- 全项目无 `HttpClient` / `Llm` / `AI` / `DeepSeek` / `OpenAi` 命中（grep 已验证）—— 全新边界。

## 5 个 D 决策（D1-D5）

| ID | 决策 | 选择 | 理由 |
|---|---|---|---|
| D1 | P0 范围 | 本地证据 + AnalysisSession + 面板 + `ILlmProvider` 接口 contract（无实现） | 收敛 scope 到无网络依赖；Mock Provider 也 YAGNI（无调用方）；接口定义清楚即可让 P1 PATCH 只填实现 |
| D2 | AnchorSnapshot 双锚语义 | 必须双锚才能"锁定 snapshot"；蓝锚 NaN 时弹提示阻止锁定 | 单锚 = 伪对照破坏核心证据价值；UI 已有 ● 比较 toggle 引导设蓝锚 |
| D3 | FaultEvent 时间窗口 | 中心时间点 + 默认 ±500ms（可调） | 车用 CAN 故障分析经验值；token 预算 + UI 状态可预测；避免大迹 OOM |
| D4 | 本地输出边界 | 明确"仅特征 + 候选 + 数据质量提示"，不归因；UI 标识"本地推断（无归因）" | 预评估硬边界第 8 条已坐实诚实信号；P0 不与 P1 LLM 价值混淆 |
| D5 | Session 持久化 | 不入 `.tmtrace`，session 内存存活于 VM singleton | EvidenceExtractor 确定性，重启后 1 步重建；避开版本对齐复杂度 |

## 硬边界（继承自预评估，已锁定 14 条不变）

1. AI 不直接读完整 LOG / 完整 DBC；只接收窗口内 evidence + 允许的 raw frame。
2. AI evidence 来源必须与 UI 显示同源（`WatchedSignalRow.LatestValue` / chart `YValues`），不替代本地 `SignalDecoder`。
3. AI 只能走 `ITraceViewerService`（INSPECTION ONLY），不能走 `IReplayService` 写总线。
4. 无 Provider 时本地确定性分析仍可用，UI 标识"本地推断（无归因）"。
5. 改变日志/DBC/fault event/时间窗口/source 集合/信号选择 → 新 session 版本，不静默改旧结果。
6. 大日志走 `ITraceSessionRegistry.GetFrames` copy 边界或 windowed/indexed reader，不直接遍历 `LoadedFrames`（无 copy）。
7. 候选信号键统一用 `SignalKey`（`{idHex}.{signalName}[.{sourceId}]`），跨源必须带 sourceId。
8. AI session 生命周期独立于 `TraceViewerViewModel.Reset()`；状态 + candidate + evidence snapshot 自带版本号。
9. `AnchorSnapshot` 含双锚（`{greenTs, blueTs, perSignal(latestValue, blueLatestValue, deltaText, latestText, blueText, signalKey, sourceId)}`），不重新构造对照。
10. `AnchorSnapshot` 是原始值快照（`double` + enum text string + signalKey），**不持** `Signal` / `DbcDocument` / `WatchedSignalRow` 引用。
11. 蓝锚缺失时 `AnchorSnapshot` 退化为 only-green 单点（`DeltaValue` / `DeltaText` 都是 NaN / "—"），UI 显式提示。**D2 升级：UI 阻止锁定而不是允许 only-green 单点。**
12. 读取 anchor-time 值必须在 `RefreshAtAnchor` / `RefreshAtAnchorBlue` 同步返回后；P0 阶段"显式 `锁定 anchor 状态` 按钮 + 复制快照"。
13. DeepSeek evidence ID 引用无效时 **whitelist 过滤优先**（保留合法 Evidence ID 条目，丢弃非法引用及其关联 claim），整体拒绝只作兜底。
14. 跨源候选排序采用 per-source 归一化（每个 source 内独立计算 Δ / 变化率 / 状态切换次数），避免帧数多 source 霸榜。

## 架构

```text
ASC / BLF
   ↓
统一加载链路（仅 ITraceViewerService，INSPECTION ONLY，v3.51.0 复用同一加载点）
   ↓
ReplayFrame（多源 overlay，SignalKey 含 sourceId）
   ↓
本地 DBC 解码 + WatchedSignalRow 已算 Latest/Blue/Delta
   ↓
[用户拖绿锚 + 拖蓝锚 → RefreshAtAnchor / RefreshAtAnchorBlue]
   ↓
[用户点『锁定 anchor 状态』→ 双锚齐全 → 创建 AnchorSnapshot]
   ↓
FaultEvent（中心时间点 + ±500ms 窗口，默认值 UI 可调）
   ↓
EvidenceExtractor（per-source 读 ITraceSessionRegistry.GetFrames copy 边界 + 时间窗裁剪 + 本地特征）
   ↓
FaultAnalysisEvidence（含 E-xxx ID + signalKey + sourceId + 原始值 + Δ + 时间戳 + 类型标签）
   ↓
LocalAnalyzer（候选关联：同 message / 同 signal group / Δ ≠ 0 / 状态切换 / 周期异常 / 丢帧 / 越界 / 跨源 per-source 归一化排序）
   ↓
LocalReport（特征摘要 + 候选排序 + 数据质量提示 + "未发现可靠关联" 是合法输出）
   ↓
AnalysisSession（fault event + anchor snapshot + evidence list + report + version，不持久化）
   ↓
AI Analysis 面板（Trace Viewer 右侧 TabControl 第 3 个 Tab）
   ↓
可选 LLM Provider（ILlmProvider 接口预留，P0 无实现；P1 PATCH 填 DeepSeek 实现）
```

## 数据契约

### AnchorSnapshot（**不持 Signal / DbcDocument 引用**）

```csharp
public sealed record AnchorSnapshot(
    double GreenTimestampSeconds,
    double BlueTimestampSeconds,
    IReadOnlyList<AnchoredSignalValue> Signals,
    DateTime CapturedAtUtc,
    int Version);

public sealed record AnchoredSignalValue(
    string SignalKey,        // {idHex}.{signalName}[.{sourceId}]
    string SourceId,
    double LatestValue,      // 绿锚时刻解码值（双锚都有时才有意义）
    double BlueLatestValue,  // 蓝锚时刻解码值
    double DeltaValue,       // BlueLatestValue - LatestValue；only-green 时 = double.NaN
    string LatestText,       // enum text 或 "12.34"
    string BlueText,         // 同上，only-green 时 = "—"
    string DeltaText);       // only-green 时 = "—"
```

### FaultEvent

```csharp
public sealed record FaultEvent(
    double CenterTimestampSeconds,
    TimeSpan WindowBefore,
    TimeSpan WindowAfter,
    string Description,
    DateTime CreatedAtUtc);
```

### FaultAnalysisEvidence

```csharp
public sealed record FaultAnalysisEvidence(
    string EvidenceId,                // E-0001
    string SignalKey,
    string SourceId,
    string Type,                      // "state-transition" / "delta-spike" / "cycle-anomaly" / ...
    double TimestampSeconds,
    double Value,
    string? EnumText,                 // 走 SignalDecoder.TryDecodeEnumText
    string Description);             // 人类可读（"BmsFaultState fault → Active"）
```

### LocalReport

```csharp
public sealed record LocalReport(
    IReadOnlyList<FaultAnalysisEvidence> Evidence,
    IReadOnlyList<CandidateSignal> Candidates,
    IReadOnlyList<string> DataQualityNotes,
    string Summary,                   // 明确"无归因，仅特征"
    DateTime GeneratedAtUtc);

public sealed record CandidateSignal(
    string SignalKey,
    string SourceId,
    double Score,                     // per-source 归一化分（0..1）
    string ReasonText,                // "Δ=-580 rpm, 116ms before fault"
    IReadOnlyList<string> EvidenceIds);  // 引用 E-xxx 列表
```

### AnalysisSession

```csharp
public sealed record AnalysisSession(
    Guid SessionId,
    int Version,                      // 自增；任一输入变化 → 新 session
    FaultEvent FaultEvent,
    AnchorSnapshot AnchorSnapshot,
    LocalReport Report,
    DateTime CreatedAtUtc);
```

### ILlmProvider（P0 接口 contract，无实现）

```csharp
public interface ILlmProvider
{
    string DisplayName { get; }
    Task<LlmAnalysisResult> AnalyzeAsync(
        AnalysisSession session,
        CancellationToken ct);
}

// P0 空 stub：NotImplementedException with "P1 PATCH: implement LLM Provider"
public sealed class NotImplementedLlmProvider : ILlmProvider
{
    public string DisplayName => "(no LLM — P0 local-only)";
    public Task<LlmAnalysisResult> AnalyzeAsync(...) =>
        throw new NotImplementedException("P1 PATCH will implement DeepSeek provider");
}
```

## 组件拆分（part 文件位置）

新增到 `src/PeakCan.Host.Core/Analysis/`（**Core 层**，不依赖 App）：

```
src/PeakCan.Host.Core/Analysis/
├── AnchorSnapshot.cs           (~30 LoC, record + AnchoredSignalValue record)
├── FaultEvent.cs               (~25 LoC, record)
├── FaultAnalysisEvidence.cs    (~30 LoC, record)
├── LocalReport.cs              (~40 LoC, record + CandidateSignal record)
├── AnalysisSession.cs          (~25 LoC, record)
├── ILlmProvider.cs             (~20 LoC, interface + NotImplementedLlmProvider stub)
├── EvidenceExtractor.cs        (~150 LoC, per-source 窗口裁剪 + 本地特征提取)
├── LocalAnalyzer.cs            (~120 LoC, 候选关联 + per-source 归一化排序)
└── AnalysisSessionRegistry.cs  (~50 LoC, version-aware session store，独立于 VM Reset)
```

新增到 `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/`：

```
TraceViewerViewModel/AnchorSnapshotFlow.cs    (~80 LoC, "锁定 anchor" 命令 + 双锚校验)
TraceViewerViewModel/AnalysisFlow.cs          (~120 LoC, AnalysisSession 创建 + 报告生成 + ILlmProvider 调用)
```

新增到 `src/PeakCan.Host.App/Views/`：

```
Views/TraceViewerView.AIPanel.xaml    (~80 LoC, 右侧 TabControl 第 3 个 Tab "AI Analysis")
```

## UI 接线

Trace Viewer 右侧 `TabControl` 增加第 3 个 Tab `AI Analysis`（sister of Watch List + Sampling Table）：

- **空状态**：当 AnalysisSession 为 null，显示提示文字"请先拖绿/蓝锚到故障时刻和对照时刻，然后点『锁定 anchor 状态』"。
- **报告展示**：AnalysisSession 非空时显示：
  - FaultEvent header（中心时间点 + 窗口范围 + 描述）
  - "本地推断（无归因）"标识（醒目色块）
  - Evidence 列表（DataGrid，EvidenceId / SignalKey / Type / Value / Description）
  - 候选关联列表（DataGrid，SignalKey / SourceId / Score / ReasonText）
  - 数据质量提示列表
  - "跳回 trace 时间点"按钮（每个 candidate 行带 → 按钮，调用 `_masterService.Seek(timestamp)`）
- **操作按钮**：
  - "锁定 anchor 状态"（在工具栏，紧邻 ● 当前 / ● 比较 toggle 之后）
  - 蓝锚缺失时弹提示窗"必须先设比较锚"（D2 升级版）

## 错误处理

- `EvidenceExtractor` 读 `GetFrames` 返回 `Array.Empty` → 候选列表为空 + 数据质量提示"源已卸载或无帧"。
- 窗口内无任何 state-transition → 候选列表为空 + 数据质量提示"窗口内无显著变化"。
- `LocalAnalyzer` 无候选 → `Summary = "未发现可靠关联"`，UI 渲染为明显标识（不是 error state）。
- ILlmProvider 调用 → P0 永远走 `NotImplementedLlmProvider`；UI 检测 `DisplayName == "(no LLM — P0 local-only)"` 时**不显示** LLM 章节（避免用户困惑）。

## 测试策略（TDD RED → GREEN → IMPROVE）

- 单元测试（Core）：
  - `AnchorSnapshotTests`：双锚 / only-green / 跨源 signalKey 格式
  - `EvidenceExtractorTests`：窗口裁剪 / per-source 独立提取 / 空源 / 大窗口边界
  - `LocalAnalyzerTests`：候选排序 per-source 归一化 / Δ ≠ 0 / 状态切换 / 周期异常 / 丢帧
  - `LocalReportTests`："未发现可靠关联" / 数据质量提示 / 候选空列表
  - `AnalysisSessionRegistryTests`：版本自增 / 输入变化生成新 session / Reset 不清空（独立于 VM）
- 集成测试（App）：
  - `AnchorSnapshotFlowTests`：`RefreshAtAnchor` + `RefreshAtAnchorBlue` 同步后 → `LockAnchorCommand` → snapshot 创建；蓝锚缺失时命令不执行 + ErrorMessage 设置
  - `AnalysisFlowTests`：完整 session 创建流程（mock ITraceSessionRegistry + DBC fixture）
- UI smoke：手测 1 个 fixture（真实 BLF 或 ASC），验证面板渲染。

## 复评/不做项

- **BLF 真实 fixture 验证**：v3.51.0 已 ship 并验证 99,976 帧 IO；本 spec 直接复用 `BlfParser`。
- **Provider 实现（DeepSeek）**：明确推到 P1 PATCH。P0 仅 `NotImplementedLlmProvider` stub。
- **API Key 安全存储 / CredentialManager / DPAPI**：明确推到 P1 PATCH。
- **流式 LLM / tool calling / json_schema 强约束**：明确推到后续 PATCH。
- **本地模型 Provider**：明确不做（用户场景客户端部署，本地推理不是目标）。
- **多日志对比 / 全量自动故障扫描 / CAN 网络健康高级分析 / AI 控制 CAN**：明确不做。
- **Session 持久化到 .tmtrace**：明确不做（D5 决策）；重启后重新点"锁定 anchor"重建。

## 6 个待观察 lesson 候选（NEW 1/3，待 2nd 观察）

| 候选 | 期望观察点 |
|---|---|
| `anchorsnapshot-must-not-hold-signal-or-dbcdocument-references` | v1.0 1st 观察：TraceViewerViewModel.Reset 后 snapshot 仍能展示已捕获原始值 |
| `analysis-session-lifecycle-must-be-independent-of-vm-reset` | v1.0 1st 观察：Reset 触发后 session 不消失，Evidence 仍可访问 |
| `llm-evidence-id-whitelist-filter-prevents-whole-response-discard` | P1 PATCH 1st 观察（不在 v1.0 范围） |
| `per-source-normalization-required-for-cross-source-candidate-ranking` | v1.0 1st 观察：2 source 帧数比 100:1 时排序仍按归一化分 |
| `lock-anchor-snapshot-must-validate-both-anchors-present-before-snapshot` | v1.0 1st 观察：D2 决策的 UI 实现 |
| `local-report-must-explicitly-mark-no-attribution-no-llm` | v1.0 1st 观察：UI 标识 vs LLM 章节边界清晰 |

## 文件 / LoC 估算

| 文件 | LoC | 备注 |
|---|---|---|
| `Core/Analysis/AnchorSnapshot.cs` | 30 | record + 子 record |
| `Core/Analysis/FaultEvent.cs` | 25 | record |
| `Core/Analysis/FaultAnalysisEvidence.cs` | 30 | record |
| `Core/Analysis/LocalReport.cs` | 40 | record + CandidateSignal |
| `Core/Analysis/AnalysisSession.cs` | 25 | record |
| `Core/Analysis/ILlmProvider.cs` | 20 | interface + stub |
| `Core/Analysis/EvidenceExtractor.cs` | 150 | per-source 窗口裁剪 + 特征提取 |
| `Core/Analysis/LocalAnalyzer.cs` | 120 | 候选关联 + 归一化排序 |
| `Core/Analysis/AnalysisSessionRegistry.cs` | 50 | version-aware session store |
| `App/.../AnchorSnapshotFlow.cs` | 80 | LockAnchorCommand + 双锚校验 |
| `App/.../AnalysisFlow.cs` | 120 | session 创建 + 报告生成 |
| `Views/TraceViewerView.AIPanel.xaml` | 80 | 第 3 Tab |
| 测试 | ~600 | 8 个 test 文件 |
| **总计** | **~1370** | 增量 |

## 待 SPEC 用户复核

本文为 spec 初稿。请 review 后批准，下一步进入 writing-plans 写实施计划。