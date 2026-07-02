# v2.0.4 PATCH — Real OEM .odx-d import (user-supplied CDD file) (2026-07-02)

## Summary

User feedback 后紧急 PATCH: v2.0.0 MINOR ODX importer 是教学用 minimal
实现——支持 canonical ODX 2.x schema，但**不能** import 真实 OEM 工具
（Vector CANdelaStudio `.odx-d`）导出的 ODX 文件。v2.0.4 PATCH 修复 5
处未覆盖的真实场景。导入用户提供的 Demo_Cdd.odx-d (38kWh BMS, CHINA
MOTOR CORPORATION) 后得到 **99 DTCs / 4 routines / 34 DIDs** （v2.0.0
= 0 / 0 / 0）。

## Items (5)

1. **`.odx-d` extension accepted** (PdxReader + UdsViewModel file dialog)
   - PdxReader 之前只接 `.odx` / `.pdx`。`.odx-d` 通过 PdxReader 默认
     fallback 走 `.odx` 路径（XDocument 解析），但 file dialog filter
     不显示。v2.0.4 filter 加 `*.odx-d`。
   - 不接 `.cdd`（Vector 二进制私有 SDK 格式）——明确不兼容。

2. **`OdxParser` 走 `<BASE-VARIANTS>` 包装** (OdxParser.cs:68)
   - v2.0.0 parser 只识别 `<DIAG-LAYER>` 单数形式。
     ISO 22901 ODX 2.x 也支持 `<BASE-VARIANTS><BASE-VARIANT>...</...>`
     plural wrapper（Vector CANdelaStudio 默认输出格式）。v2.0.4
     parser 同时识别两种，作为 layer-equivalent 处理。
   - 同步修改 DtcDop.cs 使用 input element's namespace（不依赖 hard-coded
     `OdxParser.OdxNamespace`）。

3. **`OdxParser` 接受 empty namespace (no xmlns)** (OdxParser.cs:46-50)
   - v2.0.0 parser hard-fail on root namespace != `http://www.asam.net/xml/odx`。
     真实 `.odx-d` 文件常用 `xsi:noNamespaceSchemaLocation="odx.xsd"` 而
     无 `xmlns` 声明—— root namespace = ""。v2.0.4 接受 "ODX namespace" 与
     "empty namespace" 两种。
   - 所有 element lookups (PARSE-REF、DOP-BASE 等) 现在用 detected
     `XNamespace`，兼容两种格式。

4. **DTC 提取从 `<ECU-SHARED-DATAS>`** (OdxImportService.cs + DtcDop.cs)
   - v2.0.0 在 `<DIAG-LAYER>` descendants 内找 `<DTC-DOP>`。真实文件
     DTC-DOP 在 `<DIAG-LAYER-CONTAINER><ECU-SHARED-DATAS>` 路径。
   - v2.0.4 改为 walk 整个 XDocument 的 `<DTC-DOP>`。
   - 同步支持 `<DTC-REF ID-REF="...">` 跨 DTC-DOP 引用（ODX 2.2+
     共享 DTC pool 的 canonical 表达）。先建立 `DTC id → DTC element`
     index，再 walk `<DTC-REF>` 解析。

5. **DID + Routine 提取从 `<REQUEST>` inline (Vector flat layout)** (新 RequestBasedMappers.cs)
   - 真实 `.odx-d` **没有** `<DOP-BASE>` 或 `<ECU-JOB>` 元素。所有
     UDS 服务以 `<REQUEST>` 形式表达:
       - `<PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE></PARAM>`
         (0x22 ReadDataByIdentifier) → DID
       - `<PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>46</CODED-VALUE></PARAM>`
         (0x2E WriteDataByIdentifier) → DID (writable)
       - `<PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>49</CODED-VALUE></PARAM>`
         (0x31 RoutineControl) → routine
   - 提取规则: walk `<REQUEST>` 找 0x22/0x2E/0x31，取 sibling
     `<PARAM SEMANTIC="ID">` 16-bit CODED-VALUE 作为 DID / routine id。
   - DID `Writable` = OR merge (出现 0x2E → R/W)。
   - Routine `Startable` / `Stoppable` 从 `<DIAG-SERVICE>` SHORT-NAME
     后缀 (`_Start` / `_Stop`) + REQUEST 内 SUBFUNCTION (1/2/3) 启发式
     推断。

## 文件改动

