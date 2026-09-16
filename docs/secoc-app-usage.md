# SecOc（安全车载通信）使用说明（WPF App）

> 2026-09-16 App 接线：SecOc 从 CLI-only 接入用户界面。本文档面向操作者，
> 覆盖密钥导入、受保护 PDU 声明、Trace 验签徽章与 HIL 自动化集成。

## 1. 导入密钥

1. 点工具栏 **🔒 SecOc 设置**。
2. 在「密钥管理」区填 **KeyId**（自命名，如 `vcu_charger_key`）。
3. 点 **导入密钥…**，选择 hex 文本文件（**16 字节 AES-128**，允许空白分隔：
   `00 11 22 … FF`）。
4. 密钥存于 **Windows DPAPI 密钥库**（与 CLI `peakcan-hil --secoc-key import` **共用同一目录**），
   **永不写入配置文件**。配置 JSON 只存 `keyId` 引用。

> CLI 侧对等命令：`peakcan-hil --secoc-key import --key-id <id> --key-file <path>`。

## 2. 声明受保护 PDU

同一「SecOc 设置」窗口内：

1. 「添加 PDU」，按通信矩阵填写：**CAN ID / DataId / FvLen(bits) / MacLen(bits) / KeyId / Mode / InitFv**。
2. 点 **保存**。

配置文件写入 `%LocalAppData%\PeakCanHost\secoc-pdus.secoc`，
schema 与 CLI `--secoc-config` **完全一致**（camelCase 写出、大小写不敏感读回）。

## 3. 连接后看验签徽章

设备设置连接通道后，Trace 每帧新增 **SecOC** 列，四种状态：

| 徽章 | 含义 |
|---|---|
| ✓（绿） | 验签通过 |
| ✗ + 原因（红） | 验签失败（`BadMac` / `Replay` / `FvRollback` / `FvAnomaly` / `Malformed`） |
| 未保护（灰） | SecOc 已配置但该 CAN ID 未在 PDU 列表 |
| 离线不验（灰） | 回放/离线源，或 SecOc 未启用 |

发送（TX）时受保护帧**自动签名**，无需手动操作；伪造/重放帧在 RX 被拒绝并标红。

## 4. HIL 自动化

- **方式一（推荐）**：suite JSON 内嵌 `security` 块（PDU + keyId 引用），HIL run 自动生效；
  `secocAccepted(id)` / `secocRejected(id)` / `secocLastReason(id)` 表达式可用于断言。
  **suite 内嵌块优先于面板字段**。
- **方式二**：HIL 面板「SecOc 配置」选配置文件（等价 CLI `--secoc-config`；
  默认空 = 该 run 不使用 SecOC，零回归）。

## 相关

- CLI 参考：`peakcan-hil --help` 下 `--secoc-config / --secoc-store-dir / --secoc-key`。
- 协议细节：AES-128-CMAC 截断 MAC + freshness 抗重放（spec §5-D / §6）。
