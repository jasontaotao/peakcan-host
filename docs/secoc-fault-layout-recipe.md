# D3 布局配方：命名故障 → 裸 CorruptByteIndices（Phase 2 手工展开）

> spec：`docs/superpowers/specs/2026-09-07-secoc-0x27-design.md` §5-D3、§6.1。
> Phase 4 起 studio 从 SecurityBlock 自动展开并在保存时重算；Phase 2 用例按本文档手工计算裸 indices。

## 1. 坐标基准

所有 `byteOffset` / `authLen` / `CorruptByteIndices` 一律**相对数据场首字节 data[0]**（CAN ID 不计入净荷），库 / 装饰器 / studio 三处共用同一基准（spec Rev4 §6.1）。

## 2. 线格式（§6.1）

```
Secured I-PDU 数据场 = AuthenticData ‖ TruncFV ‖ TruncMAC
```

报文尾布局：**FV 区在前，MAC 区贴尾**。

设 `fv = fvLen/8`、`mac = macLen/8`、`dlc` 为数据场长度（字节）：

| 区域 | 起始 index（含） | 结束 index（含） | 长度 |
|---|---|---|---|
| AuthenticData | 0 | `dlc − fv − mac − 1` | `authLen = dlc − fv − mac` |
| TruncFV | `dlc − fv − mac` | `dlc − mac − 1` | `fv` |
| TruncMAC | `dlc − mac` | `dlc − 1` | `mac` |

约束：`authLen ≥ 0`；发送侧必须保证 DLC 覆盖附加区（SecOcChannel TX 按 `FrameLength` 拼帧）。

## 3. 命名故障 → 裸 indices 对照（§5-D3）

| 命名故障 | 故障原语 | CorruptByteIndices | CorruptXorMask | 预期分类 |
|---|---|---|---|---|
| `BadMac` | `Corrupt`（XOR MAC 字节区） | `[dlc−mac .. dlc−1]` | 任意非零（常用 0xFF） | MAC 校验失败；FV 合法帧间被拒帧不破坏 FV 连续性 |
| `ForgedFv` | `Corrupt`（XOR FV 字节区） | `[dlc−fv−mac .. dlc−mac−1]` | 任意非零 | MAC 覆盖 FV → **BadMac**（非 FvRollback，§6.2 对照表） |

真重放（D11）不用字节毁坏原语——用 trace 回放机制重发完整旧帧（FV/MAC 均合法，测单调性检查），见 M2 demo 的 `attack-*.asc`。

## 4. 工作示例（M2 demo 参数）

`fvLen=16`（fv=2）、`macLen=24`（mac=3）、`dlc=8` → `authLen = 8−2−3 = 3`：

| 区域 | indices | demo 数据（FV=0 帧） |
|---|---|---|
| AuthenticData | data[0..2] | `01 02 03` |
| TruncFV | data[3..4] | `00 00` |
| TruncMAC | data[5..7] | `40 2B 59` |

- **BadMac**：`CorruptByteIndices = [5, 6, 7]`，`CorruptXorMask = 0xFF`
- **ForgedFv**：`CorruptByteIndices = [3, 4]`，`CorruptXorMask = 0xFF`（预期仍 BadMac——MAC 覆盖 FV，改 FV 必然 MAC 失败）

suite 片段（`--enable-faults` + Receive 方向，经 `InjectFaultStep`）：

```json
{
  "parameters": {
    "$kind": "injectFault", "FaultType": "Corrupt", "Direction": "Receive",
    "CanId": { "raw": 291, "format": "Standard", "type": "Data" },
    "Probability": "1.0", "DelayMs": "0",
    "CorruptByteIndices": [5, 6, 7], "CorruptXorMask": 255,
    "FaultId": "badmac"
  }
}
```

Phase 2 用 Send/Receive 方向 + 裸 indices 是唯一注入手段；Phase 4 studio 命名故障自动展开后本文档保留为格式依据。
