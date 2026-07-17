# W37 god-class refactor — AscLocator (22nd overall)

> 状态：W37 设计 spec（v3.54.0 series 之后；god-class refactor 节奏延续）
>
> 触发条件：W35 (PeakCanChannel 20th) + W36 (StatsViewModel 21st) + v3.52.0/v3.52.1/v3.53.0/v3.53.1/v3.54.0 ships 均已完成。继续 W3-W35 god-class refactor 节奏。W24 plan 中的 DbcSendViewModel refactor 已在 v3.51.0 ship（32d14d5）；v3.52.0/53.0 期间还有 DbcTokenizer/AscLocator/RecordService 等 god-class 候选待 refactor。W37 = AscLocator（225 LoC 真 god-class，4 职责清晰）。

## 目标

`src/PeakCan.Host.Core/Services/AscLocator.cs` 225 LoC → 拆分为 `AscLocator/` 子目录 + 3 NEW partials。Public API + tests + DI 全部不变。

## 当前代码证据（已坐实）

- `src/PeakCan.Host.Core/Services/AscLocator.cs:225` — `public sealed partial class FileSystemAscLocator : IAscLocator` (line 46)
- 1 ctor public + 1 ctor internal (test) + 1 const `MaxSearchDepth` + 1 property `SearchDirsPath`
- 5 readonly fields (`_logger` + `_hasher` + `_cacheGate` + `_cachedDirs`)
- 1 public `LocateAsync` (entry, ~14 LoC)
- 2 private recursive methods `WalkAsync` (~64 LoC) + `RecurseAsync` (~13 LoC)
- 2 private cache methods `GetSearchDirs` (~9 LoC) + `LoadSearchDirsFromDisk` (~16 LoC)
- 1 static `DefaultSearchDirsPath` (~5 LoC)
- 5 `[LoggerMessage]` partial methods (5 lines)
- 4 distinct responsibilities: locate orchestration + recursive walk + search-dirs cache + logging

## 5 个 D 决策（D1-D5）

| ID | 决策 | 选择 | 理由 |
|---|---|---|---|
| D1 | NEW partials 数量 | **3 NEW** (`LoggingFlow.partial.cs` + `SearchDirsFlow.partial.cs` + `LocateFlow.partial.cs`) | 4 职责清晰拆分；sister of W22 RecentSessionsService + W34 DbcSendViewModel |
| D2 | Partial keyword | 已有（L46 `public sealed partial class FileSystemAscLocator : IAscLocator`）—— 不改 | W19 R1 sister，无需加 keyword |
| D3 | 留在 main 的成员 | `IAscLocator` interface + `FileSystemAscLocator` class declaration + 1 const (`MaxSearchDepth`) + 1 property (`SearchDirsPath`) + 2 readonly fields (`_logger` + `_hasher` + `_cacheGate`) + public ctor + internal test ctor + `LocateAsync` public entry | 与 W19 + W23 + W34 + W36 sister：bindable properties + state stays in main |
| D4 | LARGEST method `WalkAsync` (~64 LoC) | 移到 `LocateFlow.partial.cs` | 这是真 god-class refactor 的核心；与 W36 D4 upgrade 不同（ctor-as-orchestrator），这是 long-method-as-orchestrator |
| D5 | `DefaultSearchDirsPath` static helper 位置 | 移到 `SearchDirsFlow.partial.cs` | 与 `LoadSearchDirsFromDisk` + `GetSearchDirs` 同 partial；cache + load 同 responsibility |
| D6 | 分支 | `feature/w37-asc-locator-god-class` | sister of W11-W35 |
| D7 | 顺序 | T1 LoggingFlow (least coupled, 5 lines) → T2 SearchDirsFlow (cache + load) → T3 LocateFlow (main recursive logic) | T1 验证 partial extract 基础；T2 验证 cache 拆分；T3 main 完整 |

## LoC trajectory（公式 W8.5 D7 32-locked）

| Task | Flow | Range (TBD per Phase 1 exact grep) | LoC deleted | Marker | LoC main after |
|---|---|---|---|---|---|
| T1 | Logging | L212-225 (5 [LoggerMessage] partials) | ~14 | 1 | ~211 |
| T2 | SearchDirs | L178-210 (`GetSearchDirs` + `LoadSearchDirsFromDisk` + `DefaultSearchDirsPath`) | ~30 | 1 | ~181 |
| T3 | Locate | L98-176 (`WalkAsync` + `RecurseAsync`) | ~79 | 1 | ~102 |
| T4 | release + ship | (no source) | 0 | 0 | ~102 |

**目标**：main 225 → ~102 LoC（-123 LoC, -55%）。Subdirectory 累计 3 NEW partials + main = 125 LoC distributed across 4 files。

## 架构

```text
src/PeakCan.Host.Core/Services/
├── AscLocator.cs                          (NEW: ~102 LoC, was 225 LoC)
└── AscLocator/                            (NEW subdirectory, sister of W34 DbcSendViewModel/)
    ├── LoggingFlow.partial.cs              (NEW: ~14 LoC, 5 [LoggerMessage] partials)
    ├── SearchDirsFlow.partial.cs           (NEW: ~30 LoC, cache + load + default path)
    └── LocateFlow.partial.cs               (NEW: ~79 LoC, WalkAsync + RecurseAsync)
```

