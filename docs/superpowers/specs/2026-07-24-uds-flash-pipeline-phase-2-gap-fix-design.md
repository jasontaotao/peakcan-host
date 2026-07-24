# UDS 刷写 pipeline Phase 2 缺漏修复设计

## Context

Phase 2 主体实现已完成（1561 tests pass），但 spec ↔ 实现之间存在 10 项缺漏。
本文档记录这些缺漏、修复方案，作为实现计划输入。

**根因模式**：UI 层先做了"看起来对"的控件（AddressingMode ComboBox、Verify 手填地址、
FlashDriver 多文件列表），但协议层/executor 没接上；部分步骤（PreCheck）被有意跳过但
没给出口。

---

## 缺漏清单（按严重性）

### 🔴 运行时必败 / 功能完全缺失

| # | 标签 | 缺漏 | spec 依据 |
|---|---|---|---|
| A | FunctionalId | `CommunicationControlAsync` → `SendFunctionalAsync` 需要 `FunctionalId`，但 `FlashProfile.ProgrammingCanId` 从未设置，运行时必抛 | §6.2 |
| B | AddressingMode | `FlashStep.AddressingMode` + UI ComboBox 存在，但 `PipelineExecutor.ExecuteStepAsync` 完全不读它 | §3.2 |
| C | PreCheck | executor PreCheck case 是空操作，且不在 AddableKinds 里；用户期望是 Routine 服务 | §6.3 标准流程 [1] |

### 🟡 功能可用但违背 spec

| # | 标签 | 缺漏 | spec 依据 |
|---|---|---|---|
| D | Verify 地址 | VerifyParams 暴露 StartAddress/EndAddress 手填，应从 Segment 自动获取 | §10.2 E2E |
| E | Verify CRC | ExpectedChecksum 应自动计算（Segment.Crc32），不应手填 | §4.3 |
| F | 删除 | FirmwareFiles/FlashDriver 只有 Add 没有 Remove | §3.1 布局 |
| G | FlashDriver 多文件 | `ObservableCollection<FlashDriver>` 支持多文件，应单文件 | §3.1 "Flash Driver" 单数 |

### 🟢 UI 瑕疵

| # | 标签 | 缺漏 |
|---|---|---|
| H | 对齐 | Flash Driver 栏多塞了 "Apply Multi-Level Template" 按钮，破坏对称 |
| I | Up/Down | `SelectedStep` 的 `[NotifyCanExecuteChangedFor]` 漏了 MoveUp/MoveDown |
| J | PreCheck 列表 | 不在 AddableKinds 下拉栏 |

---

## 修复方案

### 1. 数据模型

#### 1.1 FlashDriver 单文件

```csharp
// FlashProfile.cs
public FlashDriver? FlashDriver { get; set; }  // 改前：ObservableCollection<FlashDriver>
```

- `AddFlashDriver` 命令改为替换语义
- UI：ListBox → TextBlock（有值显示 Path，空显示 "(no driver loaded)"）

#### 1.2 PreCheck 参数组

```csharp
// FlashStep.cs 新增
public sealed record PreCheckParams
{
    public ushort RoutineId { get; set; } = 0x0000;
}
```

- 构造函数 `case FlashStepKind.PreCheck: PreCheck = new PreCheckParams(); break;`
- `AddableKinds` 加回 `FlashStepKind.PreCheck`

#### 1.3 VerifyParams 改为 Segment 引用

```csharp
// FlashStep.cs
public sealed record VerifyParams
{
    public ChecksumAlgorithm Algorithm { get; set; } = ChecksumAlgorithm.Crc32;
    public int SegmentIndex { get; set; }  // 引用 Segment → 自动算地址+CRC
    // ExpectedChecksum/StartAddress/EndAddress 改为只读展示（从 Segment 计算）
}
```

### 2. 协议层 + Executor

#### 2.1 FunctionalId 配置

- `FlashProfile.ProgrammingCanId` 默认值加 `FunctionalId = 0x7DF`
- UI：Programming CAN-ID 行加 FunctionalId 输入框（可选）

#### 2.2 AddressingMode 接线（0x28 专用）

- 0x28 CommunicationControl 始终用 `SendFunctionalAsync`（已有）
- 其他步骤始终物理寻址
- AddressingMode ComboBox 仅对 0x28 可选，其他步骤灰掉

#### 2.3 PreCheck 执行

```csharp
case FlashStepKind.PreCheck:
    var preCheck = step.PreCheck ?? throw new InvalidOperationException("PreCheck params missing");
    await client.RoutineControlAsync(StartRoutine, preCheck.RoutineId, data: null, ct).ConfigureAwait(false);
    break;
```

### 3. UI

#### 3.1 Firmware Files — 加删除

- `SelectedFirmwareFile` 属性 + `RemoveFirmwareFileCommand`
- ListBox 加 SelectedItem 绑定 + `[- Remove]` 按钮

#### 3.2 Flash Driver — 单文件 + 删除

- `RemoveFlashDriverCommand`（`FlashDriver = null`）
- UI：TextBlock + `[+ Add] [- Remove]`

#### 3.3 对齐

- "Apply Multi-Level Template" 按钮移出 Flash Driver 列 → Step 按钮行

#### 3.4 Up/Down 启用

```csharp
// SelectedStep 属性补
[NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
[NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
```

#### 3.5 Verify 属性面板

- Algorithm（ComboBox）+ Segment（ComboBox 引用）+ ExpectedCRC/地址（只读）

---

## 涉及文件

| 文件 | 变更 |
|---|---|
| `FlashProfile.cs` | FlashDriver 单文件 + FunctionalId 默认值 |
| `FlashStep.cs` | PreCheckParams + VerifyParams 改 Segment 引用 |
| `FlashPanelViewModel.cs` | Remove 命令 + SelectedFirmwareFile + AddressingMode 灰掉 + NotifyCanExecuteChangedFor |
| `PipelineExecutor.cs` | PreCheck case 执行 RoutineControl |
| `UdsWindow.xaml` | 属性面板调整 + 对齐 + Verify 只读字段 |
| `FlashStepSnapshot.cs` | PreCheck 快照 + Verify 快照调整 |

## 测试

| 测试 | 验证 |
|---|---|
| `FlashDriver_SingleFile_ReplacesPrevious` | Add 两次 → 只有最后一个 |
| `RemoveFirmwareFile_Selected_Removes` | 选中删除后集合减一 |
| `RemoveFlashDriver_Sets_Null` | Remove 后 FlashDriver 为 null |
| `PreCheckStep_RoutineId_Sends_0x31` | PreCheck 调 RoutineControl |
| `PreCheck_In_AddableKinds` | 下拉列表包含 PreCheck |
| `CommunicationControl_Uses_FunctionalId` | 0x28 通过 SendFunctionalAsync |
| `AddressingMode_Functional_Only_0x28` | 其他步骤灰掉 |
| `Verify_SegmentIndex_AutoCalc` | 选中 Segment 后地址+CRC 自动填充 |
| `SelectedStep_Notifies_MoveUpCanExecute` | 选中后 Up 按钮启用 |
| `Profile_SaveLoad_Includes_FunctionalId` | 序列化保留 FunctionalId |

## Out of Scope

- ODX 刷写配置桥接（IFlashConfigurationProvider）— Phase 2 可选
- 步骤拖拽排序 — 超出 Phase 2
- 每步通用 AddressingMode 接线 — 仅 0x28 有意义
