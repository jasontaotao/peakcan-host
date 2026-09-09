# SecOC M3.3: Client/Server Lockout 语义对照表

E2E 走查（`SecurityAccessEndToEndTests`）沉淀的两侧语义差异，实现依据 spec §6.3（client 侧 `UdsSecurity`、server 侧 `SecurityAccessServer`）。

| # | 场景 | client（tester 侧 host 强制） | server（线上 ECU 权威） |
|---|------|------------------------------|------------------------|
| 1 | 锁定期 requestSeed | `IsLocked` 先于线上抛 `UdsSecurityLockedException`（携带 `RemainingDelay`），**不发帧** | 回 NRC `0x36`（首次，36Then37 模式）或 `0x37`（Always37 / 第二次起），不回 seed |
| 2 | 错误 key 计数 | 收到 0x35/0x36/0x37 后 `RecordFailedAttempt`（仅 SendKey 计数；requestSeed 失败不计数） | 验 key 失败即计数；第 3 次失败进 `Delayed`（锁定窗口 5s） |
| 3 | 锁定恢复 | `UdsSecurity` 用注入的 `TimeProvider` 判断过期（E2E 用 `FakeTimeProvider` 零真实等待） | 懒过期：每次请求先 `RefreshLocked`（同一 TimeProvider） |
| 4 | 已解锁后再 sendKey | client 正常发送 | NRC `0x24`（requestSequenceError：无进行中的 seed 序列） |
| 5 | requestSeed 重复 | 每次全握手重新取 seed | 同一 seed 复发（key 未发送前，ISO 14229-1） |
| 6 | 全零 seed | 仅 server 端已解锁时回零 seed；client 不感知 | 已解锁回零 seed 且存 `CurrentSeed`，供后续 sendKey 序列 |

## E2E harness 关键事实（踩坑记录）

- **CanIdConfig 双视角**：ECU 侧（`StatefulVirtualEcu`）是 **ECU 视角**——`RequestId` = ECU 发送 ID（= tester 的 ResponseId），`ResponseId` = ECU 接收 ID（= tester 的 RequestId）；client 侧（`UdsClient`）是 **tester 视角**。写反则全部超时。
- `UdsClient.SendRequestAsync` 只剥正响应 SID+0x40（`data[1..]`）；`SecurityAccessAsync`（3 参）再剥 subfunction 返回 seed。**2 参全握手返回的是 SendKey 正响应 `[sub]`**（长度 1），不是 seed。
- 客户端脚本状态机需为 0x10 提供完整正响应（`[0x50, sub, P2×4]` ≥5 字节），否则 `DiagnosticSessionControlAsync` 抛 Invalid response。
- 双侧共享同一 `FakeTimeProvider`（`Microsoft.Extensions.TimeProvider.Testing`）→ 锁定窗口推进零真实等待。
