# ASC 解析器 0xFD 数据字节丢失修复设计

> 日期：2026-09-13
> 状态：草案 v1（待确认。确认后另出实施计划）
> 前置：P7 已合并 main（570f87de）。本期为 **Host.Core 桌面共享代码缺陷修复**——P7 真机验收发现，候选清单中优先级最高。
> 参考：[2026-09-13-mobile-trace-viewer-p7.md](../plans/2026-09-13-mobile-trace-viewer-p7.md) 收尾记录、v3.49 MINOR（ASC 格式单源）、v3.11.5 PATCH（Vector 方言）。

## 1. 背景与根因

P7 真机验收时 `search_signal_trace` 统计 max=1138.5 ≠ 理论值 999.25。排查发现设备缓存帧只有 7 字节、**首数据字节缺失**；fixture 生成器把 0xFD 重映射为 0xFC（`safe()`）后统计立刻吻合，坐实解析器吞字节。当时为不阻塞 P7，记录在案并重映射绕过。

根因（`AscFormat.TryParseDataLine` 数据区循环，AscFormat.cs:182-203）：`case "fd"` **无条件**把 token 当 `FrameFlags.Fd` flag 吞掉。而逐字节方言的数据区里，0xFD 字节恰好写作独立 token "FD"——被当 flag 吞掉，不进 data。

**波及面比验收发现的大**：

- PCAN 风格逐字节（验收 fixture `ts ch id 8 FD 00 02 …`）：任意位置 0_FD 都丢。
- **CANoe 经典帧本身就是逐字节**（现有测试 `d 8 AA BB CC DD EE FF 00 11`）：含 0xFD 的 CANoe 导出文件同样丢。
- 自家 writer（`AscFormat.WriteDataLine`）**不受影响**：数据是连体单 token（`Convert.ToHexString`），0_FD 只作为 token 片段出现，不会被误判；但自家尾部 flag token（`fd`）恰恰依赖这个 case 解析回来——**修复必须保持 round-trip**。

为什么只有 "fd" 有歧义：`brs`/`esi`/`error`/`rx`/`tx` 都含非 hex 字符，永远不可能是合法数据 token；只有 "FD" 恰好是合法 hex（0xFD）。

## 2. 现状盘点（证据）

- **解析器单一数据源**：`AscParser.DataLineParserFlow` 自 v3.49（5f99db03）起整体 delegate 到 `AscFormat.TryParseDataLine`（≈30 LoC 壳）→ 修复落**一处**，桌面 Replay（`ParseLinesFlow`）与流式/移动导入（`AscStreamingSource`）同时生效。
- 消费方：桌面 Replay `AscParser.ParseAsync → ParseLinesFlow → AscFormat`；流式回放与移动端导入 `AscStreamingSource → AscFormat`；移动端 `DurationScanner` 只用 `TryParseDateHeader`（不涉数据行）。
- 数据循环 `case "fd"` 承袭 v3.49 之前的内联 DataLineParser 语义（服务自家 round-trip）；扫描循环（marker 前）的 "fd" 跳过来自 cce31dae（ASC BLF 兼容）。
- **已知死代码**：marker 分支的 `tokens[scan].Equals("fd")`（AscFormat.cs:157、166）不可达——扫描循环已把连续 rx/tx/fd 消费完，进入 marker 判定时 `tokens[scan]` 不可能是 fd。本期**不动**（零行为收益，徒增 diff）。
- **已知限制（维持现状）**：DLC 前裸 `fd` 标记方言（`ts ch id fd N …`、`ts ch id Rx fd N …`）今天就会被拒（DLC 解析失败），CANoe FD 的实际形状是 `l N`（有测试锁定），不在本期扩大支持面。
- DLC 放宽语义（AscFormat.cs:238-247：BLF 转 ASC 时行内 DLC 可能是协议值 9-15 而实际字节 12-64，最终 `frame.Dlc = data.Count`）——本期不碰。

## 3. 修复设计：按位置消歧

数据区循环内已知两个量：行内声明的 `dlc` 与已收集的 `data.Count`。规则：

```csharp
case "fd":
    if (data.Count < dlc) break;   // 数据未凑满声明 DLC → 这是数据字节 0xFD，落回下方 hex 解析
    flags |= FrameFlags.Fd; continue;
```

（switch 内 `break` 只跳出 switch，接续到既有的 hex 解析路径，与 default 分支同路。）

各输入形状逐一验证：