| Path | Type | Lines |
|------|------|-------|
| `src/PeakCan.Host.Core/Uds/Odx/OdxParser.cs` | M | +30 |
| `src/PeakCan.Host.Core/Uds/Odx/DtcDop.cs` | M | refactor: Enumerate/IndexInlineDtcs/TryMapSingle |
| `src/PeakCan.Host.Core/Uds/Odx/RequestBasedMappers.cs` | **+** | new (200+) |
| `src/PeakCan.Host.App/Services/OdxImportService.cs` | M | +30 (DTC index, RequestBased mappers) |
| `src/PeakCan.Host.App/ViewModels/Uds/UdsViewModel.cs` | M | +1 (file dialog filter) |
| `tests/.../Uds/Odx/DemoCddSmokeTests.cs` | **+** | new (5 tests) |
| `tests/.../ViewModels/Uds/OdxImportServiceRealFileTests.cs` | **+** | new (2 tests) |
| `tests/.../Fixtures/Odx/Demo_Cdd.odx-d` | new (untracked, gitignored) | OEM fixture (proprietary, 877KB) |
| `.gitignore` | M | +6 (ignore OEM ODX-D fixtures) |
| `docs/user-manual.html` §11.5 | M | document .odx-d support + LengthBytes=0 caveat |

## Test counts (v2.0.3 → v2.0.4)

| Suite | v2.0.3 | v2.0.4 | Δ |
|-------|--------|--------|---|
| Core  | 379    | 384    | +5 (DemoCddSmokeTests) |
| App   | 442    | 444    | +2 (OdxImportServiceRealFileTests) |
| Infra | 84     | 84     | 0 |
| **Total** | **905 + 6 SKIP** | **912 + 6 SKIP** | +7 |
| Failures (race-test transient) | 0 (24/24 pattern: pass in isolation) | 0 (24/24) | — |

## 已知限制 (v2.0.4 范围内未修)

- **DID `LengthBytes=0`**：ODX-D flat layout 下 length 信息在
  POS-RESPONSE → DOP-REF → DATA-OBJECT-PROP → DIAG-CODED-TYPE BIT-LENGTH
  链中。v2.0.4 PATCH 暂未实现此链——所有 ODX 导入的 DID 都默认
  LengthBytes=0。**DID 列表 + Writable 标志已正确导入**；尝试
  read DID 会因 LengthBytes=0 静默失败。后续 v2.0.5 / v2.1 计划补 length
  resolution。
- **`.cdd` (Vector 二进制私有)**：不支持。需 Vector CANdelaStudio SDK。
- **`ODX 2.5+` schema 元素**：warn + skip（同 v2.0.0 决策 D5）。
- **CONDITIONAL / ECU-VARIANT DIAG-LAYER 子类型**：v2.1 MINOR candidate
  （同 v2.0.0 决策 D4）。

## 用户验证 (CHINA MOTOR CORPORATION 38kWh BMS Demo)

| Output | Count | 备注 |
|--------|-------|------|
| DTCs    | 99    | P0A7D01 电池 SOC 过低报警一级 等 |
| Routines | 4    | checkProgrammingPreconditions / CheckMemory / EraseMemory / CheckProgrammingDependencies (Start) |
| DIDs    | 34    | OR-merged 0x22 (R) ∪ 0x2E (R/W) unique |
| Warnings | 4    | F190 / F187 / F18A / F191 与 built-in defaults 重叠 (last-wins) |

## v2.0.0 后续债务 (closes these in v2.0.4)

- v2.0.0 MINOR closure (Tidy) — 已 ship v2.0.1 / v2.0.2 / v2.0.3 PATCH Tidy
- v2.0.0 不支持真实 OEM ODX-D 文件 — **本 PATCH 修复**

## Process lessons (NEW)

1. **ship 前用真实 OEM 文件做 RED 测试** — v2.0.0 教训: 教学用 canonical
   fixture 测过，**没**用真实 OEM 工具输出测过。v2.0.4 PATCH 用用户
   提供的 Demo_Cdd.odx-d 做 RED baseline。**新 process rule: 任何 ODX
   / CANdelaStudio 相关的 PATCH / MINOR 都要求 fixture 包含真实
   OEM 输出。**
2. **"ODX-D" 是误导命名** — `.odx-d` extension 既是 ISO 22901 ODX-D
   profile，又是 Vector CANdelaStudio 输出格式。两者 layout 差异
   巨大（标准 `<DIAG-LAYER>` vs flat `<BASE-VARIANT>` + `<REQUEST>` inline）。
3. **`<TROUBLE-CODE>` decimal by default** — v2.0.0 parser hard-coded
   `NumberStyles.HexNumber` 导致真实文件（decimal 687361 = 0xA7D01）
   解码错误。修复: 仅 "0x" 前缀走 hex，否则走 decimal。
4. **Namespace 解析** — ODX 文件可能用也可能不用 `xmlns="..."`。Parser
   应该 detect root namespace 然后 thread 通过所有 descendant lookups，
   而不是 hard-code 单一 namespace。

## Pre-ship review

- 0C / 0H / 0M / 0L (code-reviewer dispatched post-commit per established
  pattern; pre-flight inspection by self-review + integration test RED→GREEN).
- 全测 baseline 912 + 6 SKIP / 0 fail (excluding 2 race-test flakes
  pre-existing pattern, 24/24 counter).
- 真实 OEM file 验证: 99 DTCs / 4 routines / 34 DIDs 全部成功导入。

## Ship method

Tier 3 fallback (github.com:443 sustained down at ship time; pattern
established since v1.7.3 PATCH; this is Tier 3 #9).
