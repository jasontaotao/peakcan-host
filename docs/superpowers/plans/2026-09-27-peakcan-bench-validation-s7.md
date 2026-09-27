# S7 台架验证批次 — 实施计划

- spec：`docs/superpowers/specs/2026-09-27-peakcan-bench-validation-s7.md`（v0.2 已定案）
- 纪律：TDD 先红后绿；每任务一次 commit + 本文件 checkbox 更新；提交前自检本文件完整性

## 任务清单

- [x] T0 预检 spike：探针命令结构复用确认；模拟从机批次场景装配路径；C-2 静态扫描对真机 fixture `App_merge_INCA.a2l` 出首份结论；补记 spec
- [ ] T1 Core：`BenchReport` 模型 + JSON 序列化（meta / A 系 7 项 / B 系 4 项 / C 系 3 项 / 每写场景还原校验记录 / 人工判定栏）；先红后绿
- [ ] T2 Core：只读场景执行器（复用 `XcpCapabilityProber` → A 系字段；连接失败 fail-loud）；测试用 `MemorySlaveTransport`
- [ ] T3 Core：读-改-还原编排骨架（保存原值→写→校验→还原→确认还原；try/finally 中断安全；还原失败 fail-loud 记报告）；B-4 越限写拒绝面场景
- [ ] T4 Core：B 系场景（B-1 Cal 写期间 DAQ 并行观测、B-2 MTA 分片时序记录、B-3 与 A-10 共用位图）
- [ ] T5 Core：C 系场景（C-1 广播写 + 还原、C-2 静态跨段扫描（离线，`XcpA2lLoader` 输入）、C-3 在线 MAP 上传耗时 + DAQ 并发计时）
- [ ] T6 CLI：`xcp-bench` 命令装配 + `--i-have-verified-safe-state` 旗标（缺旗标只跑只读场景）+ 退出码（0 批次完成 / 1 fail-loud）+ 报告落盘 `docs/bench/`
- [ ] T7 收尾：全仓门禁 ≥ S6 基线 4398；spec 附录回填表模板；独立评审

## 依赖与红线

- 分层：场景执行器全部落 Core（`Xcp/Bench/`）；CLI 只做装配与 IO——App 层不进本批次
- 写场景唯一入口仍是 `Xcp.Calibration.XcpCalibrationWriter`（DOWNLOAD 红线）；广播写经其既有路径，不新造写协议面
- 复用 `XcpMaster.EnterMemorySequenceAsync` 序列门；批次场景间串行（不并发开第二写路径）
- 测试从机：`MemorySlaveTransport` / `XcpVirtualSlave`（既有 TestKit）；真机样本由用户台架采集
