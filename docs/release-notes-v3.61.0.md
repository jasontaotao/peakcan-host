# v3.61.0 MINOR — UDS View DID 数据类型识别

**Date:** 2026-07-20
**Type:** MINOR (能力新增)
**Ship 上下文声明:** 本 release 仅含 DID 数据类型识别这条主线改动
(branch `feature/v3-49-0-minor-did-type-recognition` 上 5 个 commit)。
工作区存在的其它 W41 (Trace/Llm) 未提交改动**不属本 release 范围**,
由后续工作流另行处理。

## 背景

既有的 UDS View 在 ODX 导入后只能识别 DID 的 id、name、长度字节,
**数据类型元数据全丢**:
- DOP-BASE 路径 `DidDop.TryMap` 把 `LengthBytes` 写死 0, 且注释里
  `BASE-TYPE=` 是错的(真实 OEM `Demo_Cdd.odx-d` 767 处实际是
  `BASE-DATA-TYPE=`)。
- REQUEST 路径 `RequestBasedMappers.ExtractDidLengths` 仅累加
  `BIT-LENGTH` 字节, 扔掉 `COMPU-METHOD` (物理解算) / `UNIT` / 复合
  DID 多字段偏移。
- Read 命中后 `BitConverter.ToString` 原样 hex 透传, 无类型解码、
  无 ASCII 还原、无物理量换算、无枚举文本翻译。

## 变更概览

### Core (`PeakCan.Host.Core.Uds.Odx`)

新增类型模型与解析器, 覆盖 ODX 类型三层 (基础类型 / 物理换算 / 单位):

- **typemodel** (`DidFieldType.cs`): `DidBaseType` 枚举 (UInt32/Int32/
  Float64/AsciiString/Unicode2String/ByteField/Unknown), `CompuMethod`
  (Identical/Linear/Texttable), `DidUnit` (避开既有
  `PeakCan.Host.Core.Unit` 命名冲突), `DidField` record。
- **DOP-BASE 解析重构** (`DidDop.cs`): 修正 `BASE-TYPE→BASE-DATA-TYPE`
  注释 bug, 解析 `BASE-DATA-TYPE`+`BIT-LENGTH`/`MAX-LENGTH`
  (STANDARD/MIN-MAX-LENGTH-TYPE), 产出 `DidField`。
- **CompuMethodParser** (新增): IDENTICAL / LINEAR 一阶有理多项式
  `physical=(V0+V1*raw)/D0` (缺分母=1) / TEXTTABLE 枚举文本表 + UNIT-REF
  跨文档索引解析。
- **RequestBasedMappers.ExtractDidFields** (新增): REQUEST 路径
  复合 DID 字段表, 每 `SEMANTIC=DATA` PARAM 产出一条 `DidField`
  (ByteOffset 优先取 BYTE-POSITION, 缺则累计回退)。
- **DidValueDecoder** (新增): 把 ReadDataByIdentifier 原始字节解码为
  `DecodedField[]` — ASCII/UTF-16BE 字符串, BE 整数 + LINEAR 物理换算
  / TEXTTABLE 枚举查表 (未命中安全回退), IEEE754 BE 浮点, ByteField
  hex 透传。短 payload / 未知类型不抛异常。
- **DidDefinition** (改): 加 init 字段 `Fields: IReadOnlyList<DidField>`
  (默认空数组, 5 参构造零破坏 + JSON round-trip 兼容)。

### App (`PeakCan.Host.App`)

把 Core 已就绪的类型表/解码能力串到 UDS View UI 数据绑定:

- **OdxImportService** (改): `ParseAndIndexOneDocument` 在 0x22/0x2E
  DID 构造时填充 `Fields = ExtractDidFields[did]` (复合 DID 多字段),
  `LengthBytes` 优先字段表累计字节 (更精确覆盖复合 DID)。
- **DidRow** (改): 加 ObservableProperty `Fields` / `DecodedFields`,
  只读计算属性 `TypeDisplay` (单标量 `UInt32[16]` / 字符串
  `AsciiString[17B]` / 复合 `ByteField ×8` / 无字段 `(no type)`) 与
  `DecodedSummary` (字段物理值 ` | ` 拼接, 无解码回退 `ReadValue`)。
