# NM M2 S4 剩余 + S5 数据体检 + 条款追溯修复 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 补完 S4 的两个 P 级原语（`normal_resume_shape` / `stop_sending`），实现 S5 的四项数据体检，并把规则 ID 与条款号分离以修复已存在的错误追溯。

**Architecture:** 三个互不依赖的改动面：(1) `NmRuleEvaluators` 增加两个原语分支 + `NmRuleWhen` 新增 `TimeoutMs` 字段；(2) 新增独立的 `NmDataQualityAnalyzer`，在 `NmAnalyzer` 顶部调用，产出三态检查表；(3) `NmRuleDef` 增加 `Clauses` 属性，所有 finding 的条款号改从它取，profile 规则改名并补 `clauses`。

**Tech Stack:** C# / .NET 10、xUnit + FluentAssertions、YamlDotNet、`TreatWarningsAsErrors=true` + `AnalysisMode=Recommended`（CA1305/CA1861/CA1826 会拦）

**Spec:** `docs/superpowers/specs/2026-11-23-nm-m2-oem-standard-design.md`（Rev 5）

## Global Constraints

以下每条对**所有** Task 生效，不再逐任务重复：

- **只跑定向测试**：`dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~Nm"`。**绝不跑全量测试套件**，绝不逐条排查全量失败清单。
- **commit 需用户明确同意**：每个 Task 的提交步骤必须先向用户确认，不得自行提交或推送。
- **代码中不得出现裸位掩码常量** `0x40` / `0x10` 表示 CBV 语义。位语义只能来自 profile 的 `bit_roles`。
- **`NmSegment` 三字段（`Evidence`/`IsWindowTruncated`/`StateBoundaryKnown`）归 S6 所有**，本计划任何 Task 不得新增或引用它们。
- **不得触碰** `NmActivityBuilder.cs` / `NmPanelFlow.cs` / `TraceViewerViewNmPanel.xaml(.cs)` / `NmSegmentBrushConverter.cs`——这些归 S6/S7。
- **工作区共享**：`peakcan-host` 可能被并行会话同时修改。每个 Task 开始前先 `git status --short` 确认没有他人未提交的改动混入；若出现本计划未列出的文件变更，**停下来问用户**。
- **注释语言**：面向用户/业务逻辑用中文；技术 API、外部接口、协议字段用英文。
- **不许静默降级**：捕获异常要么处理，要么明确上报。规则无法求值必须进 `Unmodeled`，不得静默当作通过。

## 本计划**不做**的事（明确排除，避免实施者误加）

以下来自 spec §19.5「设计未闭合」，本轮**不得实现**：

| 排除项 | 原因 |
|---|---|
| 三态条款覆盖（`Covered`/`Partial`/`NotCovered`） | 依赖逐规则 verdict，`NmRuleResult` 还没有 |
| `NmRuleEvaluation` / `NmRuleStatus` | 同上，是覆盖模型的前置依赖 |
| 条款目录 `NmStandardProfile.Clauses` + `NmClauseDef` | 依赖展平链五处打通（`Flattened`/`ApplyRemove`/`Bind`/`NmProfile`/hash） |
| DQ 三态模型（`NmDataQualityCheck`） | 四个强制点未定；本轮用简化模型，见 Task 8 |
| `truncated_window` 体检项 | spec 自认判据未定义 |
| `max_gap_observe` 原语 | 无 §4 条款映射，参数语义未定义 |
| 主动唤醒两阶段判定（§7.3） | `bit_rule` 缺 `is_one` + wakeup 作用域，是独立设计 |
| 面板 UI（DQ 横幅、unmodeled 区） | 归 S7；改 `NmPanelFlow`/XAML 会与 S6 冲突 |

## Review Focus

按 spec 推断、但容易被实施者忽略的失败模式，按可能性从高到低。**每一条都在对应 Task 里有测试钉住。**

1. **合规规则零输出**：`BitRule` 等原语在合法场景 `return true` 就结束，既不产 finding 也不产 observation。任何「有输出才算通过」的逻辑都会把合规误判成未检查。→ Task 3/4 的负例测试。
2. **`NM_06` / `NM_06_NOBURST` 是成对变体**：每条 trace 必有一条 `not_applicable`。任何「取最差」聚合都会让真实违规降级。→ Task 6 的 profile 测试。
3. **段首截断 vs 段尾截断语义不同**：段首截断时「超上限」仍可判（真实起点更早），段尾截断才整体跳过。新原语若一刀切 `continue` 会退掉现有能力。→ Task 3 的截断测试。
4. **规则改名会静默打断 `remove:`**：`ApplyRemove` 的 `rules.Remove(key)` 不检查 key 存在。→ Task 10 的 profile 迁移步骤。
5. **`NM_09` 的 `source_ref: §5.9` 是错的**：spec §4 里 §5.9 是「诊断报文不唤醒」（U 级 M3），NM_09 是「RSS 停发」。这是仓库里已存在的语义错挂。→ Task 9 的 profile 修正。

---

## File Structure

**Core（`src/PeakCan.Host.Core/Analysis/Nm/`）**

| 文件 | 职责 | 本计划改动 |
|---|---|---|
| `NmStandardProfile.cs` | 规则/profile 模型 | `NmRuleWhen` 加 `TimeoutMs`；`NmRuleDef` 加 `Clauses`；`Phase`→`DeclaredPhase` |
| `NmStandardYaml.cs` | YAML → 模型 | DTO 加 `clauses`/`timeout_ms`/`declared_phase`；`MapRule` 映射 |
| `NmStandardValidator.cs` | 加载期校验 | 校验 `Clauses` 非空与成员格式；`declared_phase` 警告 |
| `NmRuleEvaluators.Bits.cs` | 求值入口 `TryEvaluate` | switch 加两个 case |
| `NmRuleEvaluators.Resume.cs` | **新建**：`normal_resume_shape` + `stop_sending` | 全部 |
| `NmDataQualityAnalyzer.cs` | **新建**：四项体检 | 全部 |
| `NmReport.cs` | 报告契约 | `NmFinding` 加 `Clauses`；`NmReport` 加 `DataQualityChecks` |
| `NmAnalyzer.cs` | 分析主流程 | 调 DQ analyzer；finding 回填 `Clauses` |
| `NmStandardProfileBinding.cs` | 展平 → `NmProfile` | 无需改（`Clauses` 走 `NmRuleDef` 自身） |

**Profile**

| 文件 | 改动 |
|---|---|
| `profiles/oem/geely-cv@2.0.nmstd.yaml` | 规则改名 + 加 `clauses` + 修 `NM_09` 的 `source_ref` |
| `profiles/base/autosar-cannm@4.1.3.nmstd.yaml` | `NM_CYCLE` 加 `clauses`（可为空，见 Task 9） |

**App（消费方）**

| 文件 | 改动 |
|---|---|
| `src/PeakCan.Host.App/Services/ChatTools/GetNmOverviewTool.cs` | 输出 `clauses` 列表与 DQ 检查 |
| `src/PeakCan.Host.App/Services/Nm/NmReportWriter.cs` | Markdown 加条款与 DQ 段 |

---

## Task 1: `NmRuleWhen` 新增 `TimeoutMs` + `NmRuleExpect` 确认

**Files:**
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmStandardProfile.cs`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmStandardYaml.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleParamsTests.cs`

**Interfaces:**
- Produces: `NmRuleWhen.TimeoutMs`（`int?`，默认 null）、YAML 键 `when.timeout_ms`
- Consumes: 无

- [ ] **Step 1: 写失败测试**

在 `NmRuleParamsTests.cs` 末尾加：

```csharp
// spec §7.2：stop_sending 的参数是 timeout_ms，当前 NmRuleWhen 没有这个字段。
// 没有它，规则只能退回读报文级超时，无法表达「按规则声明的超时判停发」。
[Fact]
public void Load_StopSendingWithTimeoutMs_ParsesField()
{
    var yaml = """
        standard:
          id: t
          version: "1.0"
        rules:
          - id: NM_STOP_SENDING
            requirement: 进入 Ready Sleep State 后停止发送
            phase: sleep
            observability: partial
            when:
              kind: stop_sending
              timeout_ms: 2500
            expect:
              kind: observe
            finding: ""
            source_ref: §5.3.3
        """;

    var profile = NmStandardYaml.Load(yaml);

    var rule = Assert.Single(profile.Rules!);
    Assert.Equal(2500, rule.When.TimeoutMs);
}
```

（本 Task 的 YAML 刻意不写 `clauses:`——该字段在 Task 2 才引入。Task 2 会回头给 `NmRuleDef` 加 `required Clauses`，届时这条测试的构造点也要补，Step 6 的批量修复会覆盖到。）

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~Load_StopSendingWithTimeoutMs_ParsesField" -v minimal
```

预期：编译失败——`NmRuleWhen` 没有 `TimeoutMs`；`clauses` 也不是已识别字段。

- [ ] **Step 3: 加字段**

`NmStandardProfile.cs` 的 `NmRuleWhen`，在 `MinFrames` 之后加：

```csharp
    int? MinFrames = null,
    /// <summary>stop_sending 的超时窗口；null = 用报文级生效超时。</summary>
    int? TimeoutMs = null,
```

`NmStandardYaml.cs` 的 `RuleWhenDto`，在 `MinFrames` 之后加：

```csharp
        public int? TimeoutMs { get; set; }
```

`MapRule` 里 `new NmRuleWhen(...)` 的参数列表，在 `w.MinFrames` 之后插入 `w.TimeoutMs`：

```csharp
            new NmRuleWhen(w.Kind!, w.Byte, w.Bit, w.Length, w.Role, w.LatencyKind,
                w.MinSilenceMs, w.CycleMs, w.Pct, w.MinFrames, w.TimeoutMs,
                w.MinId, w.MaxId, w.Extended),
```

- [ ] **Step 4: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~Load_StopSendingWithTimeoutMs_ParsesField" -v minimal
```

预期：PASS。

- [ ] **Step 5: 向用户确认后提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmStandardProfile.cs src/PeakCan.Host.Core/Analysis/Nm/NmStandardYaml.cs tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleParamsTests.cs
git commit -m "$(cat <<'EOF'
feat(nm): add when.timeout_ms for the stop_sending primitive

spec §7.2 的 stop_sending 需要 timeout_ms 参数，NmRuleWhen 之前没有这个字段，
规则只能退回读报文级超时，无法表达「按规则声明的超时判停发」。

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: `NmRuleDef.Clauses` + `DeclaredPhase` 改名

