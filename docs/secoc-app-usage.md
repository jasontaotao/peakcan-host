# SecOc（安全车载通信）使用说明（WPF App）

> 2026-09-16 App 接线：SecOc 从 CLI-only 接入用户界面。本文档面向操作者，
> 覆盖密钥导入、受保护 PDU 声明、Trace 验签徽章与 HIL 自动化集成。
> 2026-09-17 缺口修复：per-channel 归属（通道 Handle / suite channels[].security）、
> 工具栏启用状态、保存后需重连提示。

## 1. 导入密钥

1. 点工具栏 **🔒 SecOc 设置**。
2. 窗口顶部填 **KeyId**（自命名，如 `vcu_charger_key`）。
3. 点 **导入密钥…**，选择 hex 文本文件（**16 字节 AES-128**，允许空白分隔：
   `00 11 22 … FF`）。
4. 密钥存于 **Windows DPAPI 密钥库**（与 CLI `peakcan-hil --secoc-key import` **共用同一目录**），
   **永不写入配置文件**。配置 JSON 只存 `keyId` 引用。

> CLI 侧对等命令：`peakcan-hil --secoc-key import --key-id <id> --key-file <path>`。

## 2. 声明受保护 PDU

同一「SecOc 设置」窗口内：

1. 「添加 PDU」，按通信矩阵填写：**CAN ID / DataId / FvLen(bits) / MacLen(bits) / KeyId / Mode / InitFv / 通道 Handle**。
   **通道 Handle**（如 `0x51`，对应连接设置窗口每行的设备句柄）：**空 = 全局兜底**（所有连接通道适用）；
   填了 = **仅该通道适用**。多连接会话（多设备混插）下各通道可按需配不同的 PDU/key 归属。
2. 点 **保存**。**若当前已有连接，保存后需断开重连才生效**（界面会提示）。

配置文件写入 `%LocalAppData%\PeakCanHost\secoc-pdus.secoc`，
schema 与 CLI `--secoc-config` **完全一致**（camelCase 写出、大小写不敏感读回）。

## 3. 启用状态（工具栏）

工具栏右侧显示 **SecOc 状态**：

| 状态 | 颜色 | 含义 |
|---|---|---|
| 未启用 | 灰 | 无 PDU 配置 |
| 就绪 N 条 | 绿 | 配置可解析（密钥已导入） |
| 配置错误 | 红 | JSON 损坏 / keyId 缺失等（悬停查看原因） |

## 4. 连接后看验签徽章

设备设置连接通道后，Trace 每帧新增 **SecOC** 列，四种状态：

| 徽章 | 含义 |
|---|---|
| ✓（绿） | 验签通过 |
| ✗ + 原因（红） | 验签失败（`BadMac` / `Replay` / `FvRollback` / `FvAnomaly` / `Malformed`） |
| 未保护（灰） | SecOc 已配置但该 CAN ID 未在 PDU 列表 |
| 离线不验（灰） | 回放/离线源，或 SecOc 未启用 |

发送（TX）时受保护帧**自动签名**，无需手动操作；伪造/重放帧在 RX 被拒绝并标红。

## 5. HIL 自动化

- **方式一（推荐）**：suite JSON 内嵌 `security` 块（PDU + keyId 引用），HIL run 自动生效。
  **多通道**：`channels[]` 每项可带独立 `security` 块（channel 级优先于顶层块；无则回落顶层块/面板字段）。
  `secocAccepted(id)` / `secocRejected(id)` / `secocLastReason(id)` 表达式可用于断言——
  **表达式自动跟随所在步骤的 `TargetChannel`**（该通道级 security 块的验签统计）；
  步骤无 `TargetChannel`（如 if/while 容器条件）→ 解析默认通道统计。
  **suite 内嵌块优先于面板字段**。
- **方式二**：HIL 面板「SecOc 配置」选配置文件（等价 CLI `--secoc-config`；
  默认空 = 该 run 不使用 SecOC，零回归）。

## 相关

- CLI 参考：`peakcan-hil --help` 下 `--secoc-config / --secoc-store-dir / --secoc-key`。
- 协议细节：AES-128-CMAC 截断 MAC + freshness 抗重放（spec §5-D / §6）。
