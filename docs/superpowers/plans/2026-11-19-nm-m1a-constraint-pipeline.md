# NM M1-A：约束管道（hil-core DBC NM 属性解析）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 `PeakCan.HIL.Core` 的 DBC 解析器读出项目 DBC 里已有的 NM 约束（全局 8 条属性、报文 NM 标记、节点站地址），使上层分析引擎不再靠猜 ID 段或字节布局。

**Architecture:** 不新增依赖、不改解析器架构。在现有递归下降解析器里把 `BA_DEF_`（只取 BO_ 作用域的 ENUM 名表）和 `BA_` 的三种作用域（全局 / `BO_` / `BU_`）补齐；模型仍是 immutable positional record，新字段一律追加为尾随可选参数，默认 null 表示"文件没写"。DBC_Convert 的实例属性写入是可选改进，放在最后一个任务。

**Tech Stack:** .NET 10、C# record、xUnit、FluentAssertions（测试项目已引用）。仓库 `D:\claude_proj2\peakcan-hil-core`。

**Spec:** `D:\claude_proj2\peakcan-host\docs\superpowers\specs\2026-11-19-nm-ai-analysis-design.md`（§2.2、§6.1、§6.2、§13.1、§14.1 M1-A、AC1）

## Global Constraints

- **只解析、不裁决**：hil-core 只暴露 DBC 写了什么。哪个报文"是不是 NM"的最终判定属于引擎（计划 B）；本计划只提供 `MsgTypeIndex` + 枚举名解析能力。
- **不按 `BA_DEF_` 的 min/max 拦截属性值**：黄金 DBC 自身就有 `NmBaseAddress = 1280`（声明范围 1024–1279）与 `NmAsrMessageCount = 256`（声明范围 0–255）。解析器本来就不做范围校验，本计划不新增范围校验器，也不实现 `NM_ATTR_RANGE_MISMATCH`（它不在 spec §7.8 的 M1 finding 清单里）。
- **positional record 只能追加尾随可选参数**：`DbcDocument` / `Message` / `Node` 都是 positional record，任何新字段必须放在参数表末尾并给默认值，否则所有构造点（含测试）编译失败。
- **未知/畸形属性一律跳过，不抛异常**：与现有 `ParseBaAttributes` 行为一致。
- **版本锁步**：`PeakCan.HIL.Core` 0.22.0 → 0.23.0；peakcan-host 的 `Directory.Packages.props` 同步改成 0.23.0（同一个任务里完成）。
- **DBC_Convert 源码编码**：新增/修改的 `.cs` 必须是 UTF-8 with BOM（含中文时）；改完跑 `powershell -ExecutionPolicy Bypass -File tools\check_encoding.ps1`。禁止 `MessageBox.Show`（`NotificationPolicyTests` 会扫描）。
- **只跑定向测试**：不要跑裸 `dotnet test`；用 `--filter` 限定本次改动涉及的测试类。
- **与 spec §5.1 的偏离**：`NmProfile` 的 M2 字段（`Standard`/`Layout`/`Features`/`Rules`/…）不在本计划声明，避免 M1 里出现大量死代码。

## Review Focus

| # | 输入/条件 | 期望行为 | 归属任务 |
|---|---|---|---|
| 1 | `BA_ "MsgType" BO_ <id> 4;` 但同文件 `BA_DEF_ BO_ "MsgType" ENUM` 的第 5 项**不叫** `CANNM`（或根本没有 `BA_DEF_`） | 只暴露 `MsgTypeIndex=4`；枚举名解析返回 false。不能因为"4 就是 CANNM"而判 NM | Task 1、Task 3 |
| 2 | 枚举值写成带引号的字符串：`BA_ "NmAsrMessage" BO_ <id> "Yes";` | 解析为 `true`；`"No"` 为 `false`；其它字符串为 `null`（不猜） | Task 3 |
| 3 | 老 DBC 完全没有 NM 属性（如只含 `BO_`/`SG_`） | `NmAttributes` 为 null、`MsgTypeIndex` 为 null；不产生空对象、不抛异常 | Task 2 |
| 4 | 扩展帧 ID：DBC 写 `BO_ 2550330320`（29 位 ID 带 IDE bit） | `BA_` 属性要能挂到该报文上（`ResolveMessageKey` 兜底），不能静默丢失 | Task 3 |
| 5 | `BA_ "NmStationAddress" BU_ ECU_B 244;` | 挂到 `Node` 上；不能误当成报文属性；节点名大小写按原样精确匹配 | Task 4 |
| 6 | 畸形行：缺分号、值不是整数、属性名未知、`BA_DEF_` 是 INT/HEX 类型 | 跳到分号继续解析后续行，整份文件仍 parse 成功 | Task 1、Task 2 |

---

## Task 1: 解析 `BA_DEF_ BO_` 的 ENUM 名表

**Files:**
- Create: `src/PeakCan.HIL.Core/Dbc/DbcAttributeDefinitions.cs`
- Modify: `src/PeakCan.HIL.Core/Dbc/DbcDocument.cs`（追加尾随参数 + 查询方法）
- Modify: `src/PeakCan.HIL.Core/Dbc/DbcParser/ParseDocumentFlow.cs:23`（新增字段）、`:189-192`（`BA_DEF_` 从统一跳过改为单独分支）
- Test: `tests/PeakCan.HIL.Core.Tests/Dbc/NmAttributeParsingTests.cs`

**Interfaces:**
- Produces:
  - `PeakCan.HIL.Core.Dbc.DbcAttributeDefinitions(IReadOnlyDictionary<string, IReadOnlyList<string>> MessageEnumValues)`，静态 `Empty`
  - `DbcDocument.AttributeDefinitions`（可空）
  - `bool DbcDocument.TryGetMessageEnumName(string attributeName, int index, out string name)`

- [ ] **Step 1: 写失败测试**

创建 `tests/PeakCan.HIL.Core.Tests/Dbc/NmAttributeParsingTests.cs`：

