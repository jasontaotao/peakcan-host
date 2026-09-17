# SecOc App 接线：三个设计缺口修复

## Context

SecOc App 接线（2026-09-16 分支，已合并 main @f06ef7a0）上线后发现三个设计缺口：

1. **配置粒度是全局、语义是每连接**：固定文件 `%LocalAppData%\PeakCanHost\secoc-pdus.secoc` 的 PDU 配置对所有连接通道统一套用（`ChannelConnectionCoordinator.ConnectAllAsync` 第 112 行全局一次 `provider.Invoke()`，循环内逐槽共享同一字典）。多连接会话（多设备混插、多总线多 ECU）下，不同 SecOc 域的通道会错误套用同一份 PDU/key → 该验的没验、不该验的拼命报 ✗。
2. **改动后无"需重连生效"提示**：provider 只在连接时读盘；已连接状态改配置 → 当前会话不生效，UI 无提示。
3. **启用状态不可见**：工具栏没有 SecOc 启用/就绪/配置错误指示，配了 PDU 忘导入密钥只能从连接失败倒推。

目标：三条路径（AppShell 手动连接 / HIL run）per-channel 绑定 + 重连提示 + 状态可见。per-channel 归属维度 = **通道 Handle**（`ChannelInfo.Handle`，连接槽现有标识，joiner.Configure 同维度）。

约束：旧配置（无 handle 字段）向后兼容（handle 空 = 全局兜底）；单通道零回归。

---

## 缺口 1a — AppShell 手动连接路径 per-channel（按 handle）

### Schema（本仓库 `src/PeakCan.Host.Infrastructure/Channel/SecOc/SecOcConfigLoader.cs`）

- `SecOcPduEntry`（L28-37）加字段：
  ```csharp
  /// <summary>通道 Handle（hex，如 "0x51"）。空 = 全局兜底（所有通道适用）。</summary>
  public string Handle { get; init; } = "";
  ```
  写 JSON 为 `"handle"`（JSON 序列化默认按属性名）；读侧大小写不敏感（现有 options）。

### 解析（`SecOcConfigLoader.cs`）

- 把 `LoadOptional`（L41-85）中「读文件后」的解析循环（entries → `Dictionary<uint, SecOcPduConfig>`，含 keyId 校验 / duplicate 校验 / mode 校验 / fail-loud）提取为新方法：
  ```csharp
  public static IReadOnlyDictionary<uint, SecOcPduConfig>? BuildFromEntries(
      IReadOnlyList<SecOcPduEntry> entries, string? storeDir = null, string? entropy = null)
  ```
  空列表抛 `InvalidOperationException`（保持 LoadOptional 语义）。`LoadOptional` 改为读文件后委托它。

### 按 handle 取配置（`src/PeakCan.Host.App/Services/SecOc/SecOcAppConfigStore.cs`）

- `LoadForConnectPath`（L49-56）签名改为 per-handle：
  ```csharp
  public static IReadOnlyDictionary<uint, SecOcPduConfig>? LoadForConnectPath(
      ushort handle, string? path = null, string? storeDir = null)
  {
      var entries = Load(path);                 // 文件缺失 → 空列表
      if (entries.Count == 0) return null;       // 未配置 → null（零回归）
      // 过滤：匹配 handle 或 Handle 空（全局兜底）
      var scoped = entries.Where(e => string.IsNullOrWhiteSpace(e.Handle)
          || ParseHandle(e.Handle) == handle).ToList();
      if (scoped.Count == 0) return null;        // 该通道无归属 PDU → 该通道不启用
      return SecOcConfigLoader.BuildFromEntries(scoped, storeDir);
  }
  ```
  `ParseHandle`：hex/dec 解析（复用 SecOcConfigLoader.ParseNumber 风格，非法值抛）。

### provider 签名与消费（本仓库）

- `AppHostBuilder.cs` L168-171：注册 `Func<ushort, IReadOnlyDictionary<uint, SecOcPduConfig>?>`：
  ```csharp
  builder.Services.AddSingleton<Func<ushort, IReadOnlyDictionary<uint, ...>?>>(
      _ => handle => SecOcAppConfigStore.LoadForConnectPath(handle));
  ```