**Files:**
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmStandardProfile.cs`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmStandardYaml.cs`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmStandardValidator.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleParamsTests.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmStandardValidatorTests.cs`

**Interfaces:**
- Produces:
  - `NmRuleDef.Clauses`（`IReadOnlyList<string>`，`required init`）
  - `NmRuleDef.DeclaredPhase`（替代 `Phase`）
  - YAML 键 `clauses: [...]`、`declared_phase:`
- Consumes: 无

**这是破坏性改动**：34 个 `new NmRuleDef` 构造点会全部编译失败。这是刻意的——静默漏填 `Clauses` 会让条款追溯空转。

- [ ] **Step 1: 写失败测试**

在 `NmRuleParamsTests.cs` 末尾加：

```csharp
// spec §4.0：规则 ID 与条款号是两个东西。规则可挂多条条款
// （burst_shape 覆盖 NM_03/NM_04/NM_16），所以 Clauses 是列表不是单值。
[Fact]
public void Load_RuleWithMultipleClauses_ParsesList()
{
    var yaml = """
        standard:
          id: t
          version: "1.0"
        rules:
          - id: NM_BURST_SHAPE
            requirement: 快发 10 帧 × 20ms
            declared_phase: wakeup
            observability: partial
            clauses: ["NM_03", "NM_04", "NM_16"]
            when:
              kind: burst_shape
            expect:
              kind: count_equals
              count: 10
              interval_ms: 20
              pct: 0.1
            finding: NM_WAKEUP_BURST_MISMATCH
            source_ref: §5.5
        """;

    var profile = NmStandardYaml.Load(yaml);

    var rule = Assert.Single(profile.Rules!);
    Assert.Equal(new[] { "NM_03", "NM_04", "NM_16" }, rule.Clauses);
    Assert.Equal("wakeup", rule.DeclaredPhase);
}

