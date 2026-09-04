# Restbus M4: DbcRestbusGenerator — DBC 勾选生成

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement spec §9 — hil-core `DbcRestbusGenerator` 纯函数从 DBC 机械映射生成 RestbusNode；studio "从 DBC 生成节点" 入口；DBC parser 扩展 GenMsgCycleTime 属性解析。

**Architecture:** 三层：(A) hil-core `DbcParser` 扩展 — 解析 `BA_ "GenMsgCycleTime" BO_ <id> <value>`，写入 `Message` record 新字段 `GenMsgCycleTimeMs`；(B) hil-core `DbcRestbusGenerator` — 纯函数，给定 DbcDocument + 节点名 → RestbusNode（BO_ 报文 → NodeMessage，Cnt/CRC 信号名 → AutoCounter/AutoChecksum，缺 GenMsgCycleTime → GenerationError）；(C) studio EnvironmentTab 加"从 DBC 生成节点"按钮，调 generator + RestbusNodeValidator 校验。

**Tech Stack:** C# / .NET 10, xUnit, DbcParser (hil-core), HILJsonOptions。

**Spec:** `D:\claude_proj2\peakcan-host\docs\superpowers\specs\2026-09-03-restbus-node-unification-design.md` (Draft v3, §9)

## Global Constraints

- hil-core Core 层零 I/O（NetArchTest 红线不变）。
- generator 是纯函数：`DbcDocument + 节点名 → GenerationResult`；不产生 I/O 副作用。
- DBC 缺 `GenMsgCycleTime` → 显式报错列出缺失清单，不使用静默默认周期。
- ECA 规则不从 DBC 来（协议行为不在 DBC 里）；生成节点 Rules=[]，用户叠加模板。
- 新增序列化字段一律可空默认（lockstep 惯例）。
- Conventional commits (feat/fix/chore)。

---

## File Structure

### hil-core (`PeakCan.HIL.Core`)
```
Dbc/
├── Message.cs                            — 加 GenMsgCycleTimeMs: uint? 字段
├── DbcParser/ParseDocumentFlow.cs        — 扩展 ParseBaAttributes 解析 GenMsgCycleTime
Environment/
├── DbcRestbusGenerator.cs                — 新建：纯函数 DBC → RestbusNode
└── DbcRestbusGeneratorResult.cs          — 新建：结果 record（node + errors + warnings）
```

### studio (`PeakCan.Studio.App`)
```
ViewModels/Restbus/
└── EnvironmentTabViewModel.cs            — 加 GenerateFromDbc command + DBC 节点列表
Views/
└── EnvironmentTab.xaml                   — 加"从 DBC 生成"按钮 + DBC 节点勾选列表
```

---

### Task 1: DbcParser GenMsgCycleTime 属性解析

**Files:**
- Modify: `peakcan-hil-core/src/PeakCan.HIL.Core/Dbc/Message.cs` — 加 `uint? GenMsgCycleTimeMs = null`
- Modify: `peakcan-hil-core/src/PeakCan.HIL.Core/Dbc/DbcParser/ParseDocumentFlow.cs` — ParseBaAttributes 提取 GenMsgCycleTime
- Test: `peakcan-hil-core/tests/PeakCan.HIL.Core.Tests/Dbc/GenMsgCycleTimeTests.cs`

**Interfaces:**
- Produces: `Message.GenMsgCycleTimeMs` (uint?, null = 未声明)

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public void Parse_GenMsgCycleTime_ExtractedToMessage()
{
    var dbc = DbcParser.Parse("""
BO_ 512 CRM: 8 Charger
 SG_ S1 : 0|8@1+ (1,0) [0|255] "" BMS

BA_DEF_ BO_ "GenMsgCycleTime" INT 0 10000;
BA_ "GenMsgCycleTime" BO_ 512 250;
""");
    Assert.True(dbc.IsSuccess);
    var msg = Assert.Single(dbc.Value.Messages);
    Assert.Equal((uint?)250, msg.GenMsgCycleTimeMs);
}
```

- [ ] **Step 2: FAIL → extend ParseBaAttributes → PASS**
- [ ] **Step 3: Full DBC parser regression → ALL PASS**
- [ ] **Step 4: Commit** — `feat(hil-core): DBC parser GenMsgCycleTime attribute → Message.GenMsgCycleTimeMs`

---

### Task 2: DbcRestbusGenerator 纯函数

**Files:**
- Create: `peakcan-hil-core/src/PeakCan.HIL.Core/HIL/Environment/DbcRestbusGenerator.cs`
- Create: `peakcan-hil-core/src/PeakCan.HIL.Core/HIL/Environment/DbcRestbusGeneratorResult.cs`
- Test: `peakcan-hil-core/tests/PeakCan.HIL.Core.Tests/HIL/Environment/DbcRestbusGeneratorTests.cs`

**Interfaces:**
- Consumes: `DbcDocument`, `Message.GenMsgCycleTimeMs`, `Signal.Name`
- Produces: `DbcRestbusGenerator.Generate(DbcDocument, nodeName, GeneratorOptions?) → DbcRestbusGeneratorResult`
- `GeneratorOptions`: `CounterSignalPattern = "Cnt"`, `ChecksumSignalPattern = "CRC"`
- `DbcRestbusGeneratorResult`: `RestbusNode? Node`, `IReadOnlyList<string> Errors`, `IReadOnlyList<string> Warnings`

- [ ] **Step 1: Write failing tests (table-driven)**

Test cases:
1. Simple node with 2 BO_ messages, both have GenMsgCycleTime → 2 NodeMessages, CanMessageRef, IntervalMs from attr
2. Signal named "MsgCycleCnt" → AutoCounter detected (contains "Cnt")
3. Signal named "Checksum" → AutoChecksum detected (contains "CRC" or "Checksum")
4. Missing GenMsgCycleTime on one message → Errors contains missing message name, node is null
5. Extended frame (id > 0x7FF) → CanMessageRef(IsExtended: true)
6. Node not found in BU_ → Error "Node 'X' not found"

- [ ] **Step 2: FAIL → implement → PASS**
- [ ] **Step 3: Full hil-core suite → ALL PASS**
- [ ] **Step 4: Commit** — `feat(hil-core): DbcRestbusGenerator pure function with counter/checksum detection`

---

### Task 3: Studio "从 DBC 生成节点" UI

**Files:**
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentTabViewModel.cs` — `GenerateFromDbc(DbcDocument, nodeName)` command
- Modify: `peakcan-studio/src/PeakCan.Studio.App/Views/EnvironmentTab.xaml` — DBC 节点勾选列表 + 生成按钮
- Test: `peakcan-studio/tests/PeakCan.Studio.App.Tests/Restbus/GenerateFromDbcTests.cs`

**Interfaces:**
- Consumes: `DbcRestbusGenerator.Generate()`, `RestbusNodeValidator.Validate()`
- Produces: `EnvironmentTabViewModel.GenerateFromDbc(DbcDocument dbc, string nodeName)` → adds node or reports errors

- [ ] **Step 1: Write failing test**
- [ ] **Step 2: FAIL → implement VM + XAML → PASS**
- [ ] **Step 3: Commit** — `feat(studio): generate restbus node from DBC via DbcRestbusGenerator`

---

### Task 4: 最终验证 + push

- [ ] hil-core 全量 PASS
- [ ] host 全量 PASS
- [ ] studio build PASS
- [ ] push 3 repos