- `ChannelConnectionCoordinator.cs`：
  - 字段 `_secOcPduProvider`（L57）类型改 `Func<ushort, IReadOnlyDictionary<uint, SecOcPduConfig>?>?`
  - `ConnectAllAsync` L112 `var secOcPdus = _secOcPduProvider?.Invoke();` 移到循环内（L117 `var handle = cfg.Channel.Handle;` 之后），改 `_secOcPduProvider?.Invoke(handle)`。L120 `if (secOcPdus is { Count: > 0 })` 逻辑不变（逐槽独立判断）。

### UI（`SecOcSettingsViewModel.cs` / `SecOcSettingsWindow.xaml`）

- `SecOcPduEntryModel`（L13-34）加 `[ObservableProperty] private string _handle = "";`
- `FromEntry`/`ToEntry` 带 `Handle`
- 窗口 PDU DataGrid 加「通道 Handle」列（占位提示 "0x51，空=全部"）

---

## 缺口 1b — HIL run 路径 per-channel

探索结论（facts）：
- `SecOcBlockReader`（suite 顶层 security 块探取）**在 host 仓库**（`src/PeakCan.Host.Infrastructure/Channel/SecOc/SecOcBlockReader.cs`），用 `JsonDocument` 探取不反序列化 TestSuite 全模型
- `SecOcBlock`（hil-core `HIL/Security/SecOcBlock.cs`）注释已写明已知限制："作用域为单通道，多通道需引入通道绑定字段（spec 未定义）"
- `HeadlessHostBuilder`：顶层块 → `secOcPdus` 闭包（L47-64）；**L52-56 显式拒绝 channels[] + 顶层块共存**（多通道上下文不透出 `ISecOcStatsSource/IPerCaseReset`，secocRejected 会静默禁用）；通道 0 = DI singleton（L86-93），通道 i>0 = `ComposeChannel(..., secOcPdus)`（L226-232）
- `SingleChannelContext` 已实现 `ISecOcStatsSource + IPerCaseReset`（每通道自带 `_secOcStats`）；`MultiChannelAssertionContext`（L15 接口列表）**没有**——这就是当初拒绝共存的根因
- `TestSuiteEngine` L139 fail-loud：context 透出 SecOcStats 但不实现 IPerCaseReset → 抛

方案（本仓库 host 侧为主；hil-core 仅可能一处 suite 反序列化选项确认）：
1. **suite channels[].security 探取**：`SecOcBlockReader` 加 `TryReadPerChannel(string? suitePath)` → `IReadOnlyDictionary<string, SecOcBlock>`（按 channel `name` 键，读 `channels[].security` 子对象，复用 `SecOcBlock` 类型，shape 与顶层块相同：`{ pdus: [...] }`）。channels[] 无 security 项 → 空字典。
2. **逐通道 PDU 组装**（`HeadlessHostBuilder`）：
   - L52-56 互斥校验改为：**两者可共存**，channel 级块优先，无 channel 级块的通道回落顶层块/`--secoc-config`（`ComposeChannel` 现有优先级已实现）
   - L86-93 通道 0 与 L226-232 通道 i 的 `secOcPdus` 改为按 `cfg.Name` 解析（`LoadFromBlock(perChannel[cfg.Name])`，块解析 + keyId fail-loud 在 Build 期一次性完成）
   - `MultiChannelAssertionContext` 实现 `ISecOcStatsSource + IPerCaseReset`：`SecOcStats` 透出**默认通道**的 stats（`_channels[_defaultChannelName].SecOcStats`），`ResetPerCase()` 遍历所有通道——解除 L48-51 注释所述限制，使 channels[] 路径的 secoc 表达式（默认通道）可用且通过 TestSuiteEngine L139 校验
3. **hil-core 侧**：确认 suite `channels[i]` 增加未知字段 `security` 是否会被 TestSuite 反序列化拒绝（host 的 `TryParseDeclaredChannels`/studio 解析是探取式则不受影响）。若反序列化选项 strict → hil-core 加 `ChannelConfig` 可选字段或放宽 unknown-field 处理。**标注：双仓联调时验证，优先不改 hil-core**（探取式解析全程 host 侧）。
4. **suite 文档**（README/demo suite 示例）加 channels[].security 用法。

