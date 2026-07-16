# v3.52.0 MINOR — Trace Viewer 内置 AI 推理 v1

> P0 = 本地证据 + AnalysisSession + AI Analysis 面板 + ILlmProvider 接口 contract（无 Provider 实现）。
> 为 P1 接 DeepSeek / 其他 LLM Provider 预留接口骨架。

## 概述

在 Trace Viewer 中加入 AI 辅助故障分析 v1。复用现有绿/蓝双锚作为故障时刻与对照时刻，本地确定性提取 evidence，候选关联 + per-source 归一化排序，UI 渲染 Evidence + Candidates + Summary。**无在线 LLM 依赖、无 API Key 处理**；P1 PATCH 接真实 Provider。

## 用户可见变化

1. **AI Analysis 面板**（Trace Viewer 右侧 TabControl 第 3 个 Tab "AI Analysis"）
   - 空状态：提示拖绿/蓝锚并锁定
   - 激活后：FaultEvent header + Evidence DataGrid + Candidates DataGrid + Summary 文本
   - 顶部黄底横条：`本地推断（无归因；无 LLM 验证）`（醒目区分诚实信号）

2. **2 个新工具栏按钮**（在 ● 比较 ToggleButton 之后）
   - `🔒 锁定 anchor 状态` —— 锁定当前绿/蓝锚为 evidence snapshot 起点（双锚齐全才能锁定；蓝锚缺失弹提示阻止）
   - `🤖 运行分析` —— 触发 RunAnalysisAsync（`RunAnalysisCommand.CanExecute` 要求 `CurrentAnchorSnapshot != null && !IsLoading`）

3. **诚实信号**（UI 标识）
   - `未发现可靠关联（仅本地特征；无 LLM 归因）` —— 合法输出，不是 error
   - 黄底横幅 + Summary 文案明确"无归因；无 LLM 验证"

## 数据契约（Core 层，9 个新文件 + 1 个 IFrameSourceProvider 抽象）

```
src/PeakCan.Host.Core/Analysis/
├── AnchorSnapshot.cs           ── 双锚原始值快照（不持 Signal/DbcDocument 引用）
├── FaultEvent.cs               ── 中心时间点 + ±500ms 窗口
├── FaultAnalysisEvidence.cs    ── E-0001 起始 + signalKey + sourceId + 原始值
├── LocalReport.cs              ── Evidence + Candidates + Notes + Summary
├── AnalysisSession.cs          ── SessionId + Version (自增) + 完整生命周期
├── ILlmProvider.cs             ── interface contract（P0 仅 stub，P1 PATCH 填实现）
├── LlmAnalysisResult.cs        ── Summary + AttributedEvidenceIds + RawResponseJson + Error
├── EvidenceExtractor.cs        ── per-source 窗口裁剪 + byte[0] state-transition 检测
├── LocalAnalyzer.cs            ── 候选关联 + per-source 归一化排序
├── AnalysisSessionRegistry.cs  ── version-aware session store，独立于 VM Reset
└── IFrameSourceProvider.cs     ── Core 层抽象（防御 `LoadedFrames` 直接遍历的硬边界）
```

## 5 个核心决策

| ID | 决策 | 选择 |
|---|---|---|
| D1 | P0 范围 | 本地证据 + 面板 + ILlmProvider 接口 contract（无 Provider 实现） |
| D2 | AnchorSnapshot 双锚语义 | 必须双锚才能锁定；蓝缺失时弹提示阻止 |
| D3 | FaultEvent 时间窗口 | 中心时间点 + 默认 ±500ms（可调；最小 +10ms，最大 +5s） |
| D4 | 本地输出边界 | 明确"仅特征 + 候选 + 数据质量"，不归因；UI 标识 |
| D5 | Session 持久化 | 不入 `.tmtrace`，session 内存存活于 VM singleton |

## 14 条硬边界（继承自预评估，全部 COVERED）

详细见 `docs/superpowers/specs/2026-07-16-ai-inference-v1-design.md`。T4 引入 `IFrameSourceProvider` Core 抽象解决 layering；T9 注册 `TraceSessionRegistry` 同时实现 `ITraceSessionRegistry` + `IFrameSourceProvider`。

## 架构里程碑

- **首次 Trace Viewer AI 推理功能落地**（P0）
- **5 NEW 1/3 lesson candidates 观察成功**（+1 NEW from T9 dual-interface DI wiring）
- **Core 首次引入 `IFrameSourceProvider` 抽象** —— 防御直接遍历 `LoadedFrames` 风险
- **App/ViewModels `TraceViewerViewModel` 第 13 个 partial**（`AnalysisFlow.cs` + `AnchorSnapshotFlow.cs` 加入既有的 11 partials）

## 计数

- 13 commits on `feature/v3-52-0-ai-inference-v1`
- 11 NEW source files + 4 MODIFIED files
- ~1370 LoC 增量（spec 估算；实际 13 commit 净 LoC 详见 `git diff --stat f7aa316..87c74ed`）
- 26 NEW tests（T1-T8 + T4 layering fix）→ Core 483 + App 832 + Infra 89 = **1404 PASS / 0 FAIL / 5 SKIP**

## 6 NEW 1/3 lesson candidates（待 2nd 观察后晋升）

| 候选 | 期望观察点 |
|---|---|
| `anchorsnapshot-must-not-hold-signal-or-dbcdocument-references` | T7 verified |
| `analysis-session-lifecycle-must-be-independent-of-vm-reset` | T6 + T7 verified |
| `lock-anchor-snapshot-must-validate-both-anchors-present-before-snapshot` | T7 verified |
| `per-source-normalization-required-for-cross-source-candidate-ranking` | T5 verified |
| `local-report-must-explicitly-mark-no-attribution-no-llm` | T10 XAML verified |
| `dual-interface-DI-binding-via-cast-when-registration-uses-interface-type` | **NEW at T9** —— HOLD pending 2 more confirmations |

## 显式不在范围（推到后续 PATCH）

- **P1 PATCH**：DeepSeek / Azure OpenAI / Ollama 真实 Provider 实现 + API Key 安全存储（CredentialManager / DPAPI）+ JSON Schema 校验 + Evidence ID whitelist 过滤
- **P2+ PATCH**：流式 LLM / tool calling / 周期异常 + frame-loss + out-of-range 检测器 / `.tmtrace` session 持久化

## 关联

- Spec: `docs/superpowers/specs/2026-07-16-ai-inference-v1-design.md` (`56d3518`)
- Plan: `docs/superpowers/plans/2026-07-16-ai-inference-v1.md` (`f7aa316`)
- Pre-assessment: `docs/superpowers/plans/2026-07-16-trace-ai-analysis-preliminary-assessment.md`

## 验证

```bash
dotnet build PeakCan.Host.slnx -c Debug          # 0 errors, 0 warnings
dotnet test PeakCan.Host.slnx --no-build -c Debug # 1404 PASS / 0 FAIL / 5 SKIP
```