- **DidPanelViewModel** (改): `BuildRow` 单点映射把 `DidDefinition.Fields`
  注入 `DidRow.SetFields`; `ReadDidAsync` 成功分支在 `Fields` 非空时调
  `DidValueDecoder.Decode` 填 `row.SetDecoded`, `LastResult` 优先展示
  `DecodedSummary` (有字段时显示物理值, 无则回退既有 hex)。
- **UdsWindow.xaml** (改): DIDs tab 加 `Type` 列 (绑 `TypeDisplay`),
  `Value` 列从 `ReadValue` 改绑 `DecodedSummary`, 右侧详情面板加
  `Decoded fields` ItemsControl (每字段 Name / Physical / Unit)。

## 测试

TDD RED→GREEN, **42 个新测试全绿**:

- `DidFieldTypeTests` (7): 类型模型行为契约。
- `DidDopMappingTests` (6, 含 3 旧兼容): DOP-BASE BASE-DATA-TYPE +
  BIT-LENGTH 解析。
- `DidDopCompuUnitTests` (6): COMPU-METHOD IDENTICAL/LINEAR/TEXTTABLE +
  内嵌 UNIT。
- `RequestBasedMappersFieldsTests` (4): 真实 `Demo_Cdd.odx-d` 上
  CellVolt (DID 0x0102) 复合 DID 8 字段 + 偏移序列
  `3,185,367,373,379,385,391,397` + ByteField/IDENTICAL。
- `DidValueDecoderTests` (9): VIN 17 字符 ASCII / 温度 `0.5*raw-40` /
  Percent `100*raw/255` / 枚举查表 / 未命中回退 / ByteField / 复合多字段
  偏移 / 短 payload 安全 / 无字段整 hex。
- `OdxPanelRefreshTests.DidPanelFieldPropagationTests` (2): ctor /
  RefreshFromDatabase 把 `DidDefinition.Fields` 透传 `DidRow`。
- `DidPanelViewModelTests` 新增 (2): ASCII 字段 Read→解码为字符串;
  无字段 Read→仅有 hex (既有行为不回归)。
- `OdxImportServiceRealFileTests` 新增 (1): 真实 OEM 文件导入后
  `DidDatabase.Find(0x0102).Fields` 含 8 字段端到端验证。

**回归验证:**
- Core 全测试 **596/596** (1 处 transient timer flake 经 retry 通过,
  与 DID 改动零关联, `git diff --name-only` 证实未触及 Replay 域)。
- App 全测试 **886 通过 / 3 SKIP** (0 fail; SKIP 为既有硬件相关)。
- App build 0 错误 (DID 改动 UdsWindow.xaml/DidRow/DidPanelViewModel
  编译干净)。
- **未跑全解决方案 build verify:** 工作区存在 40 个预存 W41
  (Trace/Llm) 未提交改动, 其 `CS8602/CS8603/CS0169` 警告在
  `TreatWarningsAsErrors=true` 下会让全解决方案 build 失败。这些改动
  **不属本 release 范围**, 不在 ship 验证内动 (遵循 [[pkm-capture-throttling-rule]]
  / [[peakcan-git-stash-untracked-danger]] 边界原则)。

## Commits (本 release)

```
ae9f3aa  M1: DID 数据类型识别 DOP-BASE 路径 (T0.1+T0.2+T1.1+T1.2)
6b15832  M2: REQUEST 路径复合 DID 字段表 + 接入导入 (T2.1+T2.2)
b10f3f5  M3: DidValueDecoder 原始字节解码为物理值 (T3.1)
5870077  M4: VM 层串接类型表/解码到 UI (T4.1+T4.2+T4.3)
d1534a4  M5 T5.1: UdsWindow.xaml Type 列 + 解码字段表
```

## 示例体验

ODX 导入 `Demo_Cdd.odx-d` 后, DIDs tab:

| ID | Name | Length | Type | R/W | Value |
|----|------|--------|------|-----|-------|
| 0x0102 | DID_0x0102 | 400 | ByteField ×8 | R | (Read 后 8 字段解码汇总) |
| 0xF190 | VIN | 17 | AsciiString[17B] | R | `1HGCM82633A123456` (Read 后解码字符串) |
| 0xF401 | DID_0xF401 | 2 | UInt32[16] | R | `10°C` (Read 后线性物理量) |

详情面板展开得到每字段 `Name | PhysicalValue | Unit`。
