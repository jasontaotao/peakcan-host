# ODX ↔ SecurityAccess 桥接 + DLL seed 长度适配

## Context

peakcan-host 的 UDS Flashing 流水线中，SecurityAccess (0x27) 步骤的参数（security level、seed 长度）靠操作员手动配置。这些参数实际定义在 ODX 文件中，但当前 ODX 解析器只提取 DID/Routine/DTC，**完全不读 0x27 服务的结构**。

用户 DLL（AES-CMAC 算法）硬性要求 seed = 16 字节。如果 ECU 实际 seed 长度不是 16 字节，0x27 握手拿到负响应。

目标：ODX 导入后自动推导 SecurityAccess 参数（level、seed 长度），消除手动猜配置。

## 与既有 spec 的对齐

Spec `2026-07-24-uds-flash-pipeline-phase-2-design.md` §8 已规划了 `IFlashConfigurationProvider` 接口。本方案 **继承该接口、不做任何修改**，只填充 spec 未定义的 `SecurityAccessConfig` 形状，并补充 spec 未覆盖的 seed 长度维度。

| 来源 | 本方案态度 |
|---|---|
| spec §8.3 `IFlashConfigurationProvider` 接口三方法 | **直接复用**，不改名、不加方法 |
| spec §8.2 三个自动填充目标（RoutineId / SecurityAccess level / ChecksumAlgorithm） | SecurityAccess level 本期做；RoutineId + ChecksumAlgorithm 留 `return null` 默认实现 |
| spec §8.3 挂载点 "ODX 导入时注册实现" | 在 `OdxImportService.ImportAsync` 完成后更新 provider 状态 |
| spec §11 "Out of Scope" 标记 | 本方案是 spec 标记为可选后的**首次实现** |
| spec 未定义 `SecurityAccessConfig` 形状 | 本方案 **新增设计**（byte Level + int? SeedLength） |
| spec 未提及 seed/key 长度 ODX 驱动 | 本方案 **新增维度** |

## 方案概览

```
Layer 1 (Core.ODX):  新增 SecurityAccessExtractor → 从 ODX 提取 0x27 服务的 Level + SeedLength
Layer 2 (Core.Flash): SecurityAccessParams 增加 SeedLength 字段（UI 可编辑，ODX 值为默认值）
Layer 3 (App.Flash):  IFlashConfigurationProvider 实现 + 可变 wrapper + 智能填充到 Flash 面板
DLL 侧 (独立工程):    GenerateKey wrapper 内部做 seed padding/truncation → 适配任意长度 seed
```

---

## Layer 1：ODX 解析扩展

### 1.1 新增 `SecurityAccessConfig`

**文件**: `src/PeakCan.Host.Core/Uds/Odx/SecurityAccessConfig.cs`（新建）

```csharp
/// <summary>
/// ODX-derived 0x27 SecurityAccess parameters. Returned by
/// <see cref="SecurityAccessExtractor"/>. Null if ODX has no 0x27 definition.
/// 形状对齐 spec §8.3 IFlashConfigurationProvider.GetSecurityAccessConfig() 的返回类型。
/// </summary>
public sealed record SecurityAccessConfig(
    /// <summary>Security level (0x27 sub-function). E.g. 0x01, 0x11.</summary>
    byte Level,

    /// <summary>Seed byte length from ODX POS-RESPONSE BIT-LENGTH. Null if ODX omits the structure.</summary>
    int? SeedLength);
```

### 1.2 新增 `SecurityAccessExtractor`

**文件**: `src/PeakCan.Host.Core/Uds/Odx/SecurityAccessExtractor.cs`（新建）

无依赖（纯函数式：XDocument → SecurityAccessConfig?），直接 new 使用，不需要 DI。

复用 `RequestBasedMappers` 的 ReadServiceId + BIT-LENGTH 读取模式：

```
输入: XDocument + XNamespace
步骤:
  1. 找 SERVICE-ID == 0x27 的 REQUEST
  2. 读 PARAM SEMANTIC="SUBFUNCTION" → Level（取最小奇数 subfunction，
     因为 0x27 的奇数 subfunction = RequestSeed，偶数 = SendKey）
  3. 通过 DIAG-SERVICE → POS-RESPONSE-REF → POS-RESPONSE → PARAM SEMANTIC="DATA"
     读取 BIT-LENGTH / 8 → SeedLength（POS-RESPONSE 不存在则返回 null）
  4. 如果 ODX 中 0x27 的 POS-RESPONSE 通过内联而非 POS-RESPONSE-REF 间接引用，
     也需兼容（实现时注意 M1）
输出: SecurityAccessConfig? (null if no 0x27)
```