| 输入形状 | data.Count vs dlc | "fd" 判定 | 结果 |
|---|---|---|---|
| 逐字节 `d 8 FD BB CC …`（首字节，验收场景） | 0 < 8 | 数据字节 | ✓ 修复 |
| 逐字节 `d 8 AA FD CC …` / 末尾 0xFD | < 8 | 数据字节 | ✓ 修复 |
| PCAN 无标记 `ts ch id 8 FD 00 02 …` | 0 < 8 | 数据字节 | ✓ 修复（P7 fixture 正是此形状） |
| 自家单 token `… 8 001102FD… fd brs` | 8 ≥ 8 | flag | ✓ round-trip 保持 |
| CANoe FD `l 8 AA BB … 11 fd`（满数据后 flag） | 8 ≥ 8 | flag | ✓ 保持 |
| 自家 0 数据帧 `… 0  fd` | 0 ≥ 0 | flag | ✓ 保持 |
| FD 协议 DLC 错配（声明 9、实际 12 字节后遇 fd） | 12 ≥ 9 | flag | ✓ 保持（BLF 兼容路径） |
| 畸形：声明 dlc=8 只写 2 字节后跟 fd | 2 < 8 | 数据字节（误） | 畸形输入良性退化，记为已知限制 |

**残留歧义（接受并记录）**：行内声明 DLC 小于实际字节数、且 0_FD 出现在声明范围之外——仍判 flag。真实文件未见此形状（自家格式精确匹配、CANoe/PCAN 逐字节精确匹配）。

## 4. 测试与验收

TDD，先红后绿：

1. 新增 `AscParserTests` 4 例（走 `AscParser.ParseAsync` 全路径）：
   - 首字节 0xFD（`d 8 FD BB CC DD EE FF 00 11`）→ 8 字节、`Data.Equal(0xFD, 0xBB, …)`、无 Fd flag；
   - 中间 0xFD、末尾 0_FD 各 1 例；
   - 满数据后缀 `fd`（`l 8 AA BB … 11 fd`）→ Fd flag 置位（守护 ≥ 分支不回归）。
2. 既有测试必须全绿——自家格式零回归的直接证据：`WriteDataLine_FdFrame_ParseBackRoundTripEqual`、`Parse_RecordServiceConcatenatedHexFormat_RoundTrip`、`Parse_CanoeFdDlc_LToken_SetsFdFlag` 等。
3. 全量回归：Host.Core.Tests（1123）、Host.Infrastructure.Tests、Mobile.Core.Tests（275）。桌面 UI 层（Host.App）与移动端代码零改动。
4. **真机回归**（复用 P7 验收链路）：恢复真实 0xFD 的原版 fixture（去掉 `safe()` 重映射），生成器同场输出理论统计 → 推送 → 导入 → `search_signal_trace` 统计与理论值比对 + Browse 显示完整 8 字节。设备不在位时以单测 + 理论对拍收尾并在计划状态块记录。

## 5. 影响面与风险

- 变更面：`src/PeakCan.Host.Core/Replay/AscFormat.cs` 一个 case 块（约 +2 行）+ `tests/PeakCan.Host.Core.Tests/Replay/AscParserTests.cs`。这是**刻意的 Host.Core 变更**（缺陷本体在共享解析器），桌面 UI、移动端零改动。
- 风险：低。消歧规则只改变 "fd" token 在数据未满时的归类；其余 flag token 行为不变。桌面与移动同源，单测覆盖即等价覆盖两端。
- 回滚：单 commit revert 即可。

## 6. 决策表

| # | 决策 | 说明 |
|---|---|---|
| 1 | 位置消歧：`data.Count < 行内 dlc` → 数据字节；`≥` → flag | 唯一能同时满足"外部逐字节 0_FD 不丢"与"自家尾部 flag 可回读"的规则；无需格式探测/heuristic 猜方言 |
| 2 | 只动数据循环 `case "fd"`；扫描循环与 marker 分支不碰 | marker-fd 是死代码（记录不删）；pre-marker 裸 fd 方言维持"拒绝"现状，不扩大支持面 |
| 3 | DLC 放宽语义（最终 `frame.Dlc = data.Count`）不动 | BLF 兼容路径不受影响；行内 dlc 仅作消歧基准 |
| 4 | 测试落 `AscParserTests`（`ParseAsync` 全路径） | 同一函数覆盖桌面 `ParseLinesFlow` 与流式/移动 `AscStreamingSource` 两个消费方 |
| 5 | 真机验收用恢复 0_FD 的 P7 fixture 复跑 `search_signal_trace` | 闭环 P7 收尾记录；生成器同场输出理论统计作比对基准 |
| 6 | 分支 `fix/asc-fd-data-byte`；单 fix commit（测试+修复同行），文档单独 docs commit | 小修复不拆任务提交 |