```csharp
using PeakCan.HIL.Core.Dbc;
using Xunit;

namespace PeakCan.HIL.Core.Tests.Dbc;

public class NmAttributeParsingTests
{
    [Fact]
    public void Parse_BaDefMessageEnum_CapturesValuesInDeclarationOrder()
    {
        var dbc = DbcParser.Parse("""
VERSION ""

BA_DEF_ BO_  "MsgType" ENUM  "CDD","COM","COM_LONG","COM_LONG_FC","CANNM","XCP_REQ";
BA_DEF_ BO_  "GenMsgCycleTime" INT 0 10000;
BA_DEF_ BU_  "NmAsrNode" ENUM  "No","Yes";
""");

        Assert.True(dbc.IsSuccess);
        var defs = dbc.Value!.AttributeDefinitions;
        Assert.NotNull(defs);
        Assert.Equal(
            new[] { "CDD", "COM", "COM_LONG", "COM_LONG_FC", "CANNM", "XCP_REQ" },
            defs.MessageEnumValues["MsgType"]);
        Assert.False(defs.MessageEnumValues.ContainsKey("GenMsgCycleTime"));
        Assert.False(defs.MessageEnumValues.ContainsKey("NmAsrNode"));
    }

    [Fact]
    public void TryGetMessageEnumName_ResolvesIndexAndRejectsOutOfRange()
    {
        var dbc = DbcParser.Parse("""
VERSION ""

BA_DEF_ BO_  "MsgType" ENUM  "CDD","COM","COM_LONG","COM_LONG_FC","CANNM";
""").Value!;

        Assert.True(dbc.TryGetMessageEnumName("MsgType", 4, out var name));
        Assert.Equal("CANNM", name);
        Assert.False(dbc.TryGetMessageEnumName("MsgType", 9, out _));
        Assert.False(dbc.TryGetMessageEnumName("Missing", 0, out _));
    }

    [Fact]
    public void Parse_WithoutBaDef_LeavesAttributeDefinitionsEmpty()
    {
        var dbc = DbcParser.Parse("""
VERSION ""

BO_ 512 CRM: 8 Charger
 SG_ S1 : 0|8@1+ (1,0) [0|255] "" BMS
""").Value!;

        Assert.Same(DbcAttributeDefinitions.Empty, dbc.AttributeDefinitions);
        Assert.False(dbc.TryGetMessageEnumName("MsgType", 4, out _));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-hil-core
dotnet test tests/PeakCan.HIL.Core.Tests/PeakCan.HIL.Core.Tests.csproj --filter "FullyQualifiedName~NmAttributeParsingTests"
```

Expected: 编译失败，提示 `DbcAttributeDefinitions` 不存在、`DbcDocument.AttributeDefinitions` 不存在。

- [ ] **Step 3: 新增模型**

创建 `src/PeakCan.HIL.Core/Dbc/DbcAttributeDefinitions.cs`：

```csharp
namespace PeakCan.HIL.Core.Dbc;

/// <summary>
/// 消息级（<c>BO_</c>）属性的枚举定义。从
/// <c>BA_DEF_ BO_ "&lt;name&gt;" ENUM "A","B";</c> 收集。
/// 只保留 ENUM 类型；INT / HEX / FLOAT / STRING 的定义不进入本表。
/// </summary>
public sealed record DbcAttributeDefinitions(
    IReadOnlyDictionary<string, IReadOnlyList<string>> MessageEnumValues)
{
    /// <summary>没有任何 BO_ ENUM 定义时的共享空实例。</summary>
    public static readonly DbcAttributeDefinitions Empty =
        new(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
}
```

- [ ] **Step 4: 扩展 `DbcDocument`**

把 `src/PeakCan.HIL.Core/Dbc/DbcDocument.cs` 的记录改成（保留原有 XML 注释）：

```csharp
public sealed record DbcDocument(
    string Version,
    IReadOnlyList<Node> Nodes,
    IReadOnlyList<Message> Messages,
    IReadOnlyDictionary<uint, Message> MessagesById,
    IReadOnlyDictionary<string, ValueTable> ValueTables,
    string SourcePath = "",
    DbcAttributeDefinitions? AttributeDefinitions = null)
{
    /// <summary>
    /// 把 BO_ 枚举属性的下标解析成枚举名。定义不存在、属性名不存在或下标越界都返回
    /// <c>false</c> —— 调用方不得自己假设"下标 4 就是 CANNM"。
    /// </summary>
    public bool TryGetMessageEnumName(string attributeName, int index, out string name)
    {
        name = string.Empty;
        if (AttributeDefinitions is null) return false;
        if (!AttributeDefinitions.MessageEnumValues.TryGetValue(attributeName, out var values)) return false;
        if (index < 0 || index >= values.Count) return false;
        name = values[index];
        return true;
    }
}
```

- [ ] **Step 5: 在解析器里接上 `BA_DEF_`**

在 `src/PeakCan.HIL.Core/Dbc/DbcParser/ParseDocumentFlow.cs`：

1. 类字段区（`_pendingValueTables` 附近）新增：

```csharp
// NM M1-A：BO_ 作用域的 ENUM 名表。MsgType 的值是下标，必须先拿到名字才能判定 CANNM。
private readonly Dictionary<string, IReadOnlyList<string>> _messageEnumDefs = new(StringComparer.Ordinal);
```

2. 顶层 `switch` 中把 `Keyword_BA_DEF_` 从统一跳过分支里拆出来：

```csharp
case TokenType.Keyword_EV_:
case TokenType.Keyword_SIG_GROUP_:
case TokenType.Keyword_NS_DESC_:
    SkipUntilSemicolon();
    break;

case TokenType.Keyword_BA_DEF_:
    ParseMessageAttributeDefinition();
    break;
```

3. 在 `ParseBaAttributes` 方法下方新增：

```csharp
/// <summary>
/// 解析 <c>BA_DEF_</c>。只收集 BO_ 作用域的 ENUM 枚举名表；其余作用域与类型整体跳过。
/// 属性值不按这里声明的 min/max 拦截。
/// </summary>
private void ParseMessageAttributeDefinition()
{
    Consume(); // BA_DEF_
    if (Current.Type != TokenType.Keyword_BO_)
    {
        SkipUntilSemicolon();
        return;
    }
    Consume(); // BO_
    if (Current.Type != TokenType.String)
    {
        SkipUntilSemicolon();
        return;
    }
    var name = Consume().Lexeme;
    if (Current.Type != TokenType.Identifier)
    {
        SkipUntilSemicolon();
        return;
    }
    var valueType = Consume().Lexeme;
    if (!string.Equals(valueType, "ENUM", StringComparison.OrdinalIgnoreCase))
    {
        SkipUntilSemicolon();
        return;
    }
    var values = new List<string>();
    while (Current.Type == TokenType.String)
    {
        values.Add(Consume().Lexeme);
        if (Current.Type == TokenType.Comma) Consume();
    }
    if (Current.Type == TokenType.Semicolon) Consume();
    _messageEnumDefs[name] = values;
}
```

4. 在 `ParseDocument` 结尾构造 `DbcDocument` 的地方传入定义表（第 220 行附近）：

```csharp
var definitions = _messageEnumDefs.Count == 0
    ? DbcAttributeDefinitions.Empty
    : new DbcAttributeDefinitions(_messageEnumDefs);
return Result<DbcDocument>.Ok(new DbcDocument(
    version, nodes, _pendingMessages, byId, valueTables,
    AttributeDefinitions: definitions));
```

- [ ] **Step 6: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-hil-core
dotnet test tests/PeakCan.HIL.Core.Tests/PeakCan.HIL.Core.Tests.csproj --filter "FullyQualifiedName~Dbc"
```

Expected: `NmAttributeParsingTests` 全绿，现有 `GenMsgCycleTimeTests` / `DbcParserTests` / `DbcParserMultiplexedTests` 不回归。

- [ ] **Step 7: 提交**

```bash
git add src/PeakCan.HIL.Core/Dbc/DbcAttributeDefinitions.cs \
        src/PeakCan.HIL.Core/Dbc/DbcDocument.cs \
        src/PeakCan.HIL.Core/Dbc/DbcParser/ParseDocumentFlow.cs \
        tests/PeakCan.HIL.Core.Tests/Dbc/NmAttributeParsingTests.cs