## W20 + W23 LESSON APPLIED

Per W20 T2 R1 fabrication incident + W23 T2 3-fix-cycle struct-constructor fabrication:

1. **Re-grep boundaries BEFORE running each deletion script** (re-verify before each T1/T2/T3 script run).
2. **Re-extract original code from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`** for each partial's verbatim content.
3. **Verify `IAscContentHasher` interface** (Core, sister file at `src/PeakCan.Host.Core/Services/AscContentHasher.cs` or similar) — confirm `ComputeAsync` signature.
4. Verify `IAscLocator` interface methods + `LocateAsync` signature preserved (consumed by `TraceSessionRegistry.cs` etc. — sister of T8 in v3.52.0).

## 组件拆分（part 文件位置）

**Main stays** (~102 LoC):
- using block (1-3) + namespace + interface xmldoc (5-23) + `IAscLocator` interface (24-35) + class xmldoc (37-45) + outer class declaration (46)
- 1 const `MaxSearchDepth` (L51)
- 1 property `SearchDirsPath` (L56)
- 4 readonly fields (`_logger` + `_hasher` + `_cacheGate` + `_cachedDirs`)
- public ctor (L63-66) + internal test ctor (L69-77)
- `LocateAsync` public entry (L80-94)

**LoggingFlow.partial.cs** (~14 LoC):
- 5 `[LoggerMessage]` partial methods (L212-225)

**SearchDirsFlow.partial.cs** (~30 LoC):
- `GetSearchDirs` (L178-187)
- `LoadSearchDirsFromDisk` (L189-204)
- `DefaultSearchDirsPath` (L206-210)

**LocateFlow.partial.cs** (~79 LoC):
- `WalkAsync` (L98-161)
- `RecurseAsync` (L163-176)

## 数据契约

不引入新 record / interface。Public API 100% 保留：
- `IAscLocator` interface + `LocateAsync(string, CancellationToken)` method
- `FileSystemAscLocator` class
- `MaxSearchDepth` const + `SearchDirsPath` property
- 2 ctors (public + internal test)

## 错误处理

不引入新错误处理路径。所有现有 try/catch + `[LoggerMessage]` partial 模式 verbatim 保留。

## 测试策略（TDD RED → GREEN → IMPROVE）

- 现有 `FileSystemAscLocatorTests` 测试集必须全部保留 + PASS。
- 全套 1456 PASS / 0 FAIL / 5 SKIP（v3.54.0 ship baseline）不变。
- 不改 tests 文件。
- 不引入新 tests（refactor PATCH 通常零 test change）。

## 复评/不做项

- **AscLocator 子目录 vs 同目录**：明确用子目录（sister of W34 DbcSendViewModel/）。
- **IAscContentHasher DI 注入 vs ctor 内构造**：明确不 DI（sister of W36 D4）。
- **分更多 partials**（如 CacheInvalidationFlow / WalkStateFlow）：明确不做（YAGNI；3 partials 足够）。
- **W38+ 后续 refactor**（DbcTokenizer / DbcSendViewModel / RecordService）：明确推到后续。
- **IAscLocator interface 移 Core/Services/AscLocator/IAscLocator.cs**：明确不做（interface + class 都在 main；partial 拆分只针对 class）。

## 6 NEW 1/3 lesson candidates 观察

| 候选 | 期望观察点 |
|---|---|
| `largest-method-can-move-when-flow-is-discrete-walk-not-orchestrator` | W37 1st observation: WalkAsync (~64 LoC) 整段移到 LocateFlow（sister of W36 D4 ctor variant but with WalkAsync long-method variant） |
| `recursive-walk-must-stay-coupled-with-recurse-helper-in-same-partial` | W37 1st observation: WalkAsync + RecurseAsync 同 partial 不可分（递归相互调用） |
| `cached-search-dirs-must-stay-coupled-with-load-and-default-path-helpers-in-same-partial` | W37 1st observation: cache 状态 + 加载 + 默认路径同 partial |
| `logger-message-partials-can-be-extracted-to-isolated-logging-partial-when-class-has-5-plus-log-calls` | W37 1st observation: 5 个 [LoggerMessage] partial → LoggingFlow 单独 partial |
| `3-partial-subdirectory-pattern-empirical-w34-w37` | NEW at W37: W34 DbcSendViewModel 3 partials + W37 AscLocator 3 partials |
| `cache-gate-locks-once-and-only-on-first-read-pattern-emerges-when-class-lazy-loads-from-disk` | NEW at W37: Double-checked locking pattern in `GetSearchDirs` |

## 文件 / LoC 估算

| 文件 | LoC |
|---|---|
| `src/PeakCan.Host.Core/Services/AscLocator.cs` | ~102 (was 225) |
| `src/PeakCan.Host.Core/Services/AscLocator/LoggingFlow.partial.cs` | ~14 (NEW) |
| `src/PeakCan.Host.Core/Services/AscLocator/SearchDirsFlow.partial.cs` | ~30 (NEW) |
| `src/PeakCan.Host.Core/Services/AscLocator/LocateFlow.partial.cs` | ~79 (NEW) |
| **总计** | **~225** (no net change; redistributed) |

## 待 SPEC 用户复核

本文为 spec 初稿。请 review 后批准，下一步进入 writing-plans 写实施计划。