**本期范围限制（对齐 H3）**：只提取一个 Level（最小奇数 subfunction）。多级解锁（level=1 + level=3）场景下，level=3 的步骤需要操作员手动配置。这在 UI 上用"🔗 ODX"标记哪些步骤被 ODX 自动填充、哪些需要手动。

### 1.3 `OdxImportService.ImportAsync` 完成后更新 provider

**文件**: `src/PeakCan.Host.App/Services/OdxImportService.cs`（修改 — **构造函数新增 `FlashConfigurationService` 参数**）

不替换 DI 单例（避免 H2 的 service locator 反模式），而是用一个 **可变 wrapper class** 持有当前 config：

**新建文件**: `src/PeakCan.Host.App/Services/FlashConfigurationService.cs`

（放 Services/ 而非 ViewModels/，因为它是 App 层服务不是 ViewModel）

```csharp
public sealed class FlashConfigurationService : IFlashConfigurationProvider
{
    private SecurityAccessConfig? _securityAccess;
    private ChecksumAlgorithm _checksum = ChecksumAlgorithm.Crc32;

    /// <summary>Raised after UpdateFromOdx() so subscribers can refresh.</summary>
    public event Action? ConfigUpdated;

    public void UpdateFromOdx(SecurityAccessConfig? config)
    {
        _securityAccess = config;
        ConfigUpdated?.Invoke();  // 通知订阅者（解决 N3）
    }

    public ushort? GetEraseRoutineId(uint startAddress, uint size) => null;  // Phase 2 可选
    public SecurityAccessConfig? GetSecurityAccessConfig() => _securityAccess;
    public ChecksumAlgorithm GetChecksumAlgorithm() => _checksum;
}
```

`FlashConfigurationService` 注册为 DI 单例（普通 Singleton，不是替换式）。`OdxImportService` 通过构造注入持有同一个实例，import 完成后调用 `_flashConfig.UpdateFromOdx(config)`。

**第二次导入**：直接覆盖 `_security_access` 字段。因为单例是同一个引用，所有注入 `IFlashConfigurationProvider` 的 ViewModel 自动看到新值。

**现有测试影响**：`OdxImportService` 构造函数加参数是 breaking change。需要在文件清单标注，并更新 `OdxImportServiceTests` 的构造。

---

## Layer 2：Flash 参数扩展

### 2.1 `SecurityAccessParams` + SeedLength（可编辑，非只读）

**文件**: `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashStep.cs`

```csharp
public sealed record SecurityAccessParams
{
    public byte Level { get; set; } = 0x01;
    public SecurityAccessMode Mode { get; set; } = SecurityAccessMode.Manual;
    public string ManualKeyHex { get; set; } = "";
    public string DllPath { get; set; } = "";
    /// <summary>
    /// Seed byte length. null = 自动（使用 ECU 返回的 seed 原长）。
    /// 非 null = ODX 推导值或操作员手动指定的值，DLL wrapper 会做 padding/truncation。
    /// </summary>
    public int? SeedLength { get; set; } = null;
}
```

同步加平坦字段 `_seedLength` + `SetSeedLength(int?)` helper。

### 2.2 `SecurityAccessSnapshot` + SeedLength

**文件**: `src/PeakCan.Host.Core/Uds/FlashPipeline/FlashStepSnapshot.cs`

```csharp
public sealed record SecurityAccessSnapshot(
    byte Level, SecurityAccessMode Mode, string ManualKeyHex, string DllPath,
    int? SeedLength);   // 新增
```

### 2.3 `FlashPanelViewModel.ToSnapshot` 传递 SeedLength

**文件**: `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashPanelViewModel.cs`

ToSnapshot 映射加 `SeedLength = sa.SeedLength`。

### 2.4 UI：可编辑 + ODX 标记（解决 N2）

**文件**: `src/PeakCan.Host.App/Windows/UdsWindow.xaml`

