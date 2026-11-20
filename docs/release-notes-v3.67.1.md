# v3.67.1 发布说明 — PATCH：NM 面板空态

**发布日期:** 2026-11-20

## 修复

- NM 面板在任何分析结果之前不再渲染空的 Profile 条：此前“补全”按钮内容为空，
  在 `DockPanel` 里被压成一条竖缝，看起来像未知控件。
- 补全按钮文案默认“补全”并带 ToolTip；分析完成后仍按缺项显示“缺项 N → 补全 / 输入齐备”。
- 未分析时显示空态提示“点击下方「分析全 trace」或「分析当前窗口」”。

## 验证

- 新增 2 条回归：空态按钮文案；Profile 条/空态提示的可见性契约。
- App 定向（NmViewLayout/NmPanelFlow/NmViewXamlResource）51 / 0；构建 0 warning。
