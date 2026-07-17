# v3.55.0 MINOR — AscLocator god-class refactor (22nd overall)

> W37 god-class refactor (W3-W36 series 延续，22nd god-class SHIP)。

## 概述

`AscLocator.cs` 225 LoC → 94 LoC main（**-131 LoC, -58.2%**）。3 NEW partials 在 `AscLocator/` 子目录：LoggingFlow.partial.cs (25 LoC) + SearchDirsFlow.partial.cs (55 LoC) + LocateFlow.partial.cs (92 LoC)。

## 用户可见变化

**无 UI 变化**。W37 是纯 refactor MINOR —— public API + XAML binding + DI 注册 + 测试全部 100% 保留。

## 数据契约

100% 保留：

```
public interface IAscLocator
{
    Task<string?> LocateAsync(string contentHash, CancellationToken ct = default);
}

public sealed partial class FileSystemAscLocator : IAscLocator
{
    public const int MaxSearchDepth = 4;
    public string SearchDirsPath { get; }
    public FileSystemAscLocator(ILogger<FileSystemAscLocator> logger, IAscContentHasher hasher);
    internal FileSystemAscLocator(ILogger<FileSystemAscLocator> logger, IAscContentHasher hasher, string overridePath);
    public Task<string?> LocateAsync(string contentHash, CancellationToken ct = default);
}
```

## 5 个核心决策

| ID | 决策 | 选择 |
|---|---|---|
| D1 | NEW partials 数量 | 3 NEW (LoggingFlow + SearchDirsFlow + LocateFlow) |
| D4 | LARGEST method `WalkAsync` (~64 LoC) | 移到 LocateFlow.partial.cs (long-method-as-orchestrator variant of W36 D4 ctor pattern) |
| D5 | `DefaultSearchDirsPath` static helper | 移到 SearchDirsFlow.partial.cs (with cache + load) |
| D6 | 分支 | feature/w37-asc-locator-god-class |
| D7 | 顺序 | T1 LoggingFlow → T2 SearchDirsFlow → T3 LocateFlow |

## 架构里程碑

- **22nd god-class SHIP**（W3-W36 系列延续）
- **18th Core-layer** god-class
- **15th subdirectory-pattern deployment**（sister of W34 DbcSendViewModel/ + W36 StatsViewModel/）
- **1st 3-partial subdirectory in Core layer**（sister of W34 App 3-partials）
- **W19 R1 LESSON 维持**：T1+T3 zero failures first-attempt (8/8 tests each); T2 1 fix iteration (orphan using System.Text.Json removed per plan Step 4)
- **W23 LESSON 维持**：T2 + T3 验证 `IAscContentHasher` interface 兼容

## 计数

- 4 commits on `feature/w37-asc-locator-god-class`
- 1 file modified + 3 files created (subdirectory pattern)
- Main: 225 → 94 LoC (-131, **-58.2%**)
- LoggingFlow.partial.cs: 25 LoC
- SearchDirsFlow.partial.cs: 55 LoC
- LocateFlow.partial.cs: 92 LoC
- Cumulative: +41 LoC overhead (file-scaffolding overhead, consistent with W34/W35/W36)
- 8 FileSystemAscLocatorTests 0 modifications → all PASS
- Full solution: 1456 PASS / 0 FAIL / 5 SKIP

## 6 NEW 1/3 lesson candidates

| 候选 | 期望观察点 |
|---|---|
| `largest-method-can-move-when-flow-is-discrete-walk-not-orchestrator` | NEW 1/3 (W37): WalkAsync 整段移到 LocateFlow |
| `recursive-walk-must-stay-coupled-with-recurse-helper-in-same-partial` | NEW 1/3 (W37): Walk + Recurse 同 partial |
| `cached-search-dirs-must-stay-coupled-with-load-and-default-path-helpers-in-same-partial` | NEW 1/3 (W37): cache + load + path 同 partial |
| `logger-message-partials-can-be-extracted-to-isolated-logging-partial-when-class-has-5-plus-log-calls` | NEW 1/3 (W37): 5 [LoggerMessage] → LoggingFlow |
| `3-partial-subdirectory-pattern-empirical-w34-w37` | NEW 1/3 (W37): W34 + W37 3-partial deployment |
| `cache-gate-locks-once-and-only-on-first-read-pattern-emerges-when-class-lazy-loads-from-disk` | NEW 1/3 (W37): double-checked locking in GetSearchDirs |

## 显式不在范围

- **W38+ 后续 refactor** (DbcTokenizer / DbcSendViewModel / RecordService) → 后续 work
- **P2 PATCH: API Key UI** (v3.54.0 deferred) → 后续 work
- **IAscLocator interface 移 separate file** → 不做（partial 拆分只针对 class）

## 关联

- Spec: `docs/superpowers/specs/2026-07-17-w37-asc-locator-god-class-refactor.md` (`d42adbb`)
- Plan: `docs/superpowers/plans/2026-07-17-w37-asc-locator-god-class-refactor.md` (`d7e329a`)
- W36 ship (predecessor refactor MINOR): `d02d511`
- v3.54.0 ship (predecessor P1 LLM complete): `8e49c23`

## 验证

```bash
dotnet build PeakCan.Host.slnx -c Debug           # 0 errors, 0 warnings on touched code
dotnet test PeakCan.Host.slnx --no-build -c Debug  # 1456 PASS / 0 FAIL / 5 SKIP
wc -l src/PeakCan.Host.Core/Services/AscLocator.cs  # 94 LoC
```

## 已知并行测试 flake（不影响 ship）

sister of v3.52.0/v3.52.1/v3.53.0/v3.53.1/v3.54.0: `UdsClientConcurrentSecurityAccessTests` + `AscParserTests` + `SetSpeed_PreservesCurrentTimestamp` 在并行测试下偶发失败。`dotnet test -- xUnit.MaxParallelThreads=1` 单线程运行即可 1456/1456 PASS。