SecurityAccess 属性面板加两行标记（Level 和 SeedLength 各自独立标记）：

```xml
<!-- Level 行 -->
<StackPanel Orientation="Horizontal">
    <TextBlock Text="Level:"/>
    <TextBox Text="{Binding Flash.SelectedStep.SecurityAccess.Level, Converter={StaticResource HexConverter}}"/>
    <TextBlock Text="🔗 ODX" Visibility="{Binding Flash.SelectedStep.IsSecurityLevelFromOdx, Converter={StaticResource BoolToVisibility}}"/>
</StackPanel>

<!-- Seed Length 行 -->
<StackPanel Orientation="Horizontal">
    <TextBlock Text="Seed Length:"/>
    <TextBox Text="{Binding Flash.SelectedStep.SecurityAccess.SeedLength, TargetNullValue='auto'}"/>
    <TextBlock Text="🔗 ODX" Visibility="{Binding Flash.SelectedStep.IsSeedLengthFromOdx, Converter={StaticResource BoolToVisibility}}"/>
</StackPanel>
```

`IsSecurityLevelFromOdx` 和 `IsSeedLengthFromOdx` 是 `FlashStep` 上的两个独立 bool 字段，在 ODX 自动填充时设 true，用户手动编辑对应值后设 false（通过 SetSeedLength / SetSecurityAccessLevel 方法清除 — 见 N4 方案 a）。

---

## Layer 3：IFlashConfigurationProvider 实现 + 智能填充

### 3.1 接口（直接复用 spec §8.3）

**文件**: `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/IFlashConfigurationProvider.cs`（新建）

```csharp
public interface IFlashConfigurationProvider
{
    ushort? GetEraseRoutineId(uint startAddress, uint size);
    SecurityAccessConfig? GetSecurityAccessConfig();
    ChecksumAlgorithm GetChecksumAlgorithm();
}
```

### 3.2 实现类（可变 wrapper）

**文件**: `src/PeakCan.Host.App/Services/FlashConfigurationService.cs`（新建）

见 §1.3 的设计。注册为 DI 单例。

### 3.3 DI 注册

**文件**: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs`

```csharp
services.AddSingleton<FlashConfigurationService>();
services.AddSingleton<IFlashConfigurationProvider>(sp => sp.GetRequiredService<FlashConfigurationService>());
```

`OdxImportService` 构造注入 `FlashConfigurationService`（具体类，不是接口——因为需要调用 `UpdateFromOdx` 方法）。

### 3.4 FlashPanelViewModel 注入 + 智能填充（解决 C2 + N3 + N4）

**文件**: `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashPanelViewModel.cs`

构造注入 `IFlashConfigurationProvider`。

**ODX 导入感知机制（解决 N3）**：订阅 `FlashConfigurationService.ConfigUpdated` 事件：

```csharp
// 构造时
_flashConfig.ConfigUpdated += ApplyOdxDefaultsIfUnset;

// 析构 / Dispose 时取消订阅（避免内存泄漏）
_flashConfig.ConfigUpdated -= ApplyOdxDefaultsIfUnset;
```

**填充策略（解决 C2 覆盖问题）**：

```csharp
void ApplyOdxDefaultsIfUnset()
{
    var config = _flashConfigProvider?.GetSecurityAccessConfig();
    if (config is null) return;

    foreach (var step in Steps.Where(s => s.Kind == FlashStepKind.SecurityAccess))
    {
        // 只在步骤仍为默认值时填充（不覆盖用户已修改 / profile 加载的值）
        if (step.SecurityAccess?.Level == 0x01)
        {
            step.SetSecurityAccessLevel(config.Level);  // 内部设 IsSecurityLevelFromOdx = true
        }
        if (step.SecurityAccess?.SeedLength == null)
        {
            step.SetSeedLength(config.SeedLength);      // 内部设 IsSeedLengthFromOdx = true
        }
    }
}
```

**Profile 加载时（解决 H4）**：`LoadProfileAsync` 反序列化完成后，也调用 `ApplyOdxDefaultsIfUnset()`。逻辑一致：profile 值 == 默认值时 ODX 才有机会填充。

**用户手动编辑后清除 ODX 标记（解决 N4 + R1，方案 c — computed property）**：

`SecurityAccessParams` 是 record，自动属性 setter 不能插入逻辑。XAML 绑定直接走 auto-setter，不走 wrapper 方法。
因此采用 **computed property** 方案：ODX 标记不追踪"是否被编辑"，而是比较"当前值 == ODX 推导值"。

```csharp
// FlashStep.cs
private int? _odxDerivedSeedLength;   // ODX 推导的 seed 长度（可能 null）
private byte _odxDerivedLevel;        // ODX 推导的 level