> 范围边界：本次 HIL 侧覆盖"per-channel 组装 + 默认通道 secoc 表达式可用"。**多通道逐通道 secoc 表达式断言路由**（`secocRejected("bus-b", id)` 按 TargetChannel 查对应通道 stats）为后续 H2——涉及 `StepScopeFactory`/`SecOcFunctionRegistry` 的通道参数扩展，本次不做（在 plan 备注中记录）。

---

## 缺口 2 — 改配置后"需重连生效"提示

- `SecOcSettingsViewModel` ctor（L55-67）加可选 `Func<bool>? hasActiveConnection = null`
- `Save`（L127-139）成功后：
  ```csharp
  if (hasActiveConnection?.Invoke() == true)
      StatusMessage += "；已连接会话需断开重连后生效";
  ```
- `AppShellViewModel.OpenSecOcSettings`（L526-537）构造 VM 时传 `() => IsConnected`

---

## 缺口 3 — 工具栏启用状态可见

- `SecOcAppConfigStore` 加：
  ```csharp
  public enum SecOcConfigStatusKind { NotConfigured, Ready, Error }
  public sealed record SecOcConfigStatus(SecOcConfigStatusKind Kind, int PduCount, string? Error);
  public static SecOcConfigStatus GetStatus(string? path = null)
  ```
  实现：Load 返回空 → NotConfigured；BuildFromEntries 成功 → Ready(count)；抛异常 → Error(message)。
- `AppShellViewModel` 加 `[ObservableProperty] string _secOcStatusText = "SecOc: 未启用";` + `RefreshSecOcStatus()`：调用 `SecOcAppConfigStore.GetStatus()` 映射文本（未启用 / 就绪 N 条 / 配置错误）。刷新时机：ctor 末尾、`ConnectCoreAsync` 完成、`DisconnectAsync` 完成、`OpenSecOcSettings` 窗口关闭后。
- `AppShell.xaml` 工具栏状态区（L129 ConnectionState 旁）加 TextBlock 绑定 `SecOcStatusText`（灰/绿/红按 GetStatus 映射，可用 `Ok`/`Error`/`TextSecondary` 令牌）。

---

## 测试

**缺口 1a：**
- `SecOcAppConfigStoreTests`：改 `LoadForConnectPath` 现有 3 条测试签名；新增 handle 过滤测试（专属 handle → 取对应；全局兜底合并；该 handle 无 PDU → null；handle 非法 → 抛）
- `ChannelConnectionCoordinatorSecOcTests`：provider 变 per-handle → 现有 `secOcPduProvider: OnePdu` 改 `_ => OnePdu`；新增两 slot 不同 handle 各取各配置的测试
- `SecOcSettingsViewModelTests`：Handle 列 round-trip
- 向后兼容：无 handle 字段的旧 JSON → 每通道套用全部（现有测试回归）

**缺口 2：** `SecOcSettingsViewModelTests`：hasActiveConnection true/false 两分支的 StatusMessage

**缺口 3：** `SecOcAppConfigStoreTests` 三态；AppShellViewModel 刷新测试

**缺口 1b：** `SecOcBlockReader.TryReadPerChannel` 探取测试（channels[].security / 无 security / 畸形块 fail-loud）；HeadlessHostBuilder 逐通道绑定测试（两通道不同 PDU → 各自 Compose 的 SecOcChannel 用对应 key 验签；channel 级优先于顶层块；无 channel 级回落顶层块）；MultiChannelAssertionContext ISecOcStatsSource 透出默认通道 + IPerCaseReset 遍历

## 验证

- 单测全量：App / Infrastructure / Core 相关 filter
- 双仓库 build + host 全量回归（README 记录的 CI 双 pin lockstep）
- 手动烟测（AppShell）：配两通道不同 PDU → 连接 → 各通道 Trace 徽章正确；改配置保存 → 状态栏提示需重连；工具栏状态三态

## 备注

- HIL 侧依赖 sibling `peakcan-hil-core`（本机存在，ProjectReference）；改动需两个仓库同 push + CI lockstep 版本 pin（参照 README「双 pin」描述）
- 上轮 LOW 观察（Sign-only PDU 的 joiner/通道 seq 规则差异）顺带核对该次是否触及；不属本 plan 范围
