# SecOC M4 Demo 走查（Phase 4：studio 导入矩阵 → 生成套件 → host run → 报告）

> spec：`docs/superpowers/specs/2026-09-07-secoc-0x27-design.md` §8 Phase 4。
> 前置：hil-core 0.21.0（host/studio 双 pin）；M2 demo 资产可复用（`scripts/secoc-demo/gen_demo_assets.py`）。

## 0. 前提

- `peakcan-hil-core` 已发包 0.21.0（host / studio `Directory.Packages.props` 双 pin 同一版本）。
- host：`dotnet build PeakCan.Host.slnx -c Release`；studio：`dotnet build PeakCan.Studio.slnx -c Release`。
- 一份 OEM 通信矩阵（含 SecOC 扩展列），样例：`samples/secoc/SecOC_CommMatrix_Sample.xlsx`。
  - ⚠️ 样例 `VCU_ChrgCtrlCmd` 行的 Freshness 截断 = 24bit，**超 v1 上限（≤16）**。导入时面板「校验」角标会告警；
    生成攻击套件与 host 运行会**跳过该 PDU**（fail-loud）。demo 前把该行改为 16（或删行）。
- 密钥：演示密钥**不得提交**；经 `--secoc-key import` 进本机 DPAPI KeyStore，suite 只写 `keyId`。

## 1. studio：导入矩阵 + 保存 suite

1. 启动 studio，打开 DBC（任一兼容 DBC）。
2. 菜单「视图 → SecOC 安全」（或工具栏「◀ SecOC」）展开 SecOC 面板。
3. 点「导入通信矩阵」→ 选 `SecOC_CommMatrix_Sample.xlsx`。
   - 面板出现 4 条 PDU（TBOX_RemoteCtrlCmd / VCU_TorqReq / ADAS_LatCtrlCmd / VCU_ChrgCtrlCmd），
     每行含 CAN ID / Data ID / FV / MAC / Key ID / 模式。
   - 若未修正 24bit 行，Issues 区出现 `FvLenBits must be ... ≤ 16` 校验告警。
4. 修正/删除越限行，确认 Issues 清空。
5. 菜单「文件 → 另存为 Suite...」保存 `demo.suite.json` —— 文件内 `security` 块只含 `keyId`（无密钥）。

> 保存守卫：若 SecOC 面板存在校验错误，保存被拦（提示修正或勾选「仍然保存」强制落盘），
> 避免「一个 PDU 写错 → 整块静默丢失 → host 不验签」。

## 2. studio：生成攻击套件

1. SecOC 面板点「生成攻击套件」→ 选保存路径 `demo.attack.suite.json`。
2. 文件内容：
   - 携带同一 `security` 块（host 据此装配 SecOcChannel）。
   - 每个受保护 PDU 生成 `BadMac` / `ForgedFv` 两个用例：`injectFault`(Corrupt, Receive, 计算出的裸字节索引)
     → `delay` → `clearFault` → `if (secocRejected(id))` 断言。
3. 统计语义：host 引擎在每个 case 开头清零 SecOC 验签统计（spec Rev7），`secocRejected(id)` 只反映本用例内
   的拒绝——同一 CAN ID 的多个攻击用例互不干扰，无需拆分为多次 run。

## 3. host：导入密钥 + 运行

```powershell
# 3.1 导入演示密钥（一次性；密钥文件不得提交）
peakcan-hil --secoc-key import --key-id KEY_SLOT_05 --key-file <keyfile> --store-dir <demo-store>
peakcan-hil --secoc-key list --store-dir <demo-store>     # 应列出 KEY_SLOT_05

# 3.2 正常 suite（应为全绿基线）
peakcan-hil --dbc <demo.dbc> --trace <demo-trace.asc> --suite demo.suite.json `
            --store-dir <demo-store> --format json --output result.json

# 3.3 攻击 suite（应为「攻击被拒」= 通过）
peakcan-hil --dbc <demo.dbc> --trace <attack.asc> --suite demo.attack.suite.json `
            --store-dir <demo-store> --format json --output attack-result.json
```

- 缺钥场景：`--store-dir` 指向空目录 → 启动即报 `keyId '...' not found in KeyStore`（不静默裸奔）。
- suite 内 `security` 块优先于 `--secoc-config`；两者都无 → 不装配 SecOcChannel（无保护运行，向后兼容）。

## 4. 报告

结果 JSON 交 studio「结果分析」面板打开（Run 直接内嵌时退出自动加载），或 host `--format html` 生成 HTML 报告。

## 5. 回归素材

| 素材 | 覆盖 |
|---|---|
| studio `SecOcImportTests` / `SecOcViewModelTests` | 矩阵导入、故障展开、攻击生成、保存/加载接线、保存守卫 |
| host `SecOcBlockConsumerTests` | suite `security` 块消费、块优先、缺钥 fail-loud、多通道拒绝 |
| host `SecOcChannelTests` + `SecOcFunctionRegistryTests` + `SecOcBadgeTests` | M2 双向语义 / 表达式 / trace 徽标 |
| `docs/secoc-m2-demo-walkthrough.md` | headless 注入/重放/回滚分类 |

## 已知限制（见 spec Rev7）

- 样例矩阵 24bit freshness 超 v1 上限 → 该 PDU 被跳过（fail-loud）。
- Replay / FvRollback 攻击需录制 trace（spec D11），不在攻击套件生成器内；用户以 trace 回放套件自行编写。
- multi-channel + `security` 块：v1 显式拒绝（无 per-channel PDU 绑定）。