// spec §5.3：phase 必填却不驱动求值作用域，用户会误以为它生效。
// 改名 DeclaredPhase 让名字本身诚实。
[Fact]
public void Load_LegacyPhaseKey_StillAcceptedWithAssumption()
{
    var yaml = """
        standard:
          id: t
          version: "1.0"
        rules:
          - id: R1
            requirement: 周期
            phase: normal
            observability: observed
            clauses: ["§5.5"]
            when:
              kind: cycle_tolerance
            expect:
              kind: within_pct
            finding: NM_CYCLE_DEVIATION
            source_ref: §5.5
        """;

    var profile = NmStandardYaml.Load(yaml);

    Assert.Equal("normal", Assert.Single(profile.Rules!).DeclaredPhase);
}
```

在 `NmStandardValidatorTests.cs` 末尾加：

```csharp
// Clauses 为空 = 该规则无法归因到任何条款，条款追溯对它完全失效。
// 但 base profile 的规则可能不属于 OEM 条款空间（AUTOSAR §9），
// 所以这里只校验「非空列表的每一项形状合法」，空列表交给 Task 9 的 profile 决定。
[Fact]
public void Validate_ClauseWithInvalidShape_Throws()
{
    var profile = Profile() with
    {
        Rules =
        [
            new NmRuleDef("R_BAD", "周期", "normal",
                new NmRuleWhen("cycle_tolerance"),
                new NmRuleExpect("within_pct"), "NM_CYCLE_DEVIATION")
            {
                SourceRef = "§5.5",
                Clauses = ["NM_06_NOBURST"],   // 规则名，不是条款号
            },
        ],
    };

    var ex = Assert.Throws<NmProfileValidationException>(
        () => NmStandardValidator.Validate(profile));
    Assert.Contains("NM_06_NOBURST", ex.Message, StringComparison.Ordinal);
    Assert.Contains("条款", ex.Message, StringComparison.Ordinal);
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~Load_RuleWithMultipleClauses_ParsesList|FullyQualifiedName~Validate_ClauseWithInvalidShape_Throws" -v minimal
```

预期：编译失败——`Clauses` / `DeclaredPhase` 不存在。

- [ ] **Step 3: 改模型**

`NmStandardProfile.cs` 的 `NmRuleDef`：

```csharp
public sealed record NmRuleDef(
    string Id,
    string Requirement,
    // spec §5.3：phase 不驱动求值作用域，改名让「声明性」这件事在代码里可见。
    string DeclaredPhase,
    NmRuleWhen When,
    NmRuleExpect Expect,
    string FindingCode)
{
    public string? SourceRef { get; init; }
    public NmObservability Observability { get; init; } = NmObservability.Observed;

    /// <summary>
    /// 本规则覆盖的条款号（spec §4.0）。finding 的条款归因一律从这里取，不取 <see cref="Id"/>。
    /// 一条规则可挂多条（burst_shape → NM_03/NM_04/NM_16）。
    /// 空列表 = 不参与条款覆盖统计（base profile 的规则可能不属于 OEM 条款空间）。
    /// </summary>
    public required IReadOnlyList<string> Clauses { get; init; }
}
```

`NmRulePrimitives` 附近的 `NmRulePhases` 保留不动（校验取值用）。

- [ ] **Step 4: 改 YAML 映射**

`RuleDto` 加两个属性：

```csharp
        public string? DeclaredPhase { get; set; }
        // 兼容旧键：spec §5.3 裁决 phase → declared_phase。
        public string? Phase { get; set; }
        public List<string>? Clauses { get; set; }
```

`MapRule` 的返回值改为：

```csharp
        // 旧键 phase 仍接受，但语义是「声明性元数据」。
        var declaredPhase = r.DeclaredPhase ?? r.Phase ?? "normal";

        return new NmRuleDef(
            r.Id!, r.Requirement!, declaredPhase,
            new NmRuleWhen(w.Kind!, w.Byte, w.Bit, w.Length, w.Role, w.LatencyKind,
                w.MinSilenceMs, w.CycleMs, w.Pct, w.MinFrames, w.TimeoutMs,
                w.MinId, w.MaxId, w.Extended),
            new NmRuleExpect(e.Kind!, e.Value, e.Byte, e.Bit, e.Count, e.IntervalMs,
                e.Pct, e.MinMs, e.MaxMs, e.Anchor, e.WithBurst),
            finding)
        {
            SourceRef = r.SourceRef,
            Observability = ParseObservability(r.Observability),
            Clauses = r.Clauses ?? [],
        };
```

- [ ] **Step 5: 加校验**

`NmStandardValidator.cs` 的 `ValidateRules` 里，`phase` 闭集检查改为用 `DeclaredPhase`：

```csharp
            if (!NmRulePhases.All.Contains(rule.DeclaredPhase))
            {
                throw new NmProfileValidationException(
                    $"规则 {rule.Id} 的 declared_phase「{rule.DeclaredPhase}」非法，合法值：" +
                    string.Join(" | ", NmRulePhases.All.Order(StringComparer.Ordinal)));
            }
```

紧接其后加条款形状校验：

```csharp
            // spec §4.0：条款号必须是「NM_02 / §5.5 / 附录A」形状，不能是规则名。
            // 不锚定会让 NM_06_NOBURST 被 NM_\d{2} 前缀匹配通过。
            foreach (var clause in rule.Clauses)
            {
                if (!IsClauseIdShape(clause))
                {
                    throw new NmProfileValidationException(
                        $"规则 {rule.Id} 的 clauses 项「{clause}」不是合法条款号。" +
                        "合法形状：NM_01 / §5.4.2 / 附录A。规则名不得出现在 clauses 里");
                }
            }
```

在类末尾加辅助方法：

```csharp
    // 锚定正则：不锚定时 NM_06_NOBURST 会被 NM_\d{2} 前缀匹配通过。
    private static readonly System.Text.RegularExpressions.Regex ClauseIdPattern =
        new(@"\A(NM_\d{2}|§\d+(\.\d+)*|附录 ?A)\z",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsClauseIdShape(string clause)
        => ClauseIdPattern.IsMatch(clause);
```

- [ ] **Step 6: 修所有构造点**

编译错误会列出全部 34 处。对每一处：
- 生产代码（`NmStandardYaml.cs:349`）：已在 Step 4 处理。
- 测试：加 `{ Clauses = ["§5.5"] }` 之类的对象初始化器。**不要加默认值 `= []`**——那会让漏填静默通过。

批量定位：

```bash
cd /d/claude_proj2/peakcan-host && dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj 2>&1 | grep "NmRuleDef"
dotnet build tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj 2>&1 | grep -E "CS7036|CS9035"
```

同时把测试里所有 `"normal"`/`"wakeup"`/`"sleep"` 位置参数保持不变（位置没变，只是属性改名）。

- [ ] **Step 7: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~Nm" -v minimal
```

预期：全绿。若有失败，**不要改测试迁就实现**——先判断是本次改动引入还是既有问题。

- [ ] **Step 8: 向用户确认后提交**

```bash
git add -A src/PeakCan.Host.Core tests/PeakCan.Host.Core.Tests
git commit -m "$(cat <<'EOF'
feat(nm): separate rule id from clause id, rename phase to declared_phase

规则 ID 与条款号是两个东西（spec §4.0）：burst_shape 覆盖 NM_03/NM_04/NM_16，
按条款号命名会为同一份判据造重复规则。finding 的条款归因改从 rule.Clauses 取。

phase 必填却不驱动求值作用域（spec §5.3），改名 DeclaredPhase 让名字诚实。
旧键 phase 仍接受，语义降级为声明性元数据。

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `normal_resume_shape` 原语

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Resume.cs`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Bits.cs`（switch 加 case）
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleEvaluatorsResumeTests.cs`

**Interfaces:**
- Consumes: `NmRuleContext`（`NmFrames` / `Episodes` / `Profile` / `Window` / `Resolve`）、`NmBurstDetector.DetectInEpisode`、`NmSilenceClassifier.Classify`
- Produces: `NmRuleEvaluators.TryEvaluate` 的 `case "normal_resume_shape"`

**语义（spec §7.2）**：找 ≥`min_silence_ms` 的静默，恢复后至少 `min_frames` 帧且间隔在 `cycle_ms ± pct`。

- [ ] **Step 1: 写失败测试**

新建 `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleEvaluatorsResumeTests.cs`：

```csharp
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;
using IO = System.IO;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §7.2：normal_resume_shape 判「静默后恢复为正常周期发送」。
/// P 级原语，finding 必须标推断（唤醒源不可观测）。
/// </summary>
public class NmRuleEvaluatorsResumeTests
{
    private static readonly string[] TestReasons = ["MsgType=CANNM"];

    [Fact]
    public void Resume_NormalCycleAfterSilence_NoFinding()
    {
        // 0–1s 发送 → 1–5s 静默（≥2s）→ 5s 起以 500ms 恢复 5 帧。合规。
        var frames = Frames([0.0, 0.5, 1.0], [5.0, 5.5, 6.0, 6.5, 7.0]);

        var result = Evaluate(frames, minSilenceMs: 2000, cycleMs: 500, pct: 0.1, minFrames: 4);

        Assert.Empty(result.Findings);
        Assert.Null(result.UnmodeledReason);
    }

    [Fact]
    public void Resume_TooFewFramesAfterSilence_ProducesFinding()
    {
        // 恢复后只发 2 帧，低于 min_frames=4：无法证明「恢复为正常周期发送」。
        var frames = Frames([0.0, 0.5, 1.0], [5.0, 5.5]);

        var result = Evaluate(frames, minSilenceMs: 2000, cycleMs: 500, pct: 0.1, minFrames: 4);

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_NORMAL_RESUME_SHAPE_MISMATCH", f.Code);
        Assert.True(f.IsInference, "P 级原语必须标推断");
    }

    [Fact]
    public void Resume_WrongCycleAfterSilence_ProducesFinding()
    {
        // 恢复后间隔 100ms，远低于 500ms ±10%：这是快发，不是正常周期发送。
        var frames = Frames([0.0, 0.5, 1.0], [5.0, 5.1, 5.2, 5.3, 5.4]);

        var result = Evaluate(frames, minSilenceMs: 2000, cycleMs: 500, pct: 0.1, minFrames: 4);

        Assert.Contains(result.Findings, f => f.Code == "NM_NORMAL_RESUME_SHAPE_MISMATCH");
    }

    [Fact]
    public void Resume_NoLongSilence_IsUnmodeled()
    {
        // 全程连续发送，没有 ≥2s 的静默：这条规则在本 trace 上不适用。
        // 必须显式进 Unmodeled，不得静默通过。
        var frames = Frames([0.0, 0.5, 1.0, 1.5, 2.0], []);

        var result = Evaluate(frames, minSilenceMs: 2000, cycleMs: 500, pct: 0.1, minFrames: 4);

        Assert.Empty(result.Findings);
        Assert.NotNull(result.UnmodeledReason);
    }

    private static readonly record struct Outcome(
        IReadOnlyList<NmFinding> Findings, string? UnmodeledReason);

    private static Outcome Evaluate(
        ReplayFrame[] frames, int minSilenceMs, int cycleMs, double pct, int minFrames)
    {
        var profile = Profile();
        var rule = new NmRuleDef(
            "NM_RESUME_TO_RMS",
            "静默后恢复为正常周期发送",
            "wakeup",
            new NmRuleWhen("normal_resume_shape",
                MinSilenceMs: minSilenceMs, CycleMs: cycleMs, Pct: pct, MinFrames: minFrames),
            new NmRuleExpect("equals"),
            "NM_NORMAL_RESUME_SHAPE_MISMATCH")
        {
            SourceRef = "§5.3.2",
            Clauses = ["NM_02", "NM_15"],
            Observability = NmObservability.Partial,
        };

        var nmFrames = frames
            .Select(f => new NmFrame(
                new NmMessageDef("0x18FFFAF4", true, "NM", "A", 8, true,
                    TestReasons, CycleMs: 500, TimeoutMs: 2000), f))
            .ToArray();
        var episodes = NmEpisodeBuilder.Build(nmFrames, 2000, 0, 8.0);
        var ctx = new NmRuleContext(nmFrames, episodes, profile,
            NmTimingDefaults.FromProfile(profile.Timings), new NmWindow(0, 8.0, "full"));

        var findings = new List<NmFinding>();
        var observations = new List<NmObservation>();
        var implemented = NmRuleEvaluators.TryEvaluate(
            rule, ctx, findings, observations, out var reason);

        Assert.True(implemented, "normal_resume_shape 必须在 v1 闭集内有实现");
        return new Outcome(findings, reason);
    }

    private static ReplayFrame[] Frames(double[] before, double[] after)
    {
        var all = before.Concat(after).OrderBy(t => t).ToArray();
        return all.Select(t => new ReplayFrame(t, 0x18FFFAF4, 8,
            new byte[] { 0xF4, 0x00, 0x00, 0x01, 0, 0, 0, 0 },
            PeakCan.HIL.Core.FrameFlags.None, IsExtended: true)).ToArray();
    }

    private static NmProfile Profile()
        => new(NmProfileSource.Dbc,
            [new NmMessageDef("0x18FFFAF4", true, "NM", "A", 8, true,
                TestReasons, CycleMs: 500, TimeoutMs: 2000)],
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            [new NmNodeDef("A", StationAddress: 0xF4, IsNmNode: true)],
            Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1),
            Features: new NmFeatures(PnEnabled: false));
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmRuleEvaluatorsResumeTests" -v minimal
```

预期：`Assert.True(implemented)` 失败——`normal_resume_shape` 走 `default: return false`。

- [ ] **Step 3: 加 switch case**

`NmRuleEvaluators.Bits.cs` 的 `TryEvaluate`，在 `case "first_frame_latency"` 之后加：

```csharp
            case "normal_resume_shape":
                return NormalResumeShape(rule, ctx, findings, observations, out unmodeledReason);
```

- [ ] **Step 4: 写实现**

新建 `src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Resume.cs`：

```csharp
namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 静默恢复类 P 级原语（spec §7.2）：<c>normal_resume_shape</c> / <c>stop_sending</c>。
/// 两者都只能推断——唤醒源与「满足睡眠条件」不可观测，finding 必须标 IsInference。
/// </summary>
public static partial class NmRuleEvaluators
{
    /// <summary>
    /// spec §7.2：静默后是否以**正常周期**恢复发送（NM_02 / NM_15，NM_10 见下）。
    /// <para>
    /// 只判「静默后有帧」会把「恢复后不快发、周期错误、只发一帧」都当成合规，
    /// 所以必须同时校验最少帧数与间隔容差。
    /// </para>
    /// <para>
    /// <b>NM_10 的目标状态不同</b>：NM_02/15 恢复后进 RMS（NMSts=0），NM_10 进 NOS（NMSts=1）。
    /// v1 不区分——加了 <c>expect_nmsts</c> 才能区分，但 phase 作用域本身未实现（spec §5.3），
    /// 单加参数并不能真正限定「恢复后的正确状态」。因此 v1 对 NM_10 只标 Partial。
    /// </para>
    /// </summary>
    private static bool NormalResumeShape(
        NmRuleDef rule, NmRuleContext ctx,
        List<NmFinding> findings, List<NmObservation> observations,
        out string? unmodeledReason)
    {
        unmodeledReason = null;

        if (!string.Equals(rule.Expect.Kind, "equals", StringComparison.Ordinal)
            && !string.Equals(rule.Expect.Kind, "within_pct", StringComparison.Ordinal))
        {
            unmodeledReason =
                $"normal_resume_shape 仅实现 expect.kind=equals/within_pct，当前为 {rule.Expect.Kind}";
            return true;
        }

        if (rule.When.CycleMs is not { } cycleMs || cycleMs <= 0)
        {
            unmodeledReason = "normal_resume_shape 缺少有效的 when.cycle_ms";
            return true;
        }

        var minSilenceMs = rule.When.MinSilenceMs ?? 0;
        var minFrames = rule.When.MinFrames ?? 2;

        var violations = new List<double>();
        var observed = new List<double>();
        var noSilenceEpisodes = 0;

        foreach (var episode in ctx.Episodes)
        {
            var frames = FramesInEpisode(ctx.NmFrames, episode);
            if (frames.Length < 2) continue;

            foreach (var messageFrames in GroupByMessage(frames))
            {
                if (messageFrames.Length < 2) continue;

                var def = messageFrames[0].Def;
                var effective = ctx.Resolve(def);
                // 规则显式配置优先（spec §5.2）；未配置时用该报文的生效时序。
                var pct = rule.When.Pct ?? effective.ToleranceRatio;
                var toleranceMs = Math.Max(cycleMs * pct, effective.ToleranceAbsMs);

                var times = messageFrames.Select(f => f.Frame.Timestamp).ToArray();
                var resumeIndex = FindResumeIndex(times, minSilenceMs / 1000.0);
                if (resumeIndex < 0) { noSilenceEpisodes++; continue; }

                var resumeAt = times[resumeIndex];
                observed.Add(resumeAt);

                var after = times[resumeIndex..];
                // 恢复后帧数不足：无法证明「以正常周期持续发送」，不得判通过。
                if (after.Length < minFrames)
                {
                    violations.Add(resumeAt);
                    continue;
                }

                // 恢复后前 minFrames 个间隔必须落在 cycle ± tolerance。
                var bad = false;
                for (var i = 1; i < minFrames; i++)
                {
                    var gapMs = (after[i] - after[i - 1]) * 1000.0;
                    if (Math.Abs(gapMs - cycleMs) > toleranceMs + DurationMsEpsilon) { bad = true; break; }
                }
                if (bad) violations.Add(resumeAt);
            }
        }

        // 全程没有足够长的静默：本条 trace 上规则不适用，必须显式进 Unmodeled。
        if (observed.Count == 0 && noSilenceEpisodes > 0)
        {
            unmodeledReason =
                $"窗口内没有 ≥{minSilenceMs}ms 的静默段，normal_resume_shape 在本 trace 上不适用";
            return true;
        }

        if (violations.Count > 0 && !string.IsNullOrEmpty(rule.FindingCode))
        {
            findings.Add(new NmFinding(
                rule.FindingCode, "warn",
                $"{violations.Count} 处静默恢复后未以 {cycleMs}ms±{rule.When.Pct ?? 0:P0} 正常周期持续发送" +
                $"（最少 {minFrames} 帧）",
                violations.Take(10).ToArray(),
                // P 级：唤醒源不可观测，只能从行为反推。
                IsInference: true,
                SuggestedNextCheck: "核对唤醒后的 Repeat Message State 与发送周期配置",
                RuleId: rule.Id,
                RequirementId: rule.Clauses.Count > 0 ? rule.Clauses[0] : rule.Id,
                SourceRef: rule.SourceRef));
        }

        return true;
    }

    /// <summary>
    /// 返回「静默结束后的第一个帧下标」；没有 ≥minSilenceSec 的静默时返回 -1。
    /// 只认报文自身的时间序列——跨报文静默由 episode 划分，不在这里判。
    /// </summary>
    private static int FindResumeIndex(IReadOnlyList<double> times, double minSilenceSec)
    {
        for (var i = 1; i < times.Count; i++)
        {
            if (times[i] - times[i - 1] >= minSilenceSec) return i;
        }
        return -1;
    }
}
```

**注意**：`DurationMsEpsilon` 定义在 `NmRuleEvaluators.Latency.cs`，同一 `partial` 类，可直接引用。

- [ ] **Step 5: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmRuleEvaluatorsResumeTests" -v minimal
```

预期：4 项全 PASS。

- [ ] **Step 6: 向用户确认后提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Resume.cs src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Bits.cs tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleEvaluatorsResumeTests.cs
git commit -m "$(cat <<'EOF'
feat(nm): implement normal_resume_shape for NM_02/NM_10/NM_15

只判「静默后有帧」会把恢复后不快发、周期错误、只发一帧都当成合规。
本原语同时校验最少帧数与间隔容差，P 级 finding 标推断。

NM_10 恢复后目标是 NOS 而非 RMS，v1 不区分（phase 作用域未实现），只标 Partial。

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: `stop_sending` 原语

**Files:**
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Resume.cs`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Bits.cs`（switch 加 case）
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleEvaluatorsResumeTests.cs`

**Interfaces:**
- Consumes: `NmSilenceClassifier.Classify`（已有的唯一归因实现）
- Produces: `TryEvaluate` 的 `case "stop_sending"`

**语义（spec §4.1 / §7.2）**：停发**不判违规**，只产 observation。归因复用 `NmSilenceClassifier`，不另写。

- [ ] **Step 1: 写失败测试**

在 `NmRuleEvaluatorsResumeTests.cs` 末尾加：

```csharp
// spec §4.1：停发本身不是违规（准备睡眠状态下停发是规范要求的）。
// stop_sending 只产 observation，绝不产 finding。
[Fact]
public void StopSending_NodeStopsWhileOthersActive_ProducesObservationNotFinding()
{
    // A 在 2s 后停发，B 持续到 8s。
    var frames = new List<ReplayFrame>();
    foreach (var t in new[] { 0.0, 0.5, 1.0, 1.5 })
        frames.Add(FrameAt(t, 0x18FFFAF4, 0xF4));
    foreach (var t in new[] { 0.0, 0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 4.5, 5.0, 5.5, 6.0, 6.5, 7.0, 7.5, 8.0 })
        frames.Add(FrameAt(t, 0x18FFFAF5, 0xF5));

    var result = EvaluateStopSending(frames.ToArray());

    Assert.Empty(result.Findings);
    Assert.NotEmpty(result.Observations);
    Assert.Contains(result.Observations, o => o.Kind == "stop_sending");
}

private static readonly record struct StopOutcome(
    IReadOnlyList<NmFinding> Findings,
    IReadOnlyList<NmObservation> Observations,
    string? UnmodeledReason);

private static StopOutcome EvaluateStopSending(ReplayFrame[] frames)
{
    var profile = Profile();
    var rule = new NmRuleDef(
        "NM_STOP_SENDING",
        "进入 Ready Sleep State 后停止发送 NM 报文",
        "sleep",
        new NmRuleWhen("stop_sending"),
        new NmRuleExpect("observe"),
        "")
    {
        SourceRef = "§5.3.3",
        Clauses = ["NM_09", "NM_12"],
        Observability = NmObservability.Partial,
    };

    var nmFrames = frames
        .Select(f => new NmFrame(
            new NmMessageDef(f.IsExtended ? $"0x{f.Id:X8}" : $"0x{f.Id:X3}",
                f.IsExtended, "NM", "N", 8, true,
                TestReasons, CycleMs: 500, TimeoutMs: 2000), f))
        .ToArray();
    var episodes = NmEpisodeBuilder.Build(nmFrames, 2000, 0, 9.0);
    var ctx = new NmRuleContext(nmFrames, episodes, profile,
        NmTimingDefaults.FromProfile(profile.Timings), new NmWindow(0, 9.0, "full"));

    var findings = new List<NmFinding>();
    var observations = new List<NmObservation>();
    var implemented = NmRuleEvaluators.TryEvaluate(
        rule, ctx, findings, observations, out var reason);

    Assert.True(implemented, "stop_sending 必须在 v1 闭集内有实现");
    return new StopOutcome(findings, observations, reason);
}

private static ReplayFrame FrameAt(double t, uint id, byte nid)
    => new(t, id, 8, new byte[] { nid, 0x00, 0x00, 0x01, 0, 0, 0, 0 },
        PeakCan.HIL.Core.FrameFlags.None, IsExtended: true);
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~StopSending_NodeStopsWhileOthersActive" -v minimal
```

预期：`Assert.True(implemented)` 失败。

- [ ] **Step 3: 加 switch case**

`NmRuleEvaluators.Bits.cs`：

```csharp
            case "stop_sending":
                return StopSending(rule, ctx, observations, out unmodeledReason);
```

- [ ] **Step 4: 写实现**

在 `NmRuleEvaluators.Resume.cs` 末尾（类内）加：

```csharp
    /// <summary>
    /// spec §4.1 / §7.2：停发观察，**不判违规**。规范要求准备睡眠状态下停止发送，
    /// 所以「停发」本身是合规行为，报违规会大量误报。
    /// <para>
    /// 归因复用 <see cref="NmSilenceClassifier"/>——那是「其它 NM 是否仍在发」的唯一实现，
    /// 两处各写一份会得出相反结论。
    /// </para>
    /// </summary>
    private static bool StopSending(
        NmRuleDef rule, NmRuleContext ctx,
        List<NmObservation> observations,
        out string? unmodeledReason)
    {
        unmodeledReason = null;

        if (!string.Equals(rule.Expect.Kind, "observe", StringComparison.Ordinal))
        {
            unmodeledReason = $"stop_sending 仅实现 expect.kind=observe，当前为 {rule.Expect.Kind}";
            return true;
        }

        var nidByte = NmProfilePrecedence.EffectiveNidByte(ctx.Profile);
        var emitted = 0;

        foreach (var group in ctx.NmFrames.GroupBy(f => (f.Def.CanIdHex, f.Def.IsExtended)))
        {
            var def = group.First().Def;
            var effective = ctx.Resolve(def);
            var timeoutSec = (rule.When.TimeoutMs ?? effective.TimeoutMs) / 1000.0;

            var times = group.Select(f => f.Frame.Timestamp).OrderBy(t => t).ToArray();
            var lastSeen = times[^1];

            // 窗口内仍在发送：没有停发可观察。
            if (ctx.Window.End - lastSeen < timeoutSec) continue;

            // 找到该报文所属 episode：停发的语义依赖「其它节点是否还在发」。
            var episode = ctx.Episodes
                .FirstOrDefault(e => lastSeen >= e.Start && lastSeen <= e.End);
            if (episode is null) continue;

            var verdict = NmSilenceClassifier.Classify(
                ctx.NmFrames, def.CanIdHex, def.IsExtended,
                lastSeen, episode.End, ctx.Window.End, timeoutSec,
                nidByte, episode.BoundaryEnd);

            // AllNmSilent 是全网静默，归因在 gap 分析器里做，这里不重复产观测。
            if (verdict == NmSilenceVerdict.NotSilent
                || verdict == NmSilenceVerdict.AllNmSilent) continue;

            observations.Add(new NmObservation(
                "stop_sending", def.Sender, def.CanIdHex,
                lastSeen, episode.End,
                $"{def.CanIdHex} 在 {lastSeen:F3}s 后停止发送（归因 {verdict}）",
                // 停发是实测的；归因（合规释放 vs 可疑静默）是推断。
                verdict == NmSilenceVerdict.IndeterminateAtBoundary ? "boundary" : "measured",
                IsInference: verdict != NmSilenceVerdict.ReleasedInReadySleep,
                RuleId: rule.Id));
            emitted++;
        }

        if (emitted == 0)
        {
            unmodeledReason = "窗口内没有可归因的停发段（各报文都在持续发送，或已归为全网静默）";
            return true;
        }

        return true;
    }
```

- [ ] **Step 5: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmRuleEvaluatorsResumeTests" -v minimal
```

预期：5 项全 PASS。

- [ ] **Step 6: 向用户确认后提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Resume.cs src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Bits.cs tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleEvaluatorsResumeTests.cs
git commit -m "$(cat <<'EOF'
feat(nm): implement stop_sending as observation-only

spec §4.1：准备睡眠状态下停发是规范要求的，报违规会大量误报。
本原语只产 observation，归因复用 NmSilenceClassifier（唯一实现）。

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: `NmFinding.Clauses` + analyzer 回填

**Files:**
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmReport.cs`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmAnalyzer.cs`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmRuleEngine.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleEngineTests.cs`

**Interfaces:**
- Consumes: `NmRuleDef.Clauses`（Task 2）
- Produces: `NmFinding.Clauses`（`IReadOnlyList<string>`，默认空）；`NmUnmodeledRule.RuleId` + `ReasonCode`

**为什么在 analyzer 回填而不是让每个 evaluator 填**：9 处 finding 构造分散在三个文件，靠人工记得填迟早漏。回填是结构性保证。

- [ ] **Step 1: 写失败测试**

在 `NmRuleEngineTests.cs` 末尾加：

```csharp
// spec §4.0：finding 的条款归因必须来自 rule.Clauses，不能是 rule.Id。
// 引擎统一回填，不依赖 8 个 evaluator 各自记得填。
[Fact]
public void Evaluate_FindingCarriesClausesFromRule()
{
    var rule = Rule("NM_BURST_SHAPE",
        When("burst_shape", cycleMs: 500),
        Expect("count_equals", count: 10, intervalMs: 20, pct: 0.1),
        "NM_WAKEUP_BURST_MISMATCH",
        clauses: ["NM_03", "NM_04", "NM_16"]);

    var result = NmRuleEngine.Evaluate([rule], Context(burstFrames: 5));

    var f = Assert.Single(result.Findings);
    Assert.Equal(new[] { "NM_03", "NM_04", "NM_16" }, f.Clauses);
    // 兼容字段：按 ordinal 排序后的第一个，不依赖声明顺序。
    Assert.Equal("NM_03", f.RequirementId);
}

// Unmodeled 必须能区分「原语未实现」和「数据缺失」——Reason 是自由文本，AI 读不了。
[Fact]
public void Evaluate_UnmodeledCarriesRuleIdAndReasonCode()
{
    var rule = Rule("NM_GAP",
        When("max_gap_observe"),
        Expect("observe"),
        "",
        clauses: ["§5.5"]);

    var result = NmRuleEngine.Evaluate([rule], Context());

    var u = Assert.Single(result.Unmodeled);
    Assert.Equal("NM_GAP", u.RuleId);
    Assert.Equal("unimplemented_primitive", u.ReasonCode);
}
```

同文件补两个辅助（若已有同名则复用）：

```csharp
    private static NmRuleDef Rule(
        string id, NmRuleWhen when, NmRuleExpect expect, string finding,
        string[]? clauses = null)
        => new(id, "条款", "normal", when, expect, finding)
        {
            SourceRef = "§5.5",
            Clauses = clauses ?? ["§5.5"],
        };
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~Evaluate_FindingCarriesClausesFromRule|FullyQualifiedName~Evaluate_UnmodeledCarriesRuleIdAndReasonCode" -v minimal
```

预期：编译失败——`NmFinding.Clauses` / `NmUnmodeledRule.RuleId` 不存在。

- [ ] **Step 3: 改类型**

`NmReport.cs` 的 `NmFinding`：

```csharp
public sealed record NmFinding(
    string Code,
    string Severity,
    string Message,
    IReadOnlyList<double> EvidenceTimes,
    bool IsInference,
    string? SuggestedNextCheck,
    string? RuleId = null,
    // 已弃用（spec §4.0）：多条款时单值会造假归因。新代码读 Clauses。
    string? RequirementId = null,
    string? SourceRef = null)
{
    /// <summary>本 finding 覆盖的条款号（来自 rule.Clauses）。</summary>
    public IReadOnlyList<string> Clauses { get; init; } = Array.Empty<string>();
}
```

`NmUnmodeledRule`：

```csharp
public sealed record NmUnmodeledRule(
    string Clause,
    string Reason,
    string? RequirementId = null,
    string? SourceRef = null)
{
    /// <summary>未实现的规则 ID；Clause 是条款号，两者不是一回事。</summary>
    public string? RuleId { get; init; }

    /// <summary>结构化原因码，见 spec §9.4。</summary>
    public string? ReasonCode { get; init; }
}
```

- [ ] **Step 4: 引擎回填**

`NmRuleEngine.cs` 的 `Evaluate`，在返回前统一回填：

```csharp
        // spec §4.0：条款归因由引擎统一回填，不依赖各 evaluator 记得填。
        // 9 处 finding 构造分散在三个文件，人工维护迟早漏。
        var byRule = rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
        for (var i = 0; i < findings.Count; i++)
        {
            if (findings[i].RuleId is not { } rid || !byRule.TryGetValue(rid, out var def)) continue;
            findings[i] = findings[i] with
            {
                Clauses = def.Clauses,
                // 兼容字段取确定性值：按 ordinal 排序后的第一个，不依赖 YAML 声明顺序。
                RequirementId = def.Clauses.Count == 0
                    ? null
                    : def.Clauses.Order(StringComparer.Ordinal).First(),
            };
        }
```

Unmodeled 的三处构造补 `RuleId` 与 `ReasonCode`：

```csharp
                unmodeled.Add(new NmUnmodeledRule(
                    rule.Clauses.Count > 0 ? rule.Clauses[0] : rule.Id,
                    "条款不可观测（U 级），只能推断，不产 finding",
                    RequirementId: rule.Clauses.Count > 0 ? rule.Clauses[0] : null,
                    SourceRef: rule.SourceRef)
                {
                    RuleId = rule.Id,
                    ReasonCode = "unobservable",
                });
```

```csharp
                unmodeled.Add(new NmUnmodeledRule(
                    rule.Clauses.Count > 0 ? rule.Clauses[0] : rule.Id,
                    $"原语「{rule.When.Kind}」在 v1 闭集中无实现",
                    RequirementId: rule.Clauses.Count > 0 ? rule.Clauses[0] : null,
                    SourceRef: rule.SourceRef)
                {
                    RuleId = rule.Id,
                    ReasonCode = "unimplemented_primitive",
                });
```

```csharp
                unmodeled.Add(new NmUnmodeledRule(
                    rule.Clauses.Count > 0 ? rule.Clauses[0] : rule.Id,
                    unmodeledReason,
                    RequirementId: rule.Clauses.Count > 0 ? rule.Clauses[0] : null,
                    SourceRef: rule.SourceRef)
                {
                    RuleId = rule.Id,
                    ReasonCode = "missing_profile_data",
                });
```

- [ ] **Step 5: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~Nm" -v minimal
```

预期：全绿。若 `NmRealTraceContractTests` 里断言 `u.Clause == "NM_06"` 的地方失败，那是**正确的失败**——Clause 现在是条款号不是规则名。Task 9 会一并修 profile 与这些断言。

- [ ] **Step 6: 向用户确认后提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmReport.cs src/PeakCan.Host.Core/Analysis/Nm/NmRuleEngine.cs tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleEngineTests.cs
git commit -m "$(cat <<'EOF'
feat(nm): backfill finding clauses from rule definitions

9 处 finding 构造分散在三个文件，靠人工填条款号迟早漏。引擎统一回填是
结构性保证。RequirementId 降级为兼容字段，取排序后第一个以避免顺序依赖。

NmUnmodeledRule 增加 RuleId 与 ReasonCode：Clause 改成条款号后，
「哪条规则未实现」会从结构化输出里消失。

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: 两个新原语的负例集（零误报门）

**Files:**
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleNegativeSetTests.cs`

**Interfaces:**
- Consumes: Task 3 / Task 4 的原语
- Produces: 无

**为什么单独一个 Task**：spec §12 要求「合法场景零误报」。新原语最容易在合规数据上误报，而正常路径测试只验证「违规能检出」，不验证「合规不误报」。

- [ ] **Step 1: 写测试**

在 `NmRuleNegativeSetTests.cs` 末尾加两个用例：

```csharp
// spec §12：合法场景零误报。新原语最容易在合规数据上误报。
[Fact]
public void LegalPassiveWakeup_NormalResumeShape_NoViolation()
{
    // 被动唤醒：静默后以正常周期恢复（不快发），完全合规。
    var frames = new List<ReplayFrame>();
    foreach (var t in new[] { 0.0, 0.5, 1.0 })
        frames.Add(FrameAt(t, 0x18FFFAF4, 0xF4, nmsts: 0));
    foreach (var t in new[] { 5.0, 5.5, 6.0, 6.5, 7.0, 7.5 })
        frames.Add(FrameAt(t, 0x18FFFAF4, 0xF4, nmsts: 0));

    var report = Analyze(frames.ToArray());

    Assert.DoesNotContain(report.Findings,
        f => f.Code == "NM_NORMAL_RESUME_SHAPE_MISMATCH");
}

// spec §4.1：合法释放（节点按 NM_09 停发，其它节点仍在发）不得报任何违规。
[Fact]
public void LegalReleaseToSleep_StopSending_NoViolation()
{
    var frames = new List<ReplayFrame>();
    // A 在 2s 后按 NM_09 停发（最后一帧 NMSts=1 表示已进 RSS）。
    foreach (var t in new[] { 0.0, 0.5, 1.0, 1.5, 2.0 })
        frames.Add(FrameAt(t, 0x18FFFAF4, 0xF4, nmsts: t >= 2.0 ? (byte)1 : (byte)0));
    // B 持续发送。
    for (var t = 0.0; t <= 8.0; t += 0.5)
        frames.Add(FrameAt(t, 0x18FFFAF5, 0xF5, nmsts: 1));

    var report = Analyze(frames.ToArray());

    Assert.DoesNotContain(report.Findings,
        f => f.Code == "NM_NORMAL_RESUME_SHAPE_MISMATCH");
}
```

- [ ] **Step 2: 跑测试**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmRuleNegativeSetTests" -v minimal
```

预期：PASS。**若失败，不要改测试**——说明原语在合法场景误报，是真 bug。按 `superpowers:systematic-debugging` 找根因。

- [ ] **Step 3: 向用户确认后提交**

```bash
git add tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleNegativeSetTests.cs
git commit -m "$(cat <<'EOF'
test(nm): guard the two new primitives against false positives

spec §12 要求合法场景零误报。正常路径测试只验证「违规能检出」，
不验证「合规不误报」——新原语最容易漏的是后者。

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: `NmDataQualityAnalyzer` 骨架 + `unknown_id`

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmDataQualityAnalyzer.cs`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmReport.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmDataQualityAnalyzerTests.cs`

**Interfaces:**
- Produces:
  - `NmDataQualityAnalyzer.Analyze(NmAnalysisRequest) → IReadOnlyList<NmDataQualityCheck>`
  - `NmDataQualityCheck(string Code, NmDataQualityStatus Status, string Detail, IReadOnlyList<NmTimeRange> Ranges, IReadOnlyList<string> AffectedCanIds)`
  - `NmDataQualityStatus { Passed, Failed, NotChecked }`
  - `NmReport.DataQualityChecks`
- Consumes: `NmAnalysisRequest`（已有 `Frames` / `Dbc` / `Profile`）

**为什么独立分析器而不是规则原语**：体检需要原始帧与 DBC（`NmRuleContext` 没有），且必须在规则求值前运行。做成规则会需要引入规则间调度。

- [ ] **Step 1: 写失败测试**

新建 `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmDataQualityAnalyzerTests.cs`：

```csharp
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §9.1：数据体检在 NmAnalyzer 顶部运行，产出三态检查表。
/// 「通过」与「没查」必须可区分——问题列表装不下这个语义。
/// </summary>
public class NmDataQualityAnalyzerTests
{
    private static readonly string[] TestReasons = ["MsgType=CANNM"];

    [Fact]
    public void UnknownId_FrameNotInProfile_IsFailed()
    {
        var profile = Profile("0x18FFFAF4");
        // 0x18FFFB27 不在 profile 声明的报文里。
        var frames = new[]
        {
            FrameAt(0.0, 0x18FFFAF4, 0xF4),
            FrameAt(0.5, 0x18FFFB27, 0x27),
        };

        var checks = NmDataQualityAnalyzer.Analyze(Request(frames, profile));

        var check = Assert.Single(checks, c => c.Code == "unknown_id");
        Assert.Equal(NmDataQualityStatus.Failed, check.Status);
        Assert.Contains("0x18FFFB27", check.AffectedCanIds);
    }

    [Fact]
    public void UnknownId_AllFramesDeclared_IsPassed()
    {
        var profile = Profile("0x18FFFAF4");
        var frames = new[] { FrameAt(0.0, 0x18FFFAF4, 0xF4), FrameAt(0.5, 0x18FFFAF4, 0xF4) };

        var checks = NmDataQualityAnalyzer.Analyze(Request(frames, profile));

        Assert.Equal(NmDataQualityStatus.Passed,
            Assert.Single(checks, c => c.Code == "unknown_id").Status);
    }

    // 没 DBC 时无法判定「ID 是否在 DBC 中」——必须报 NotChecked，不能报 Passed。
    [Fact]
    public void UnknownId_NoDbc_IsNotChecked()
    {
        var profile = Profile("0x18FFFAF4");
        var frames = new[] { FrameAt(0.0, 0x18FFFAF4, 0xF4) };

        var checks = NmDataQualityAnalyzer.Analyze(Request(frames, profile, dbc: null));

        Assert.Equal(NmDataQualityStatus.NotChecked,
            Assert.Single(checks, c => c.Code == "unknown_id").Status);
    }

    private static NmAnalysisRequest Request(
        ReplayFrame[] frames, NmProfile profile, PeakCan.HIL.Core.Dbc.DbcDocument? dbc = default)
        => new("test", null, frames, dbc, profile, 0, 1.0);

    private static NmProfile Profile(string canIdHex)
        => new(NmProfileSource.Dbc,
            [new NmMessageDef(canIdHex, true, "NM", "A", 8, true,
                TestReasons, CycleMs: 500, TimeoutMs: 2000)],
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            [new NmNodeDef("A", StationAddress: 0xF4, IsNmNode: true)],
            Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1),
            Features: new NmFeatures(PnEnabled: false));

    private static ReplayFrame FrameAt(double t, uint id, byte nid)
        => new(t, id, 8, new byte[] { nid, 0x00, 0x00, 0x01, 0, 0, 0, 0 },
            PeakCan.HIL.Core.FrameFlags.None, IsExtended: true);
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmDataQualityAnalyzerTests" -v minimal
```

预期：编译失败——`NmDataQualityAnalyzer` / `NmDataQualityCheck` 不存在。

- [ ] **Step 3: 加类型**

`NmReport.cs` 末尾加：

```csharp
/// <summary>数据体检的三态结果（spec §9.2）。</summary>
public enum NmDataQualityStatus
{
    Passed = 0,
    Failed = 1,
    // 不是 Failed：数据没问题叫「通过」，数据不够叫「没查成」，结论强度不同。
    NotChecked = 2,
}

/// <summary>受影响的时间区间。用具名 record 而非 ValueTuple：后者 JSON 序列化会丢元素名。</summary>
public sealed record NmTimeRange(double Start, double End);

/// <summary>单项体检结果（spec §9.2）。</summary>
public sealed record NmDataQualityCheck(
    string Code,
    NmDataQualityStatus Status,
    string Detail,
    IReadOnlyList<NmTimeRange> Ranges,
    IReadOnlyList<string> AffectedCanIds);
```

`NmReport` 加一个可选参数（放在末尾，保持现有构造点不变）：

```csharp
    IReadOnlyList<NmDataQualityCheck>? DataQualityChecks = null);
```

- [ ] **Step 4: 写分析器**

新建 `src/PeakCan.Host.Core/Analysis/Nm/NmDataQualityAnalyzer.cs`：

```csharp
using System.Globalization;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 数据体检（spec §9）。跑规则前先做一致性检查，结果放报告顶部。
/// <para>
/// <b>不做成规则原语</b>：体检需要原始帧与 DBC（<see cref="NmRuleContext"/> 没有），
/// 且必须在规则求值之前运行——做成规则需要引入规则间调度，v1 不做。
/// </para>
/// <para>
/// <b>三态而非「问题列表」</b>：「通过」与「没查」必须可区分。问题列表装不下这个语义，
/// 会把「无 DBC 所以没查」误读成「查了没问题」。
/// </para>
/// </summary>
public static class NmDataQualityAnalyzer
{
    public static IReadOnlyList<NmDataQualityCheck> Analyze(NmAnalysisRequest request)
    {
        var checks = new List<NmDataQualityCheck>(4);
        checks.Add(CheckUnknownId(request));
        return checks;
    }

    /// <summary>
    /// spec §9.1：报文 ID 是否在 DBC / profile 声明中。
    /// <para>
    /// 没有 DBC 时用 profile 声明的报文集合判定——profile 是用户显式给的约束，
    /// 不依赖 DBC 也能查。两者都没有才报 NotChecked。
    /// </para>
    /// </summary>
    private static NmDataQualityCheck CheckUnknownId(NmAnalysisRequest request)
    {
        var declared = request.Profile.Messages
            .Where(m => m.IsNm)
            .Select(m => (m.CanIdHex, m.IsExtended))
            .ToHashSet();

        if (declared.Count == 0 && request.Dbc is null)
        {
            return new NmDataQualityCheck(
                "unknown_id", NmDataQualityStatus.NotChecked,
                "既无 DBC 也无 profile 报文声明，无法判定帧 ID 是否合法",
                Array.Empty<NmTimeRange>(), Array.Empty<string>());
        }

        var unknown = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var times = new List<double>();
        foreach (var frame in request.Frames)
        {
            var hex = "0x" + frame.Id.ToString("X", CultureInfo.InvariantCulture);
            if (declared.Count > 0 && !declared.Contains((hex, frame.IsExtended)))
            {
                unknown.Add(hex);
                times.Add(frame.Timestamp);
            }
        }

        if (unknown.Count == 0)
        {
            return new NmDataQualityCheck(
                "unknown_id", NmDataQualityStatus.Passed,
                $"全部 {request.Frames.Count} 帧的 ID 均在声明范围内",
                Array.Empty<NmTimeRange>(), Array.Empty<string>());
        }

        return new NmDataQualityCheck(
            "unknown_id", NmDataQualityStatus.Failed,
            $"{times.Count} 帧的 CAN ID 不在 profile 声明中：{string.Join(", ", unknown)}",
            [new NmTimeRange(times.Min(), times.Max())],
            unknown.ToArray());
    }
}
```

- [ ] **Step 5: 接线到 analyzer**

`NmAnalyzer.cs` 的 `Analyze`，在 `var findings = new List<NmFinding>();` 之前加：

```csharp
        // spec §9：数据体检在规则求值之前跑。体检不阻断分析，只标注。
        var dataQuality = NmDataQualityAnalyzer.Analyze(request);
```

`Finish` 里的 `new NmReport(...)` 加最后一个参数：

```csharp
                DataQualityChecks: dataQuality,
```

- [ ] **Step 6: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmDataQualityAnalyzerTests" -v minimal
```

预期：3 项全 PASS。

- [ ] **Step 7: 向用户确认后提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmDataQualityAnalyzer.cs src/PeakCan.Host.Core/Analysis/Nm/NmReport.cs src/PeakCan.Host.Core/Analysis/Nm/NmAnalyzer.cs tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmDataQualityAnalyzerTests.cs
git commit -m "$(cat <<'EOF'
feat(nm): add data quality analyzer with tri-state results

spec §9：体检不阻断分析，但必须明示「数据存疑」。
三态而非问题列表——「通过」与「没查」必须可区分，否则无 DBC 会被误读成通过。

独立分析器而非规则原语：体检需要原始帧与 DBC，且必须先于规则求值运行。

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: 其余三项体检

**Files:**
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmDataQualityAnalyzer.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmDataQualityAnalyzerTests.cs`

**Interfaces:**
- Produces: `dlc_mismatch` / `non_monotonic` / `nid_mapping` 三项

**`truncated_window` 不做**：spec §9.1 自认判据未定义（「首帧落在窗口边界」会让每份录制都算截断）。宁可不做，不造永远为真的告警。

- [ ] **Step 1: 写失败测试**

在 `NmDataQualityAnalyzerTests.cs` 末尾加：

```csharp
// spec §9.1：时间戳非单调必须报区间，不能只报一个 bool。
[Fact]
public void NonMonotonic_ReportsAffectedRange()
{
    var profile = Profile("0x18FFFAF4");
    var frames = new[]
    {
        FrameAt(0.0, 0x18FFFAF4, 0xF4),
        FrameAt(1.0, 0x18FFFAF4, 0xF4),
        FrameAt(0.5, 0x18FFFAF4, 0xF4),   // 回退
        FrameAt(1.5, 0x18FFFAF4, 0xF4),
    };

    var checks = NmDataQualityAnalyzer.Analyze(Request(frames, profile));

    var check = Assert.Single(checks, c => c.Code == "non_monotonic");
    Assert.Equal(NmDataQualityStatus.Failed, check.Status);
    Assert.NotEmpty(check.Ranges);
}

[Fact]
public void NonMonotonic_Monotonic_IsPassed()
{
    var profile = Profile("0x18FFFAF4");
    var frames = new[] { FrameAt(0.0, 0x18FFFAF4, 0xF4), FrameAt(0.5, 0x18FFFAF4, 0xF4) };

    var checks = NmDataQualityAnalyzer.Analyze(Request(frames, profile));

    Assert.Equal(NmDataQualityStatus.Passed,
        Assert.Single(checks, c => c.Code == "non_monotonic").Status);
}

// spec §9.1：NID 映射只在选中企标 profile 且声明了 nid_byte 时才可判。
[Fact]
public void NidMapping_NoProfile_IsNotChecked()
{
    var profile = Profile("0x18FFFAF4");
    var frames = new[] { FrameAt(0.0, 0x18FFFAF4, 0xF4) };

    var checks = NmDataQualityAnalyzer.Analyze(Request(frames, profile));

    Assert.Equal(NmDataQualityStatus.NotChecked,
        Assert.Single(checks, c => c.Code == "nid_mapping").Status);
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmDataQualityAnalyzerTests" -v minimal
```

预期：`Single(c => c.Code == "non_monotonic")` 抛异常——没有这一项。

- [ ] **Step 3: 写三项实现**

`NmDataQualityAnalyzer.Analyze` 改为：

```csharp
    public static IReadOnlyList<NmDataQualityCheck> Analyze(NmAnalysisRequest request)
    {
        var checks = new List<NmDataQualityCheck>(4)
        {
            CheckUnknownId(request),
            CheckDlcMismatch(request),
            CheckNonMonotonic(request),
            CheckNidMapping(request),
        };
        return checks;
    }
```

在类内加：

```csharp
    /// <summary>spec §9.1：实测 DLC 与声明是否一致。检测器复用 M1 的口径，不重造判定。</summary>
    private static NmDataQualityCheck CheckDlcMismatch(NmAnalysisRequest request)
    {
        var declared = request.Profile.Messages
            .Where(m => m.IsNm)
            .ToDictionary(m => (m.CanIdHex, m.IsExtended), m => m.Dlc);

        if (declared.Count == 0)
        {
            return new NmDataQualityCheck(
                "dlc_mismatch", NmDataQualityStatus.NotChecked,
                "profile 未声明任何 NM 报文，无法比对 DLC",
                Array.Empty<NmTimeRange>(), Array.Empty<string>());
        }

        var wrong = new List<double>();
        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in request.Frames)
        {
            var hex = "0x" + frame.Id.ToString("X", CultureInfo.InvariantCulture);
            if (declared.TryGetValue((hex, frame.IsExtended), out var dlc) && frame.Dlc != dlc)
            {
                wrong.Add(frame.Timestamp);
                ids.Add(hex);
            }
        }

        return wrong.Count == 0
            ? new NmDataQualityCheck("dlc_mismatch", NmDataQualityStatus.Passed,
                "实测 DLC 与声明一致", Array.Empty<NmTimeRange>(), Array.Empty<string>())
            : new NmDataQualityCheck("dlc_mismatch", NmDataQualityStatus.Failed,
                $"{wrong.Count} 帧实测 DLC 与声明不一致", 
                [new NmTimeRange(wrong.Min(), wrong.Max())], ids.ToArray());
    }

    /// <summary>spec §9.1：时间戳必须单调。非单调要报受影响区间，供上层剔除。</summary>
    private static NmDataQualityCheck CheckNonMonotonic(NmAnalysisRequest request)
    {
        var regressions = new List<double>();
        double? prev = null;
        foreach (var frame in request.Frames)
        {
            if (prev is { } p && frame.Timestamp < p) regressions.Add(frame.Timestamp);
            prev = frame.Timestamp;
        }

        return regressions.Count == 0
            ? new NmDataQualityCheck("non_monotonic", NmDataQualityStatus.Passed,
                "时间戳单调", Array.Empty<NmTimeRange>(), Array.Empty<string>())
            : new NmDataQualityCheck("non_monotonic", NmDataQualityStatus.Failed,
                $"{regressions.Count} 处时间戳回退，相关区间结论不可靠",
                [new NmTimeRange(regressions.Min(), regressions.Max())], Array.Empty<string>());
    }

    /// <summary>
    /// spec §9.1：NID 是否与 CAN ID 映射一致。
    /// 只有选中企标 profile 且声明了 nid_byte 时才可判——否则 NotChecked，不能记成通过。
    /// </summary>
    private static NmDataQualityCheck CheckNidMapping(NmAnalysisRequest request)
    {
        if (request.Profile.StandardRules is not { Count: > 0 }
            || NmProfilePrecedence.EffectiveNidByte(request.Profile) is not { } nidByte)
        {
            return new NmDataQualityCheck(
                "nid_mapping", NmDataQualityStatus.NotChecked,
                "未选中企标 profile 或未声明 layout.nid_byte，无法校验 NID 映射",
                Array.Empty<NmTimeRange>(), Array.Empty<string>());
        }

        var wrong = new List<double>();
        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in request.Frames)
        {
            var data = frame.Data;
            if (data.Length <= nidByte) continue;
            if (!frame.IsExtended && frame.Id < 0x400) continue;

            var expected = frame.IsExtended
                ? (byte)(frame.Id & 0xFF)
                : (byte)((frame.Id - 0x400) & 0xFF);
            if (data[nidByte] != expected)
            {
                wrong.Add(frame.Timestamp);
                ids.Add("0x" + frame.Id.ToString("X", CultureInfo.InvariantCulture));
            }
        }

        return wrong.Count == 0
            ? new NmDataQualityCheck("nid_mapping", NmDataQualityStatus.Passed,
                "NID 与 CAN ID 映射一致", Array.Empty<NmTimeRange>(), Array.Empty<string>())
            : new NmDataQualityCheck("nid_mapping", NmDataQualityStatus.Failed,
                $"{wrong.Count} 帧 NID 与 CAN ID 映射不一致",
                [new NmTimeRange(wrong.Min(), wrong.Max())], ids.ToArray());
    }
```

- [ ] **Step 4: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmDataQualityAnalyzerTests" -v minimal
```

预期：6 项全 PASS。

- [ ] **Step 5: 向用户确认后提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmDataQualityAnalyzer.cs tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmDataQualityAnalyzerTests.cs
git commit -m "$(cat <<'EOF'
feat(nm): add dlc_mismatch, non_monotonic and nid_mapping checks

三项都复用已有检测口径，不重造判定。non_monotonic 补受影响区间（spec §9.1）。
nid_mapping 无 profile 时报 NotChecked 而非 Passed——「没查」不等于「通过」。

truncated_window 不做：spec §9.1 自认判据未定义，宁可不做也不造永远为真的告警。

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: Profile 条款修复与规则改名

**Files:**
- Modify: `profiles/oem/geely-cv@2.0.nmstd.yaml`
- Modify: `profiles/base/autosar-cannm@4.1.3.nmstd.yaml`
- Modify: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRealTraceContractTests.cs`
- Modify: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleIntegrationTests.cs`
- Modify: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleNegativeSetTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `clauses` 字段
- Produces: 改名后的 profile 规则

**这是本计划唯一改动数据的 Task。改前先备份并核对 diff 规模。**

**旧 → 新映射表**：

| 旧 ID | 新 ID | clauses |
|---|---|---|
| `NM_01` | `NM_NID_MAPPING` | `["§5.4.2"]` |
| `NM_05` | `NM_RMR` | `["§5.4.3"]` |
| `NM_06` | `NM_RMS_DURATION_BURST` | `["§5.5"]` |
| `NM_06_NOBURST` | `NM_RMS_DURATION_NOBURST` | `["§5.5"]` |
| `NM_03` | `NM_BURST_SHAPE` | `["NM_03", "NM_04", "NM_16"]` |
| `NM_09` | `NM_STOP_SENDING` | `["NM_09", "NM_12"]` |
| `NM_RESERVED_B1_*` | 不变 | `["§5.4.3"]` |
| `NM_RESERVED_B3_*` | 不变 | `["§5.4.4"]` |
| `NM_LAYOUT_ID_EXT` | 不变 | `["§5.4.1"]` |
| `NM_LAYOUT_ID_STD` | 不变 | `["§5.4.1"]` |

- [ ] **Step 1: 记录改前状态**

```bash
cd /d/claude_proj2/peakcan-host && cp profiles/oem/geely-cv@2.0.nmstd.yaml /tmp/geely-before.yaml && wc -l profiles/oem/geely-cv@2.0.nmstd.yaml && git status --short profiles/
```

- [ ] **Step 2: 改 OEM profile**

对 `profiles/oem/geely-cv@2.0.nmstd.yaml` 中每条规则：

1. `id:` 按上表改名。
2. `phase:` 改为 `declared_phase:`。
3. 在 `observability:` 之后插入 `clauses: [...]`（按上表）。
4. **`NM_STOP_SENDING` 的 `source_ref` 从 `§5.9` 改为 `§5.3.3`** —— spec §4 里 §5.9 是「诊断报文不唤醒」（U 级、M3），NM_09 是「RSS 停发」。

例如 `NM_01` 改为：

```yaml
  - id: NM_NID_MAPPING
    requirement: NM 报文 ECU 地址（NID）应与 CAN ID 低字节一致（扩展帧）
    declared_phase: normal
    observability: observed
    clauses: ["§5.4.2"]
    when:
      kind: nid_id_mapping
    expect:
      kind: matches_id_low_byte
    finding: NM_NID_MISMATCH
    source_ref: §5.4.2
```

`NM_STOP_SENDING` 改为：

```yaml
  - id: NM_STOP_SENDING
    requirement: 进入 Ready Sleep State 后停止发送 NM 报文
    declared_phase: sleep
    observability: partial
    clauses: ["NM_09", "NM_12"]
    when:
      kind: stop_sending
    expect:
      kind: observe
    finding: ""
    source_ref: §5.3.3
```

- [ ] **Step 3: 改 base profile**

`profiles/base/autosar-cannm@4.1.3.nmstd.yaml` 的 `NM_CYCLE`：

```yaml
  - id: NM_CYCLE
    requirement: NM 报文应以 T_NM_MessageCycle 周期发送，容差 ±20%
    declared_phase: normal
    observability: observed
    # base 规则不属于 OEM 条款空间（它是 AUTOSAR SWS §9），空列表 = 不参与条款覆盖统计。
    clauses: []
    when:
      kind: cycle_tolerance
    expect:
      kind: within_pct
    finding: NM_CYCLE_DEVIATION
    source_ref: AUTOSAR SWS CanNm 4.1.3 §9
```

- [ ] **Step 4: 核对 diff 规模**

```bash
cd /d/claude_proj2/peakcan-host && diff /tmp/geely-before.yaml profiles/oem/geely-cv@2.0.nmstd.yaml | head -60 && echo "--- 变更行数 ---" && diff /tmp/geely-before.yaml profiles/oem/geely-cv@2.0.nmstd.yaml | grep -c "^[<>]"
```

**预期**：变更行数与「改名 6 条 + 加 clauses 18 条 + phase 改名 18 条 + source_ref 1 条」相当（约 60 行）。若远超此数，说明行尾被整体翻成 CRLF 或格式被重写——`git diff --stat` 核对，必要时回滚重做。

- [ ] **Step 5: 跑 profile 加载测试**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmStandardProfileYamlTests|FullyQualifiedName~NmStandardInheritanceTests" -v minimal
```

预期：PASS。若报「clauses 项不是合法条款号」，检查 `§5.4.2` 这类是否被 YAML 解析成了非字符串。

- [ ] **Step 6: 修测试里的旧 ID 断言**

`NmRealTraceContractTests.cs` 里所有 `"NM_06"` / `"NM_06_NOBURST"` / `"NM_03"` / `"NM_09"` 断言按映射表改名。

**关键**：`Assert.DoesNotContain(report.UnmodeledRules!, u => u.Clause == "NM_06")` 这类断言，`u.Clause` 现在是**条款号**不是规则名，必须改成 `u.RuleId == "NM_RMS_DURATION_BURST"`。

```bash
cd /d/claude_proj2/peakcan-host && grep -n "NM_0[0-9]\|NM_1[0-7]" tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRealTraceContractTests.cs tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleIntegrationTests.cs tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleNegativeSetTests.cs
```

逐处按映射表改。**不要用 sed 全局替换**——`"NM_03"` 在 `clauses` 数组里是**条款号**，不该改；在 `RuleId` 断言里是**规则名**，该改。逐处判断。

- [ ] **Step 7: 跑 NM 定向测试**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~Nm" -v minimal
```

预期：全绿。若有失败，逐条判断是「断言该改」还是「实现有 bug」。

- [ ] **Step 8: 向用户确认后提交**

```bash
git add profiles/ tests/PeakCan.Host.Core.Tests/Analysis/Nm/
git commit -m "$(cat <<'EOF'
fix(nm): correct rule ids and clause attribution in the Geely profile

规则 ID 与条款号此前混为一谈：NM_01 规则实际是 §5.4.2 NID 映射，
而 spec §4 里 NM_01 是 KL30 初始化（U 级不可观测）——报告会给一个
不可观测条款发 NID finding。NM_05 同理。

规则改为描述性 ID + clauses 显式声明。NM_STOP_SENDING 的 source_ref
从 §5.9 改为 §5.3.3（§5.9 是诊断报文不唤醒，与本条款无关）。

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: 消费方接线（AI 工具 + Markdown）

**Files:**
- Modify: `src/PeakCan.Host.App/Services/ChatTools/GetNmOverviewTool.cs`
- Modify: `src/PeakCan.Host.App/Services/Nm/NmReportWriter.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/ChatTools/GetNmToolTests.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/Nm/NmReportWriterTests.cs`

**Interfaces:**
- Consumes: `NmFinding.Clauses`、`NmUnmodeledRule.RuleId`、`NmReport.DataQualityChecks`
- Produces: 无

- [ ] **Step 1: 写失败测试**

在 `GetNmToolTests.cs` 末尾加：

```csharp
// spec §15：AI 输出必须带条款号。多条款时给列表，不能只给一个。
[Fact]
public void NmOverview_FindingWithMultipleClauses_EmitsClauseList()
{
    var report = ReportWith(new NmFinding(
        "NM_WAKEUP_BURST_MISMATCH", "warn", "快发形态不符",
        [1.0], IsInference: true, SuggestedNextCheck: null,
        RuleId: "NM_BURST_SHAPE", RequirementId: "NM_03", SourceRef: "§5.5")
    {
        Clauses = ["NM_03", "NM_04", "NM_16"],
    });

    var json = Serialize(report);

    var clauses = json["findings"]![0]!["clauses"]!.AsArray()
        .Select(n => n!.GetValue<string>()).ToArray();
    Assert.Equal(new[] { "NM_03", "NM_04", "NM_16" }, clauses);
}

// spec §9.2：数据体检必须出现在 AI 输出里，且「没查」不能显示成「通过」。
[Fact]
public void NmOverview_DataQualityCheck_EmitsStatus()
{
    var report = ReportWith(dataQuality:
    [
        new NmDataQualityCheck("nid_mapping", NmDataQualityStatus.NotChecked,
            "未选中企标 profile", Array.Empty<NmTimeRange>(), Array.Empty<string>()),
    ]);

    var json = Serialize(report);

    var check = json["data_quality"]![0]!;
    Assert.Equal("nid_mapping", check["code"]!.GetValue<string>());
    Assert.Equal("not_checked", check["status"]!.GetValue<string>());
}
```

在 `NmReportWriterTests.cs` 末尾加：

```csharp
// spec §4.0：Markdown 此前从未输出条款号。改 Clauses 后必须补上。
[Fact]
public void Markdown_IncludesClauses()
{
    var report = ReportWith(new NmFinding(
        "NM_WAKEUP_BURST_MISMATCH", "warn", "快发形态不符",
        [1.0], IsInference: true, SuggestedNextCheck: null,
        RuleId: "NM_BURST_SHAPE", RequirementId: "NM_03", SourceRef: "§5.5")
    {
        Clauses = ["NM_03", "NM_04", "NM_16"],
    });

    var text = NmReportWriter.Write(report);

    Assert.Contains("NM_03", text, StringComparison.Ordinal);
    Assert.Contains("NM_04", text, StringComparison.Ordinal);
    Assert.Contains("NM_16", text, StringComparison.Ordinal);
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~NmOverview_FindingWithMultipleClauses|FullyQualifiedName~NmOverview_DataQualityCheck|FullyQualifiedName~Markdown_IncludesClauses" -v minimal
```

预期：FAIL——输出里没有 `clauses` / `data_quality`。

- [ ] **Step 3: 改 AI 工具**

`GetNmOverviewTool.cs` 里 finding 的序列化处，把 `requirement_id` 换成/补上 `clauses`：

```csharp
            // spec §4.0：条款是列表（burst_shape 覆盖 NM_03/NM_04/NM_16），
            // 单值 requirement_id 会造假归因。保留它仅为兼容旧消费方。
            ["clauses"] = new JsonArray(
                f.Clauses.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
```

unmodeled 处补 `rule_id` 与 `reason_code`：

```csharp
                ["rule_id"] = u.RuleId,
                ["reason_code"] = u.ReasonCode,
```

报告级补 `data_quality`：

```csharp
        // spec §9.2：三态。「没查」不能显示成「通过」。
        ["data_quality"] = new JsonArray(report.DataQualityChecks is null
            ? Array.Empty<JsonNode?>()
            : report.DataQualityChecks.Select(c => (JsonNode?)new JsonObject
            {
                ["code"] = c.Code,
                ["status"] = c.Status switch
                {
                    NmDataQualityStatus.Passed => "passed",
                    NmDataQualityStatus.Failed => "failed",
                    _ => "not_checked",
                },
                ["detail"] = c.Detail,
                ["affected_can_ids"] = new JsonArray(
                    c.AffectedCanIds.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
            }).ToArray()),
```

- [ ] **Step 4: 改 Markdown writer**

`NmReportWriter.cs` 的 finding 行，在 `rule=` 之后补 `clauses=`：

```csharp
        var clauseText = f.Clauses.Count > 0
            ? $" clauses={string.Join(",", f.Clauses)}"
            : "";
```

并在该行末尾追加 `clauseText`。同时新增一段数据体检：

```csharp
        if (report.DataQualityChecks is { Count: > 0 } checks)
        {
            sb.AppendLine();
            sb.AppendLine("## Data Quality");
            sb.AppendLine();
            foreach (var c in checks)
            {
                var status = c.Status switch
                {
                    NmDataQualityStatus.Passed => "通过",
                    NmDataQualityStatus.Failed => "不通过",
                    _ => "未检查",
                };
                sb.AppendLine($"- `{c.Code}` {status}：{c.Detail}");
            }
        }
```

- [ ] **Step 5: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~Nm" -v minimal
```

预期：全绿。

- [ ] **Step 6: 向用户确认后提交**

```bash
git add src/PeakCan.Host.App/Services/ tests/PeakCan.Host.App.Tests/Services/
git commit -m "$(cat <<'EOF'
feat(nm): surface clauses and data quality to AI tool and markdown export

spec §15：AI 只发条款号。多条款时给列表（burst_shape → NM_03/04/16），
单值 requirement_id 会造假归因，降级为兼容字段。

Markdown 此前从未输出条款号——§4.0 说它是「追溯出口」不成立，本次补上。
数据体检三态进两处输出：「没查」不能显示成「通过」。

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**1. Spec 覆盖**

| spec 章节 | 覆盖 Task | 备注 |
|---|---|---|
| §4.0 规则 ID ≠ 条款号 | Task 2, 5, 9 | ✅ |
| §5.3 `NmRuleDef.Clauses` / `DeclaredPhase` | Task 2 | ✅ |
| §7.1.1 逐规则状态 | — | **不做**，属 §19.5「设计未闭合」 |
| §7.2 `normal_resume_shape` | Task 3 | ✅ |
| §7.2 `stop_sending` | Task 4 | ✅ |
| §7.2 `max_gap_observe` | — | **不做**，spec 明确 v1 不实现 |
| §7.3 主动唤醒两阶段 | — | **不做**，需 `bit_rule.is_one` + 作用域 |
| §9.1 `unknown_id` / `dlc_mismatch` / `non_monotonic` / `nid_mapping` | Task 7, 8 | ✅ |
| §9.1 `truncated_window` | — | **不做**，判据未定义 |
| §9.2 三态检查表 | Task 7 | ✅（简化版，`SuppressedRuleIds` 不做） |
| §9.3 独立 analyzer | Task 7 | ✅ |
| §9.4 规则抑制 | — | **不做**，依赖 §9.2 完整模型 |
| §9.5 消费方 | Task 10 | ✅ |
| §11 S4 / S5 验收 | Task 3, 4, 6, 7, 8 | ✅ |
| §12 合法场景零误报 | Task 6 | ✅ |
| §19.5 不可实施项 | 全部排除 | ✅ 见「本计划不做的事」 |

**2. 占位符扫描**：无 TBD / TODO / 「类似 Task N」/ 未给代码的实现步骤。

**3. 类型一致性核对**

- `NmRuleWhen.TimeoutMs`：Task 1 定义，Task 4 使用 ✅
- `NmRuleDef.Clauses` / `DeclaredPhase`：Task 2 定义，Task 3/4/5/9 使用 ✅
- `NmFinding.Clauses`：Task 5 定义，Task 10 使用 ✅
- `NmUnmodeledRule.RuleId` / `ReasonCode`：Task 5 定义，Task 10 使用 ✅
- `NmDataQualityCheck` / `NmDataQualityStatus` / `NmTimeRange`：Task 7 定义，Task 8/10 使用 ✅
- `DurationMsEpsilon`：Task 3 引用 `NmRuleEvaluators.Latency.cs` 的既有常量（同 `partial` 类）✅

**4. Review Focus 逐条对应**

| Review Focus 项 | 钉住它的测试 |
|---|---|
| 合规规则零输出 | Task 3 `Resume_NormalCycleAfterSilence_NoFinding`、Task 6 两个负例 |
| `NM_06`/`NM_06_NOBURST` 成对变体 | Task 9 的 `NmRealTraceContractTests` 修断言 |
| 段首 vs 段尾截断 | Task 3 `Resume_NoLongSilence_IsUnmodeled`（不适用必须显式进 Unmodeled） |
| 规则改名打断 `remove:` | Task 9 Step 2 逐条改 + Step 6 逐处判断（明确禁止 sed 全局替换） |
| `NM_09` 的 `source_ref` 错 | Task 9 Step 2 明确改为 §5.3.3 |

---

## 执行顺序与依赖

```
Task 1 (TimeoutMs)
  └─→ Task 2 (Clauses + DeclaredPhase)   ← 破坏性，必须先做
        ├─→ Task 3 (normal_resume_shape)
        ├─→ Task 4 (stop_sending)
        ├─→ Task 5 (finding 回填)
        │     └─→ Task 9 (profile 改名)   ← 依赖 5 的 RuleId
        │           └─→ Task 10 (消费方)
        ├─→ Task 6 (负例集)               ← 依赖 3、4
        └─→ Task 7 (DQ 骨架)
              └─→ Task 8 (其余三项)
```

Task 7 / 8 与 Task 3–6 互相独立，可并行。