git commit -m "feat(dbc): parse BA_DEF_ BO_ enum definitions"
```

---

## Task 2: 解析全局 NM 属性（`NmAttributes`）

**Files:**
- Create: `src/PeakCan.HIL.Core/Dbc/NmAttributes.cs`
- Modify: `src/PeakCan.HIL.Core/Dbc/DbcDocument.cs`（追加尾随参数）
- Modify: `src/PeakCan.HIL.Core/Dbc/DbcParser/ParseDocumentFlow.cs`（`ParseBaAttributes` 前置分支 + 结尾构造）
- Test: `tests/PeakCan.HIL.Core.Tests/Dbc/NmAttributeParsingTests.cs`（追加用例）

**Interfaces:**
- Produces: `PeakCan.HIL.Core.Dbc.NmAttributes`，`DbcDocument.NmAttributes`

- [ ] **Step 1: 写失败测试**

把以下用例追加到 `NmAttributeParsingTests`：

```csharp
[Fact]
public void Parse_GlobalNmAttributes_ExtractsAllEightValues()
{
    var dbc = DbcParser.Parse("""
VERSION ""

BU_: ECU_A ECU_B

BA_ "NmBaseAddress" 1280;
BA_ "NmAsrMessageCount" 256;
BA_ "NmAsrBaseAddress" 1280;
BA_ "NmAsrWaitBusSleepTime" 2000;
BA_ "NmAsrTimeoutTime" 2000;
BA_ "NmAsrRepeatMessageTime" 1600;
BA_ "NmAsrCanMsgCycleTime" 500;
BA_ "DBName" "CAN_Network";
""");

    Assert.True(dbc.IsSuccess);
    var nm = dbc.Value!.NmAttributes;
    Assert.NotNull(nm);
    Assert.Equal(1280, nm.BaseAddress);
    Assert.Equal(256, nm.MessageCount);
    Assert.Equal(1280, nm.AsrBaseAddress);
    Assert.Equal(2000, nm.WaitBusSleepTimeMs);
    Assert.Equal(2000, nm.TimeoutTimeMs);
    Assert.Equal(1600, nm.RepeatMessageTimeMs);
    Assert.Equal(500, nm.CanMsgCycleTimeMs);
    Assert.Equal("CAN_Network", nm.NetworkName);
    Assert.False(nm.IsEmpty);
}

[Fact]
public void Parse_DbcWithoutNmAttributes_LeavesNull()
{
    var dbc = DbcParser.Parse("""
VERSION ""

BO_ 512 CRM: 8 Charger
 SG_ S1 : 0|8@1+ (1,0) [0|255] "" BMS
""").Value!;

    Assert.Null(dbc.NmAttributes);
}

[Fact]
public void Parse_NmAttributeOutsideDeclaredRange_IsKeptNotRejected()
{
    // 黄金 DBC 自身就违反自己的 BA_DEF_ 范围：NmBaseAddress 1280 声明在 1024..1279。
    var dbc = DbcParser.Parse("""
VERSION ""

BA_DEF_  "NmBaseAddress" HEX 1024 1279;
BA_ "NmBaseAddress" 1280;
""");

    Assert.True(dbc.IsSuccess);
    Assert.Equal(1280, dbc.Value!.NmAttributes!.BaseAddress);
}

[Fact]
public void Parse_MalformedNmAttribute_IsSkippedAndLaterLinesStillParse()
{
    var dbc = DbcParser.Parse("""
VERSION ""

BA_ "NmAsrTimeoutTime" not_a_number;
BA_ "NmAsrCanMsgCycleTime" 500;
""");

    Assert.True(dbc.IsSuccess);
    Assert.Null(dbc.Value!.NmAttributes!.TimeoutTimeMs);
    Assert.Equal(500, dbc.Value!.NmAttributes!.CanMsgCycleTimeMs);
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-hil-core
dotnet test tests/PeakCan.HIL.Core.Tests/PeakCan.HIL.Core.Tests.csproj --filter "FullyQualifiedName~NmAttributeParsingTests"
```

Expected: 编译失败，`NmAttributes` 不存在。

- [ ] **Step 3: 新增模型**

创建 `src/PeakCan.HIL.Core/Dbc/NmAttributes.cs`：

```csharp
namespace PeakCan.HIL.Core.Dbc;

/// <summary>
/// DBC 全局 NM 属性：<c>BA_ "NmAsr*"</c>、<c>BA_ "NmBaseAddress"</c>、<c>BA_ "DBName"</c>。
/// 全部为 null 表示该 DBC 没有声明任何 NM 全局属性。
/// <para>
/// 数值不按 <c>BA_DEF_</c> 声明的 min/max 拦截：黄金 DBC 本身就越界（见 spec §6.2）。
/// </para>
/// </summary>
public sealed record NmAttributes(
    int? BaseAddress = null,
    int? MessageCount = null,
    int? AsrBaseAddress = null,
    int? WaitBusSleepTimeMs = null,
    int? TimeoutTimeMs = null,
    int? RepeatMessageTimeMs = null,
    int? CanMsgCycleTimeMs = null,
    string? NetworkName = null)
{
    /// <summary>所有字段都为 null。</summary>
    public bool IsEmpty =>
        BaseAddress is null && MessageCount is null && AsrBaseAddress is null &&
        WaitBusSleepTimeMs is null && TimeoutTimeMs is null &&
        RepeatMessageTimeMs is null && CanMsgCycleTimeMs is null && NetworkName is null;
}
```

- [ ] **Step 4: 扩展 `DbcDocument`**

在 Task 1 的签名末尾追加一个尾随可选参数：

```csharp
    DbcAttributeDefinitions? AttributeDefinitions = null,
    NmAttributes? NmAttributes = null)
```

- [ ] **Step 5: 解析器接入**

在 `ParseDocumentFlow.cs` 的字段区追加（与 Task 1 的 `_messageEnumDefs` 放一起）：

```csharp
// NM M1-A：全局 NM 属性的可变收集槽；构造 DbcDocument 时冻结成 NmAttributes。
private int? _nmBaseAddress;
private int? _nmMessageCount;
private int? _nmAsrBaseAddress;
private int? _nmWaitBusSleepTime;
private int? _nmTimeoutTime;
private int? _nmRepeatMessageTime;
private int? _nmCanMsgCycleTime;
private string? _nmNetworkName;
```

在 `ParseBaAttributes` 中，读完 `attrName` 后、`GenMsgCycleTime` 分支之前插入：

```csharp
if (TryParseNmGlobalAttribute(attrName))
{
    if (Current.Type == TokenType.Semicolon) Consume();
    return;
}
```

新增方法：

```csharp
/// <summary>
/// 处理全局作用域（没有 BO_/BU_/SG_ 前缀）的 NM 属性。
/// 返回 true 表示这个属性名归本方法处理（无论值是否合法）。
/// </summary>
private bool TryParseNmGlobalAttribute(string attrName)
{
    switch (attrName)
    {
        case "NmBaseAddress": TryReadNmInt(out _nmBaseAddress); return true;
        case "NmAsrMessageCount": TryReadNmInt(out _nmMessageCount); return true;
        case "NmAsrBaseAddress": TryReadNmInt(out _nmAsrBaseAddress); return true;
        case "NmAsrWaitBusSleepTime": TryReadNmInt(out _nmWaitBusSleepTime); return true;
        case "NmAsrTimeoutTime": TryReadNmInt(out _nmTimeoutTime); return true;
        case "NmAsrRepeatMessageTime": TryReadNmInt(out _nmRepeatMessageTime); return true;
        case "NmAsrCanMsgCycleTime": TryReadNmInt(out _nmCanMsgCycleTime); return true;
        case "DBName": TryReadNmString(out _nmNetworkName); return true;
        default: return false;
    }
}