/// <summary>ODX 标记：当前 SeedLength 仍等于 ODX 推导值时显示。用户改了自动消失。</summary>
public bool IsSeedLengthFromOdx => SeedLength == _odxDerivedSeedLength;

/// <summary>ODX 标记：当前 Level 仍等于 ODX 推导值时显示。用户改了自动消失。</summary>
public bool IsSecurityLevelFromOdx => Level == _odxDerivedLevel;
```

ODX 填充时保存推导值：

```csharp
// ApplyOdxDefaultsIfUnset 中
if (step.SecurityAccess?.Level == 0x01)
{
    step.SetSecurityAccessLevel(config.Level);
    step._odxDerivedLevel = config.Level;   // 记录 ODX 推导值
}
if (step.SecurityAccess?.SeedLength == null)
{
    step.SetSeedLength(config.SeedLength);
    step._odxDerivedSeedLength = config.SeedLength;  // 记录 ODX 推导值
}
```

**优势**：
- XAML 绑定不需要改（仍绑定 record 自动属性）
- 不需要事件 / behavior / LostFocus 机制
- 用户编辑值 → computed property 自动 false → "🔗 ODX" 标记消失
- 用户改回 ODX 值 → computed property 自动 true → 标记重新出现

---

## DLL 侧适配（KeyGenDll_GenerateKeyEx 工程）

### 问题

`GenerateKeyEx` 硬性要求 `iSeedArraySize == 16`，否则返回 `KGRE_BufferToSmall`。

### 方案

**文件**: `GenerateKeyExImpl.cpp`

1. 放宽 `GenerateKeyEx` 自身的限制：接受任意长度 seed（去掉 `iSeedArraySize != 16` 检查，或改为 `> 16` 报错）。
2. `GenerateKey` wrapper 内部做 seed 适配：

```cpp
// seed 适配：任意长度 → 16 字节（AES-CMAC 密钥长度固定 16）
unsigned char seed16[16] = {0};
if (seedLen >= 16) {
    memcpy(seed16, seed, 16);  // 截断：取前 16 字节
} else {
    memcpy(seed16, seed, seedLen);  // 右补零：seed 字节在前，零填充在后
}
```

**⚠️ 用户验证项（解决 H5）**：
- 截断方向（取前 16 字节 vs 取后 16 字节）和补零方向（左补零 vs 右补零）取决于目标 ECU 的 key 生成算法对 seed 字节序的假设。
- 当前默认：**取前 16 字节 + 右补零**。如果 ECU 使用不同策略，需要调整。
- 建议：在 UI SeedLength 显示框旁边加 tooltip："Seed ≠ 16 字节时，DLL 会截断/补零到 16 字节。确认目标 ECU 的 seed 字节序。"

---

## 关键文件清单

| 文件 | 动作 | Layer |
|---|---|---|
| `src/PeakCan.Host.Core/Uds/Odx/SecurityAccessConfig.cs` | **新建** | L1 |
| `src/PeakCan.Host.Core/Uds/Odx/SecurityAccessExtractor.cs` | **新建** | L1 |
| `src/PeakCan.Host.App/Services/OdxImportService.cs` | 修改：**构造函数新增 `FlashConfigurationService` 参数** + import 完成后调 UpdateFromOdx | L1 |
| `src/PeakCan.Host.App/Services/FlashConfigurationService.cs` | **新建**（IFlashConfigurationProvider 实现 + 可变 wrapper + ConfigUpdated 事件） | L1+L3 |
| `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashStep.cs` | 修改：SecurityAccessParams + SeedLength + Odx 标记字段 | L2 |
| `src/PeakCan.Host.Core/Uds/FlashPipeline/FlashStepSnapshot.cs` | 修改：+SeedLength | L2 |
| `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashPanelViewModel.cs` | 修改：ToSnapshot + 注入 IFlashConfigurationProvider + 订阅 ConfigUpdated 事件 + ApplyOdxDefaultsIfUnset | L2+L3 |
| `src/PeakCan.Host.App/Windows/UdsWindow.xaml` | 修改：+SeedLength 编辑 + Level/SeedLength 各自独立 ODX 标记 | L2 |
| `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/IFlashConfigurationProvider.cs` | **新建**（spec §8.3 接口） | L3 |
| `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` | 修改：DI 注册 FlashConfigurationService | L3 |
| `KeyGenDll_GenerateKeyEx\GenerateKeyExImpl.cpp` | 修改：变长 seed 适配 | DLL |

**复用**（不修改）：`RequestBasedMappers`（ReadServiceId / BIT-LENGTH 链）、`OdxParser`（XDocument/NS 输入）、`OdxDocument` / `DiagLayer` / `DiagService` 模型。

---

## 验证

### 单元测试

1. `SecurityAccessExtractorTests`：synthetic ODX（0x27 + SUBFUNCTION + POS-RESPONSE BIT-LENGTH=128）→ Level 和 SeedLength 正确。
2. `SecurityAccessExtractorTests`：无 0x27 → null。
3. `SecurityAccessExtractorTests`：有 0x27 但无 POS-RESPONSE → Level + SeedLength=null。
4. `FlashStepTests`：SetSecurityAccessLevel + SetSeedLength 更新 grouped params 和 flat 字段。
5. `FlashPanelViewModelTests`：mock IFlashConfigurationProvider → Steps 自动填充 Level/SeedLength（仅默认值步骤）。
6. `FlashPanelViewModelTests`：非默认值步骤不被 ODX 覆盖。
7. `FlashConfigurationServiceTests`：UpdateFromOdx 后 GetSecurityAccessConfig 返回新值；第二次 import 覆盖旧值。
8. DLL：4/8/16/32 字节 seed 验证 padding/truncation。

### 集成

1. 导入含 0x27 的测试 ODX → `FlashConfigurationService.GetSecurityAccessConfig()` 非 null。
2. 导入后打开 Flashing tab → SecurityAccess 步骤 Level + SeedLength 自动显示 + "🔗 ODX" 标记。
3. 用户手动改 SeedLength → "🔗 ODX" 标记消失。
4. 加载已保存 profile（Level=0x11）→ ODX 导入后不覆盖 Level（因为非默认值）。
5. `dumpbin /exports SeednKey.dll` 确认 `GenerateKey` 导出存在。
6. 16 字节 seed ECU 仿真 → DLL mode 0x27 握手正响应。

---

## 评审修正记录

| 编号 | 问题 | 修正 |
|---|---|---|
| N1 | XAML 文件路径错误 | → `src/PeakCan.Host.App/Windows/UdsWindow.xaml` |
| N2 | IsOdxDerived vs 两个独立 bool 不一致 | → Level/SeedLength 各自独立标记 |
| N3 | OnOdxImported 触发机制未定 | → `FlashConfigurationService.ConfigUpdated` 事件 |
| N4 | record setter 不能插入逻辑 | → 见 R1（computed property 替代 wrapper 方法） |
| N5 | FlashConfigurationService 路径语义 | → `Services/` 目录 |
| N6 | OdxImportService 构造函数变更未标注 | → 文件清单标注 + 更新测试 |
| R1 | XAML 绑定走 auto-setter，wrapper 无法清除 ODX 标记 | → **computed property**：`IsSeedLengthFromOdx => SeedLength == _odxDerivedSeedLength`，不需改绑定 |
| R2 | §3.2 文件路径与 §1.3 不一致 | → §3.2 改为 `Services/FlashConfigurationService.cs` |

## 不在本期范围

- ECU-MEMORY/FLASH descriptor → Erase RoutineId 映射（spec §8.2 目标 #1，留 null 默认）
- ChecksumAlgorithm 从 ODX 推导（spec §8.2 目标 #3，留 Crc32 默认）
- `SecurityAccessMode.Auto` DI 注册（Phase 3）
- COMPARAM-SPEC 通讯参数解析
- 多级 SecurityAccess（level=1 + level=3）的 ODX 自动填充（本期只支持单级，多级需手动配置）
- DLL padding/truncation 策略可配置化（当前固定为"取前 16 + 右补零"，需用户根据目标 ECU 验证）
