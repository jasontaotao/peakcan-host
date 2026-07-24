# UDS Flash Pipeline Phase 2 剩余缺漏修复设计

## Context

Phase 2 主体 + 预编程检查/依赖性检查已完成。本文档记录剩余 5 项缺漏的修复方案。

---

## 缺漏清单

| # | 标签 | 缺漏 | 严重性 |
|---|---|---|---|
| Q2 | Flash driver 文件类型 | 当前 .dll/.bin，应改 .hex/.s19 | 明确修复 |
| Q4 | CAN-ID 无 UI 编辑 | 定死 0x714/0x760/0x7DF | 缺功能 |
| Q5 | Flash driver 下载到 ECU 未实现 | executor 无 driver 处理 | 缺功能 |
| M1 | flat/grouped 参数不同步 | executor 读 flat，UI 绑定 grouped | 潜在 bug |
| M2 | Verify Segment ComboBox 绑定错位 | ItemsSource=文件，Index=Segment | 潜在 bug |

---

## 修复方案

### 1. FlashDriverDownload 步骤（Q5）

**机制**：独立步骤，下载到 RAM，ECU 自动识别执行（TransferExit 后无需额外启动）。

```csharp
// FlashStepKind 新增
FlashDriverDownload,

// 参数 — StartAddress 从 driver 文件解析自动获取
public sealed record FlashDriverDownloadParams
{
    public uint StartAddress { get; set; }  // 从 FlashDriver.Segments[0].StartAddress 自动填充
}

// Executor case
case FlashStepKind.FlashDriverDownload:
    var driver = CurrentProfile.FlashDriver ?? throw new InvalidOperationException("No flash driver loaded");
    var ddParams = step.FlashDriverDownload ?? throw new InvalidOperationException("FlashDriverDownload params missing");
    await ExecuteDownloadAsync(client, ddParams.StartAddress, driver.Data, progress, stepIndex, total, ct);
    break;
```

**文件类型**：`.hex/.s19`（带地址信息，解析后自动获取起始地址，无需手动填）。

**默认模板位置**：SecurityAccess 之后、Erase 之前。

### 2. CAN-ID UI 编辑（Q4）

**方案**：内联编辑。

```
Programming CAN-ID: Req [0x714] / Resp [0x760] / Func [0x7DF]    [编辑]
```

- 默认显示模式：只读文本 + "编辑"按钮
- 编辑模式：三个 TextBox（Req/Resp/Func）+ "保存"/"取消"
- 绑定到 `FlashProfile.ProgrammingCanId`

### 3. 文件类型修复（Q2）

`AddFlashDriver` 过滤器改为：
```csharp
"Flash driver (*.hex;*.s19)|*.hex;*.s19|Intel HEX (*.hex)|*.hex|Motorola S19 (*.s19)|*.s19|All files|*.*"
```

### 4. flat/grouped 参数同步（M1）

`ToSnapshot()` 中从 grouped params 反向同步 flat fields：
```csharp
// SecurityAccess: UI 绑定 grouped → snapshot 读 grouped
SecurityAccess = step.SecurityAccess is { } sa ? new SecurityAccessSnapshot(sa.Level, sa.Mode, sa.ManualKeyHex, sa.DllPath) : null,
```

executor 改为读 grouped snapshot 字段（而非 flat fields）。

### 5. Verify Segment ComboBox 绑定（M2）

VM 暴露扁平化 segment 列表：
```csharp
public IReadOnlyList<Segment> AllSegments =>
    CurrentProfile.FirmwareFiles.SelectMany(f => f.Segments).ToList();
```

XAML ComboBox 绑定到 `AllSegments`，显示 "0x{StartAddress:X8} - 0x{EndAddress:X8} ({Length} bytes)"。

---

## 涉及文件

| 文件 | 变更 |
|---|---|
| `FlashStepKind.cs` | 新增 FlashDriverDownload |
| `FlashStep.cs` | FlashDriverDownloadParams + 属性 + 构造初始化 |
| `FlashStepSnapshot.cs` | FlashDriverDownloadSnapshot |
| `PipelineExecutor.cs` | FlashDriverDownload case + ExecuteDownloadAsync helper |
| `FlashPanelViewModel.cs` | AddableKinds + ToSnapshot + AllSegments + 文件过滤器 |
| `UdsWindow.xaml` | CAN-ID 编辑 + FlashDriverDownload 面板 + Verify ComboBox 绑定 |
| `FlashProfile.cs` | CreateDefault 加 FlashDriverDownload 步骤 |

## Out of Scope

- OEM 安全密钥 DLL 加载反馈（Q3）— 待用户确认需求
- Keep-alive catch 范围（M3）— 低风险暂缓