/// <summary>读一个整数属性值；不是整数时跳到分号，槽位保持 null。</summary>
private void TryReadNmInt(out int? target)
{
    target = null;
    if (Current.Type != TokenType.Integer)
    {
        SkipUntilSemicolon();
        return;
    }
    if (long.TryParse(Current.Lexeme, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value is >= int.MinValue and <= int.MaxValue)
    {
        target = (int)value;
    }
    Consume();
}

/// <summary>读一个字符串属性值；不是字符串时跳到分号，槽位保持 null。</summary>
private void TryReadNmString(out string? target)
{
    target = null;
    if (Current.Type != TokenType.String)
    {
        SkipUntilSemicolon();
        return;
    }
    target = Consume().Lexeme;
}
```

在结尾构造 `DbcDocument` 时追加：

```csharp
var nm = (_nmBaseAddress is null && _nmMessageCount is null && _nmAsrBaseAddress is null &&
          _nmWaitBusSleepTime is null && _nmTimeoutTime is null && _nmRepeatMessageTime is null &&
          _nmCanMsgCycleTime is null && _nmNetworkName is null)
    ? null
    : new NmAttributes(_nmBaseAddress, _nmMessageCount, _nmAsrBaseAddress,
        _nmWaitBusSleepTime, _nmTimeoutTime, _nmRepeatMessageTime, _nmCanMsgCycleTime, _nmNetworkName);

return Result<DbcDocument>.Ok(new DbcDocument(
    version, nodes, _pendingMessages, byId, valueTables,
    AttributeDefinitions: definitions,
    NmAttributes: nm));
```

- [ ] **Step 6: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-hil-core
dotnet test tests/PeakCan.HIL.Core.Tests/PeakCan.HIL.Core.Tests.csproj --filter "FullyQualifiedName~Dbc"
```

Expected: 全绿。

- [ ] **Step 7: 提交**

```bash
git add src/PeakCan.HIL.Core/Dbc/NmAttributes.cs \
        src/PeakCan.HIL.Core/Dbc/DbcDocument.cs \
        src/PeakCan.HIL.Core/Dbc/DbcParser/ParseDocumentFlow.cs \
        tests/PeakCan.HIL.Core.Tests/Dbc/NmAttributeParsingTests.cs
git commit -m "feat(dbc): parse global NM BA_ attributes into NmAttributes"
```

---

## Task 3: 报文级 NM 标记（`MsgType` / `NmAsrMessage`）

**Files:**
- Modify: `src/PeakCan.HIL.Core/Dbc/Message.cs`
- Modify: `src/PeakCan.HIL.Core/Dbc/DbcParser/ParseDocumentFlow.cs`（`ParseBaAttributes` 两个新分支 + `UpdateMessage` helper）
- Test: `tests/PeakCan.HIL.Core.Tests/Dbc/NmAttributeParsingTests.cs`（追加用例）

**Interfaces:**
- Consumes: `DbcDocument.TryGetMessageEnumName`（Task 1）
- Produces: `Message.NmAsrMessage`（`bool?`）、`Message.MsgTypeIndex`（`int?`）

- [ ] **Step 1: 写失败测试**

追加到 `NmAttributeParsingTests`：

```csharp
[Fact]
public void Parse_MsgTypeIndexAndEnumName_ResolveToCannm()
{
    var dbc = DbcParser.Parse("""
VERSION ""

BO_ 1280 NM_Message: 8 ECU_A

BA_DEF_ BO_  "MsgType" ENUM  "CDD","COM","COM_LONG","COM_LONG_FC","CANNM";
BA_ "MsgType" BO_ 1280 4;
""").Value!;

    var msg = Assert.Single(dbc.Messages);
    Assert.Equal(4, msg.MsgTypeIndex);
    Assert.True(dbc.TryGetMessageEnumName("MsgType", msg.MsgTypeIndex!.Value, out var name));
    Assert.Equal("CANNM", name);
}

[Fact]
public void Parse_MsgTypeIndexWithoutEnumDefinition_CannotResolveName()
{
    // 没有 BA_DEF_ 时，下标 4 只是数字，不能推定成 CANNM。
    var dbc = DbcParser.Parse("""
VERSION ""

BO_ 1280 NM_Message: 8 ECU_A

BA_ "MsgType" BO_ 1280 4;
""").Value!;

    var msg = Assert.Single(dbc.Messages);
    Assert.Equal(4, msg.MsgTypeIndex);
    Assert.False(dbc.TryGetMessageEnumName("MsgType", 4, out _));
}

[Fact]
public void Parse_NmAsrMessageQuotedYes_BecomesTrue()
{
    var dbc = DbcParser.Parse("""
VERSION ""

BO_ 1280 NM_Message: 8 ECU_A

BA_ "NmAsrMessage" BO_ 1280 "Yes";
""").Value!;

    Assert.True(Assert.Single(dbc.Messages).NmAsrMessage);
}

[Fact]
public void Parse_NmAsrMessageQuotedNo_BecomesFalse()
{
    var dbc = DbcParser.Parse("""
VERSION ""

BO_ 1280 NM_Message: 8 ECU_A

BA_ "NmAsrMessage" BO_ 1280 "No";
""").Value!;

    Assert.False(Assert.Single(dbc.Messages).NmAsrMessage);
}

[Fact]
public void Parse_NmAsrMessageUnknownValue_StaysNull()
{
    var dbc = DbcParser.Parse("""
VERSION ""

BO_ 1280 NM_Message: 8 ECU_A

BA_ "NmAsrMessage" BO_ 1280 "Maybe";
""").Value!;

    Assert.Null(Assert.Single(dbc.Messages).NmAsrMessage);
}

[Fact]
public void Parse_MsgTypeOnExtendedFrame_IsAttachedDespiteIdeBit()
{
    // 0x98FFFAF4 = 29 位 ID 0x18FFFAF4 | IDE bit。
    var dbc = DbcParser.Parse("""
VERSION ""

BO_ 2566848244 BMS_NWM: 8 BMS

BA_DEF_ BO_  "MsgType" ENUM  "CDD","COM","COM_LONG","COM_LONG_FC","CANNM";
BA_ "MsgType" BO_ 2566848244 4;
""").Value!;

    var msg = Assert.Single(dbc.Messages);
    Assert.Equal(4, msg.MsgTypeIndex);
    Assert.True(dbc.TryGetMessageEnumName("MsgType", 4, out var name));
    Assert.Equal("CANNM", name);
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-hil-core
dotnet test tests/PeakCan.HIL.Core.Tests/PeakCan.HIL.Core.Tests.csproj --filter "FullyQualifiedName~NmAttributeParsingTests"
```

Expected: 编译失败，`Message.MsgTypeIndex` / `Message.NmAsrMessage` 不存在。

- [ ] **Step 3: 扩展 `Message`**

在 `Message` 参数表**末尾**追加（保持现有参数与注释不动）：

```csharp
    uint? GenMsgCycleTimeMs = null,
    bool? NmAsrMessage = null,
    int? MsgTypeIndex = null);
```

并补 XML 注释：

```csharp
/// <param name="NmAsrMessage">来自 <c>BA_ "NmAsrMessage" BO_ ... "Yes"</c>。
/// null 表示文件没有写或值无法识别。</param>
/// <param name="MsgTypeIndex">来自 <c>BA_ "MsgType" BO_ ... N</c> 的枚举下标。
/// 下标是否等于 CANNM 必须用 <see cref="DbcDocument.TryGetMessageEnumName"/> 反查，不能写死。</param>
```

- [ ] **Step 4: 解析器接入**

在 `ParseDocumentFlow.cs` 新增 helper（放在 `ApplyMessageCycleTime` 旁边）：

```csharp
/// <summary>把一条 BA_ BO_ 属性应用到已解析的报文上。报文不存在时静默忽略。</summary>
private void UpdateMessage(uint msgId, Func<Message, Message> update)
{
    if (ResolveMessageKey(msgId) is not { } key
        || !_pendingMessagesById.TryGetValue(key, out var msg) || msg is null)
    {
        return;
    }
    var updated = update(msg);
    _pendingMessages[_pendingMessages.FindIndex(m => m.Id == key)] = updated;
    _pendingMessagesById[key] = updated;
}
```

在 `ParseBaAttributes` 的 `GenMsgCycleTime` 分支之后、`Comment` 判断之前插入两个分支：

```csharp
if (string.Equals(attrName, "MsgType", StringComparison.OrdinalIgnoreCase))
{
    if (Current.Type != TokenType.Keyword_BO_) { SkipUntilSemicolon(); return; }
    Consume();
    if (Current.Type != TokenType.Integer) { SkipUntilSemicolon(); return; }
    var msgId = uint.Parse(Current.Lexeme, CultureInfo.InvariantCulture);
    Consume();
    if (Current.Type == TokenType.Integer
        && int.TryParse(Current.Lexeme, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
    {
        Consume();
        UpdateMessage(msgId, m => m with { MsgTypeIndex = index });
    }
    if (Current.Type == TokenType.Semicolon) Consume();
    return;
}

if (string.Equals(attrName, "NmAsrMessage", StringComparison.OrdinalIgnoreCase))
{
    if (Current.Type != TokenType.Keyword_BO_) { SkipUntilSemicolon(); return; }
    Consume();
    if (Current.Type != TokenType.Integer) { SkipUntilSemicolon(); return; }
    var msgId = uint.Parse(Current.Lexeme, CultureInfo.InvariantCulture);
    Consume();
    bool? value = null;
    if (Current.Type == TokenType.String)
    {
        if (string.Equals(Current.Lexeme, "Yes", StringComparison.OrdinalIgnoreCase)) value = true;
        else if (string.Equals(Current.Lexeme, "No", StringComparison.OrdinalIgnoreCase)) value = false;
        Consume();
    }
    if (value is not null) UpdateMessage(msgId, m => m with { NmAsrMessage = value });
    if (Current.Type == TokenType.Semicolon) Consume();
    return;
}
```

可顺手把 `ApplyMessageCycleTime` 改为调用 `UpdateMessage`（行为不变）：

```csharp
private void ApplyMessageCycleTime(uint msgId, uint cycleTimeMs)
    => UpdateMessage(msgId, m => m with { GenMsgCycleTimeMs = cycleTimeMs });
```

- [ ] **Step 5: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-hil-core
dotnet test tests/PeakCan.HIL.Core.Tests/PeakCan.HIL.Core.Tests.csproj --filter "FullyQualifiedName~Dbc"
```

Expected: 全绿，`GenMsgCycleTimeTests` 不回归。

- [ ] **Step 6: 提交**

```bash
git add src/PeakCan.HIL.Core/Dbc/Message.cs \
        src/PeakCan.HIL.Core/Dbc/DbcParser/ParseDocumentFlow.cs \
        tests/PeakCan.HIL.Core.Tests/Dbc/NmAttributeParsingTests.cs
git commit -m "feat(dbc): parse per-message NM markers (MsgType, NmAsrMessage)"
```

---

## Task 4: 节点级 NM 属性（`NmAsrNode` / `NmAsrNodeIdentifier` / `NmStationAddress`）

**Files:**
- Modify: `src/PeakCan.HIL.Core/Dbc/Node.cs`
- Modify: `src/PeakCan.HIL.Core/Dbc/DbcParser/ParseDocumentFlow.cs`（`nodes` 改为字段 + `BU_` 分支）
- Test: `tests/PeakCan.HIL.Core.Tests/Dbc/NmAttributeParsingTests.cs`（追加用例）

**Interfaces:**
- Produces: `Node.NmAsrNode`（`bool?`）、`Node.NmAsrNodeIdentifier`（`byte?`）、`Node.NmStationAddress`（`byte?`）

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Parse_NodeNmAttributes_AreAttachedToTheNamedNode()
{
    var dbc = DbcParser.Parse("""
VERSION ""

BU_: ECU_A ECU_B

BA_ "NmAsrNode" BU_ ECU_B "Yes";
BA_ "NmAsrNodeIdentifier" BU_ ECU_B 244;
BA_ "NmStationAddress" BU_ ECU_B 244;
""").Value!;

    var b = dbc.Nodes.Single(n => n.Name == "ECU_B");
    Assert.True(b.NmAsrNode);
    Assert.Equal((byte?)244, b.NmAsrNodeIdentifier);
    Assert.Equal((byte?)244, b.NmStationAddress);

    var a = dbc.Nodes.Single(n => n.Name == "ECU_A");
    Assert.Null(a.NmAsrNode);
    Assert.Null(a.NmStationAddress);
}

[Fact]
public void Parse_NodeAttributeForUnknownNode_IsIgnored()
{
    var dbc = DbcParser.Parse("""
VERSION ""

BU_: ECU_A

BA_ "NmStationAddress" BU_ ECU_Z 244;
""").Value!;

    Assert.Equal((byte?)null, Assert.Single(dbc.Nodes).NmStationAddress);
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-hil-core
dotnet test tests/PeakCan.HIL.Core.Tests/PeakCan.HIL.Core.Tests.csproj --filter "FullyQualifiedName~NmAttributeParsingTests"
```

Expected: 编译失败，`Node.NmAsrNode` 等属性不存在。

- [ ] **Step 3: 扩展 `Node`**

```csharp
namespace PeakCan.HIL.Core.Dbc;

/// <summary>
/// A CAN bus node (ECU). Identified by a single string name.
/// </summary>
/// <param name="NmAsrNode">来自 <c>BA_ "NmAsrNode" BU_ ... "Yes"</c>。null = 文件没写。</param>
/// <param name="NmAsrNodeIdentifier">来自 <c>BA_ "NmAsrNodeIdentifier" BU_ ...</c>（0–255）。</param>
/// <param name="NmStationAddress">来自 <c>BA_ "NmStationAddress" BU_ ...</c>（0–255）。</param>
public sealed record Node(
    string Name,
    bool? NmAsrNode = null,
    byte? NmAsrNodeIdentifier = null,
    byte? NmStationAddress = null);
```

- [ ] **Step 4: 解析器接入**

1. 在 `ParserState` 字段区新增：

```csharp
// BU_ 行在 BA_ 之前解析；节点属性需要回写到这个列表，所以从 ParseDocument 的局部变量提升为字段。
private readonly List<Node> _pendingNodes = new();
```

2. `ParseDocument` 里删除 `var nodes = new List<Node>();`，把 `nodes.Add(...)` 改为 `_pendingNodes.Add(...)`，构造 `DbcDocument` 时传 `_pendingNodes`。

3. 新增 helper：

```csharp
/// <summary>把一条 BA_ BU_ 属性应用到指定名字的节点上。节点不存在时静默忽略。</summary>
private void UpdateNode(string nodeName, Func<Node, Node> update)
{
    var index = _pendingNodes.FindIndex(n => string.Equals(n.Name, nodeName, StringComparison.Ordinal));
    if (index < 0) return;
    _pendingNodes[index] = update(_pendingNodes[index]);
}
```

4. 在 `ParseBaAttributes` 中 `NmAsrMessage` 分支之后插入：

```csharp
if (attrName is "NmAsrNode" or "NmAsrNodeIdentifier" or "NmStationAddress")
{
    if (Current.Type != TokenType.Keyword_BU_) { SkipUntilSemicolon(); return; }
    Consume();
    if (Current.Type != TokenType.Identifier) { SkipUntilSemicolon(); return; }
    var nodeName = Consume().Lexeme;

    if (attrName == "NmAsrNode")
    {
        bool? value = null;
        if (Current.Type == TokenType.String)
        {
            if (string.Equals(Current.Lexeme, "Yes", StringComparison.OrdinalIgnoreCase)) value = true;
            else if (string.Equals(Current.Lexeme, "No", StringComparison.OrdinalIgnoreCase)) value = false;
            Consume();
        }
        if (value is not null) UpdateNode(nodeName, n => n with { NmAsrNode = value });
    }
    else if (Current.Type == TokenType.Integer
             && int.TryParse(Current.Lexeme, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw)
             && raw is >= 0 and <= 255)
    {
        var b = (byte)raw;
        Consume();
        UpdateNode(nodeName, attrName == "NmAsrNodeIdentifier"
            ? n => n with { NmAsrNodeIdentifier = b }
            : n => n with { NmStationAddress = b });
    }

    if (Current.Type == TokenType.Semicolon) Consume();
    return;
}
```

- [ ] **Step 5: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-hil-core
dotnet test tests/PeakCan.HIL.Core.Tests/PeakCan.HIL.Core.Tests.csproj --filter "FullyQualifiedName~Dbc"
```

Expected: 全绿。

- [ ] **Step 6: 提交**

```bash
git add src/PeakCan.HIL.Core/Dbc/Node.cs \
        src/PeakCan.HIL.Core/Dbc/DbcParser/ParseDocumentFlow.cs \
        tests/PeakCan.HIL.Core.Tests/Dbc/NmAttributeParsingTests.cs
git commit -m "feat(dbc): parse per-node NM attributes (NmAsrNode, station address)"
```

---

## Task 5: 黄金 DBC 与真实 DBC 回归

**Files:**
- Create: `tests/PeakCan.HIL.Core.Tests/Dbc/Fixtures/nm-diagnostic-golden.dbc`（从 DBC_Convert 黄金文件复制）
- Create: `tests/PeakCan.HIL.Core.Tests/Dbc/Fixtures/pure-electric-v4.6.dbc`（从 peakcan-host 测试 fixture 复制）
- Create: `tests/PeakCan.HIL.Core.Tests/Dbc/NmAttributeGoldenTests.cs`
- Modify: `tests/PeakCan.HIL.Core.Tests/PeakCan.HIL.Core.Tests.csproj`（若 fixture 未自动复制，加 `<None Update ... CopyToOutputDirectory>`）

**Interfaces:**
- Consumes: Task 1–4 的全部模型

- [ ] **Step 1: 复制 fixture**

```bash
cd /d/claude_proj2/peakcan-hil-core
mkdir -p tests/PeakCan.HIL.Core.Tests/Dbc/Fixtures
cp "/d/claude_proj2/DBC_Convert/Tests/DBC_Convert.Tests/Fixtures/golden/excel2dbc.diagnostic.etas.expected.dbc" \
   tests/PeakCan.HIL.Core.Tests/Dbc/Fixtures/nm-diagnostic-golden.dbc
cp "/d/claude_proj2/peakcan-host/tests/PeakCan.Host.App.Tests/Fixtures/Can/pure_electric_v4.6.dbc" \
   tests/PeakCan.HIL.Core.Tests/Dbc/Fixtures/pure-electric-v4.6.dbc
```

- [ ] **Step 2: 确认 fixture 会被复制到输出目录**

检查 `tests/PeakCan.HIL.Core.Tests/PeakCan.HIL.Core.Tests.csproj`。如果没有对 `Fixtures\**` 的复制规则，加入：

```xml
<ItemGroup>
  <None Update="Dbc\Fixtures\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

- [ ] **Step 3: 写测试**

创建 `tests/PeakCan.HIL.Core.Tests/Dbc/NmAttributeGoldenTests.cs`：

```csharp
using System;
using System.IO;
using System.Linq;
using PeakCan.HIL.Core.Dbc;
using Xunit;

namespace PeakCan.HIL.Core.Tests.Dbc;

public class NmAttributeGoldenTests
{
    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "Dbc", "Fixtures", name);

    [Fact]
    public void GoldenDiagnosticDbc_ExposesAllGlobalNmValues()
    {
        var text = File.ReadAllText(FixturePath("nm-diagnostic-golden.dbc"));
        var dbc = DbcParser.Parse(text);
        Assert.True(dbc.IsSuccess, dbc.Error?.Message);

        var nm = dbc.Value!.NmAttributes;
        Assert.NotNull(nm);
        Assert.Equal(1280, nm.BaseAddress);
        Assert.Equal(256, nm.MessageCount);
        Assert.Equal(1280, nm.AsrBaseAddress);
        Assert.Equal(2000, nm.WaitBusSleepTimeMs);
        Assert.Equal(2000, nm.TimeoutTimeMs);
        Assert.Equal(1600, nm.RepeatMessageTimeMs);
        Assert.Equal(500, nm.CanMsgCycleTimeMs);
        Assert.Equal("CAN_Network", nm.NetworkName);
    }

    [Fact]
    public void GoldenDiagnosticDbc_Marks0x500AsCannmViaEnumName()
    {
        var text = File.ReadAllText(FixturePath("nm-diagnostic-golden.dbc"));
        var dbc = DbcParser.Parse(text).Value!;

        var msg = dbc.MessagesById[0x500];
        Assert.Equal(4, msg.MsgTypeIndex);
        Assert.True(dbc.TryGetMessageEnumName("MsgType", 4, out var name));
        Assert.Equal("CANNM", name);
    }

    [Fact]
    public void GoldenDiagnosticDbc_ExposesNodeStationAddress()
    {
        var text = File.ReadAllText(FixturePath("nm-diagnostic-golden.dbc"));
        var dbc = DbcParser.Parse(text).Value!;

        var ecuB = dbc.Nodes.Single(n => n.Name == "ECU_B");
        Assert.Equal((byte?)244, ecuB.NmStationAddress);
    }

    [Fact]
    public void PureElectricDbc_HasGlobalNmAttributesButNoNmMarkedMessages()
    {
        var text = File.ReadAllText(FixturePath("pure-electric-v4.6.dbc"));
        var dbc = DbcParser.Parse(text);
        Assert.True(dbc.IsSuccess, dbc.Error?.Message);

        var nm = dbc.Value!.NmAttributes;
        Assert.NotNull(nm);
        Assert.Equal(1024, nm.BaseAddress);
        Assert.Equal(10, nm.MessageCount);
        Assert.Equal(1600, nm.CanMsgCycleTimeMs);

        // 该文件 54 条报文，MsgType 全是 1（COM），没有任何一条能解析成 CANNM。
        Assert.Equal(54, dbc.Value.Messages.Count);
        Assert.DoesNotContain(dbc.Value.Messages, m =>
            m.MsgTypeIndex is { } idx &&
            dbc.Value.TryGetMessageEnumName("MsgType", idx, out var name) &&
            name == "CANNM");
    }
}
```

- [ ] **Step 4: 跑测试**

```bash
cd /d/claude_proj2/peakcan-hil-core
dotnet test tests/PeakCan.HIL.Core.Tests/PeakCan.HIL.Core.Tests.csproj --filter "FullyQualifiedName~NmAttributeGoldenTests"
```

Expected: 4 个用例全绿。若 `Messages.Count` 不是 54，先 `grep -c '^BO_ '` 核实真实条数再改断言——**不要为了让测试变绿而放宽断言**。

- [ ] **Step 5: 提交**

```bash
git add tests/PeakCan.HIL.Core.Tests/Dbc/Fixtures \
        tests/PeakCan.HIL.Core.Tests/Dbc/NmAttributeGoldenTests.cs \
        tests/PeakCan.HIL.Core.Tests/PeakCan.HIL.Core.Tests.csproj
git commit -m "test(dbc): pin NM attribute parsing against golden and real DBCs"
```

---

## Task 6: 版本 bump 与跨仓锁步

**Files:**
- Modify: `src/PeakCan.HIL.Core/PeakCan.HIL.Core.csproj:6`
- Modify: `D:\claude_proj2\peakcan-host\Directory.Packages.props`（`PeakCan.HIL.Core` 版本）

**Interfaces:**
- Produces: `PeakCan.HIL.Core 0.23.0`

- [ ] **Step 1: 改 hil-core 版本**

`src/PeakCan.HIL.Core/PeakCan.HIL.Core.csproj`：

```xml
<Version>0.23.0</Version>
```

- [ ] **Step 2: 改 host 侧锁步版本**

`D:\claude_proj2\peakcan-host\Directory.Packages.props`：

```xml
<!-- NM M1-A: DBC NM 属性解析（spec 2026-11-19-nm-ai-analysis §6.2）。 -->
<PackageVersion Include="PeakCan.HIL.Core" Version="0.23.0" />
```

- [ ] **Step 3: 打包验证**

```bash
cd /d/claude_proj2/peakcan-hil-core
dotnet pack src/PeakCan.HIL.Core/PeakCan.HIL.Core.csproj -c Release -o nupkg
```

Expected: 生成 `nupkg/PeakCan.HIL.Core.0.23.0.nupkg`，退出码 0。

- [ ] **Step 4: 编译宿主侧确认引用一致**

```bash
cd /d/claude_proj2/peakcan-host
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj
```

Expected: 0 errors。本机存在兄弟仓目录时走 `ProjectReference`，不需要额外还原。

- [ ] **Step 5: 提交（两个仓库分别提交）**

```bash
cd /d/claude_proj2/peakcan-hil-core
git add src/PeakCan.HIL.Core/PeakCan.HIL.Core.csproj
git commit -m "chore(release): bump PeakCan.HIL.Core to 0.23.0 for NM attributes"

cd /d/claude_proj2/peakcan-host
git add Directory.Packages.props
git commit -m "chore(deps): pin PeakCan.HIL.Core 0.23.0 (NM attributes)"
```

---

## Task 7（可选，可跳过）: DBC_Convert 写 NM 实例属性

> 依据 spec §14.1：29 位 NM 标记是"减少用户输入"的可选改进，**不是 M1 前置**。跳过本任务不影响计划 B/C —— 用户改走补全对话框即可。做本任务前先确认当前工作区干净、黄金文件可更新。

**Files:**
- Modify: `D:\claude_proj2\DBC_Convert\Core\NmAttributeWriter.cs`
- Modify: `D:\claude_proj2\DBC_Convert\Application\ExcelToDbcService.cs:574-585`（BA_ NM 段）、`:622-690`（BA_ BO 循环）
- Modify: `D:\claude_proj2\DBC_Convert\Tests\DBC_Convert.Tests\Core\NmAttributeWriterTests.cs`
- Modify: `D:\claude_proj2\DBC_Convert\Tests\DBC_Convert.Tests\Fixtures\golden\excel2dbc.diagnostic.etas.expected.dbc`

**Interfaces:**
- Produces: `NmAttributeWriter.BuildNodeInstances(NmAttributeValues values)` → `string[]`（2 行：`NmAsrNode`、`NmAsrNodeIdentifier`）

- [ ] **Step 1: 写失败测试**

在 `Tests/DBC_Convert.Tests/Core/NmAttributeWriterTests.cs` 追加：

```csharp
[Fact]
public void BuildNodeInstances_writes_node_marker_and_identifier_for_the_main_node()
{
    var lines = NmAttributeWriter.BuildNodeInstances(DefaultValues("CAN_Network"));

    Assert.Equal(2, lines.Length);
    Assert.Equal("BA_ \"NmAsrNode\" BU_ ECU_A \"Yes\";", lines[0]);
    Assert.Equal("BA_ \"NmAsrNodeIdentifier\" BU_ ECU_A 244;", lines[1]);
}

[Fact]
public void BuildInstanceLine_for_nm_message_uses_quoted_enum_value()
{
    Assert.Equal(
        "BA_ \"NmAsrMessage\" BO_ 1280 \"Yes\";",
        NmAttributeWriter.BuildMessageInstanceLine("1280"));
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/DBC_Convert
dotnet test Tests/DBC_Convert.Tests/DBC_Convert.Tests.csproj --filter "FullyQualifiedName~NmAttributeWriterTests"
```

Expected: 编译失败，两个新方法不存在。

- [ ] **Step 3: 实现**

在 `Core/NmAttributeWriter.cs` 追加（**新内容含中文时文件必须保持 UTF-8 BOM**）：

```csharp
/// <summary>
/// 节点级 NM 实例属性。当前输入模型只有主节点，因此只为它写；其它节点的缺失由
/// 补全对话框处理（spec §6.1）。
/// </summary>
public static string[] BuildNodeInstances(NmAttributeValues values)
{
    return
    [
        "BA_ \"NmAsrNode\" BU_ " + values.EcuName + " \"Yes\";",
        "BA_ \"NmAsrNodeIdentifier\" BU_ " + values.EcuName + " " + values.NmStationAddress + ";",
    ];
}

/// <summary>单条 NM 报文的 <c>NmAsrMessage</c> 实例行。枚举值必须带引号。</summary>
public static string BuildMessageInstanceLine(string dbMessageId)
    => "BA_ \"NmAsrMessage\" BO_ " + dbMessageId + " \"Yes\";";
```

在 `Application/ExcelToDbcService.cs` 的 `#region BA_ NM` 里，`NmAttributeWriter.Build(...)` 写完后追加：

```csharp
File_WriteStringArray(DbcSw, NmAttributeWriter.BuildNodeInstances(new NmAttributeValues(
    NmBaseAddress: NMStartIdentifier,
    NmMessageCount: NmMessageCount,
    NmAsrWaitBusSleepTime: NmAsrWaitBusSleepTime,
    NmAsrTimeoutTime: NmAsrTimeoutTime,
    NmAsrRepeatMessage: NmAsrRepeatMessage,
    NmAsrCanMsgCycleTime: NmAsrCanMsgCycleTime,
    NmStationAddress: NmStationAddress,
    EcuName: EcuName,
    NetworkName: request.NetworkNodeName)));
```

在 BA_ BO 循环里，`switch (diagnostic.Kind)` 之后、构造 `tWriteString` 之后追加一条 NM 实例行。为避免改动数组长度逻辑，用独立写法：

```csharp
if (diagnostic.Kind == DiagnosticFrameKind.Nm)
{
    File_WriteStringArray(DbcSw, new[]
    {
        NmAttributeWriter.BuildMessageInstanceLine(tMessageID),
    });
}
```

> 注意：`tMessageID` 此时已是 `frameId.DbMessageId`（带 IDE bit），与 `BA_ "MsgType" BO_ <同一 id>` 一致。

- [ ] **Step 4: 更新黄金文件并人工审 diff**

```bash
cd /d/claude_proj2/DBC_Convert
UPDATE_GOLDEN=1 dotnet test Tests/DBC_Convert.Tests/DBC_Convert.Tests.csproj --filter "FullyQualifiedName~ExcelToDbc"
git diff --stat
git diff -- Tests/DBC_Convert.Tests/Fixtures/golden/
```

Expected: 黄金 diff 只包含新增的 `BA_ "NmAsrNode" BU_ ECU_B "Yes";` / `BA_ "NmAsrNodeIdentifier" BU_ ECU_B 244;` / 每个 NM 报文的 `BA_ "NmAsrMessage" BO_ <id> "Yes";`。**出现任何其它行变化就停下来查原因，不要接受。**

- [ ] **Step 5: 编码守卫与全量相关测试**

```bash
cd /d/claude_proj2/DBC_Convert
powershell -ExecutionPolicy Bypass -File tools/check_encoding.ps1
dotnet test Tests/DBC_Convert.Tests/DBC_Convert.Tests.csproj --filter "FullyQualifiedName~NmAttributeWriter|FullyQualifiedName~ExcelToDbc|FullyQualifiedName~BoDiagnostic"
```

Expected: 编码脚本退出 0；测试全绿。

- [ ] **Step 6: 提交**

```bash
cd /d/claude_proj2/DBC_Convert
git add Core/NmAttributeWriter.cs Application/ExcelToDbcService.cs \
        Tests/DBC_Convert.Tests/Core/NmAttributeWriterTests.cs \
        Tests/DBC_Convert.Tests/Fixtures/golden/
git commit -m "feat(nm): write NmAsrMessage/NmAsrNode instance attributes"
```

---

## Self-Review

**1. Spec 覆盖**

| spec 要求 | 任务 |
|---|---|
| §6.2 DbcDocument 增加可空 `NmAttributes`（8 条全局值） | Task 2 |
| §6.2 节点/报文上的 `NmAsrNode`、`NmAsrNodeIdentifier`、`NmStationAddress`、`MsgType`、`NmAsrMessage` | Task 3、Task 4 |
| §6.2 `ParseBaAttributes` 支持全局 / `BO_` / `BU_` 三种形式 | Task 2–4 |
| §6.2 `MsgType` 必须经 `BA_DEF_` 枚举确认，不能假设索引 4 | Task 1、Task 3 |
| §6.2 不按 min/max 拦截；越界保留 | Task 2 测试 `Parse_NmAttributeOutsideDeclaredRange_IsKeptNotRejected` |
| §6.2 未知/畸形属性跳过不抛 | Task 2 测试 `MalformedNmAttribute_IsSkipped...` |
| §6.2 扩展帧键沿用 `ResolveMessageKey` | Task 3 测试 `MsgTypeOnExtendedFrame...` |
| §6.2 测试：黄金 DBC 的值、无 NM 属性的旧 DBC 返回 null、扩展帧、字符串枚举引号 | Task 1–5 |
| §6.2 版本 bump + host 锁步 | Task 6 |
| §6.1 DBC_Convert 补写实例属性 | Task 7（可选） |
| AC1：黄金 DBC 解析出 NM 属性，`0x500` 标记为 CANNM | Task 5 `GoldenDiagnosticDbc_Marks0x500AsCannmViaEnumName` |
| §13.1 `pure_electric_v4.6.dbc` 有全局属性无标记 | Task 5 `PureElectricDbc_HasGlobalNmAttributesButNoNmMarkedMessages` |

**2. Placeholder 扫描**：无 TBD / "类似 Task N" / 无代码的步骤。Task 5 的 54 条断言附了核实方法。

**3. 类型一致性**：`DbcAttributeDefinitions.MessageEnumValues`、`TryGetMessageEnumName`、`NmAttributes` 字段名、`Message.MsgTypeIndex` / `NmAsrMessage`、`Node.NmStationAddress` 在全文一致；计划 B 将直接消费这些名字。

**4. Review Focus 覆盖**：6 条全部落在 Task 1–4 的测试里。

**5. 已知偏离**：未实现 `NM_ATTR_RANGE_MISMATCH`（spec §6.2 提到，但 §7.8 M1 finding 清单不含它，且解析器本就不校验范围）；M2 字段未在模型中预声明。
