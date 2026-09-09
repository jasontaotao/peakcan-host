# SecOC M2 Demo 走查脚本（≤5 分钟）

> spec：`2026-09-07-secoc-0x27-design.md` §8 Phase 2 DoD ③④。演示「正常帧全绿 → 注入 BadMac → 单帧变红 + 计数器 +1 → 恢复全绿」+ 重放/回滚攻击分类，同时作 M2 回归素材（对应 §5-D1 双向语义断言的 headless 路径）。
>
> App trace 徽标（DoD ④ 的 WPF 侧）：受保护帧经旁路表渲染 ✓/✗，离线/回放显示源标注「离线不验」——由 `SecOcBadgeTests` + `SecOcChannelTests` 固化为回归（App 侧 SecOC 通道接线在 Phase 3 落地，届时本走查第 6 步可直接目视徽标）。

## 0. 前置（一次性，约 1 分钟）

```powershell
# 1) 生成 demo 资产（trace/suite/PDU 配置/DBC；密钥只经 stdin 参与 MAC 计算，绝不落盘）
python scripts/secoc-demo/gen_demo_assets.py --key-hex <32位hex演示密钥> --out-dir artifacts/secoc-demo

# 2) 导入密钥到本地 DPAPI KeyStore（--store-dir 指向临时目录，不进 git）
peakcan-hil --secoc-key import --key-id demo-key --key-file <key文件> --store-dir <demo-store>
peakcan-hil --secoc-key list --store-dir <demo-store>          # 应列出 demo-key
```

密钥文件**不得提交**（spec D4 / .gitignore 防护）。生成产物只含 keyId 引用与 MAC，可安全入库。

## 1. 全绿基线（~1 分钟）

```powershell
peakcan-hil --dbc artifacts/secoc-demo/demo.dbc `
            --trace artifacts/secoc-demo/demo-trace.asc `
            --suite artifacts/secoc-demo/demo-suite-green.json `
            --secoc-config artifacts/secoc-demo/demo-pdus.json `
            --store-dir <demo-store>
```

**预期**：`ALL PASSED`（AllFramesAccepted 3/3 steps）。8 帧全部验签通过，`secocAccepted(0x123)=true` 且 `secocRejected(0x123)=false`。

## 2. 注入 BadMac → 单帧变红 + 计数器 +1 → 恢复全绿（~1.5 分钟）

```powershell
peakcan-hil --dbc artifacts/secoc-demo/demo.dbc `
            --trace artifacts/secoc-demo/demo-trace.asc `
            --suite artifacts/secoc-demo/demo-suite.json `
            --secoc-config artifacts/secoc-demo/demo-pdus.json `
            --store-dir <demo-store> --enable-faults
```

时间线（trace 帧距 400ms）：

| 步骤 | 动作 | 现象 |
|---|---|---|
| 0–600ms | 帧正常 | RX accepted（Debug 级日志） |
| 600ms | `injectFault` XOR MAC 区 data[5..7] | — |
| 800–1600ms | 3 帧被毁坏 | 日志 `SecOC RX rejected id=0x123 reason=BadMac`（计数器 +1×3） |
| ~1900ms | `clearFault` | — |
| 2000ms+ | 帧恢复 | accepted（FV 候选集 k=+1 跨隙重同步，§6.2） |
| 断言 | `if(secocRejected)` / `if(secocAccepted)` | **`ALL PASSED`（7/7 steps）** |

缺钥场景（D4 启动拦截，反向演示）：换 `--store-dir` 指向空目录 → 启动即报 `keyId 'demo-key' not found in KeyStore`，不静默裸奔。

## 3. 重放 / 回滚攻击分类（~1.5 分钟）

```powershell
peakcan-hil --dbc artifacts/secoc-demo/demo.dbc --trace artifacts/secoc-demo/attack-replay.asc `
            --suite artifacts/secoc-demo/attack-suite.json --secoc-config artifacts/secoc-demo/demo-pdus.json --store-dir <demo-store>
peakcan-hil --dbc artifacts/secoc-demo/demo.dbc --trace artifacts/secoc-demo/attack-rollback.asc `
            --suite artifacts/secoc-demo/attack-suite.json --secoc-config artifacts/secoc-demo/demo-pdus.json --store-dir <demo-store>
```

**预期**：
- `attack-replay.asc`（同帧重发 FV=3,3）→ `reason=Replay`
- `attack-rollback.asc`（旧帧迟到 FV=2，last=3）→ `reason=FvRollback`
- 两 run 均 `ALL PASSED`（攻击被拒 → `secocRejected=true`）。

注意：攻击帧时间戳必须单调递增（回放调度按时间序分发）——「旧帧」体现为 FV 回退而非时间戳回退。

## 4. 回归素材

| 素材 | 覆盖 |
|---|---|
| `tests/.../Channel/SecOc/SecOcChannelTests.cs` | D1 五断言（TX/RX BadMac、FvRollback、Replay、单层装饰） |
| `tests/.../HIL/Expressions/SecOcFunctionRegistryTests.cs` | secoc* 表达式三函数 |
| `tests/PeakCan.Host.App.Tests/.../SecOcBadgeTests.cs` | trace 徽标三态 + 断开清理钩子 |
| `scripts/secoc-demo/gen_demo_assets.py` | demo 资产再生成（改参数即得新回归夹具） |
| 本走查 Run 1–3 | headless 端到端链路（CLI → 组装 → 验签 → 注入 → 断言） |
