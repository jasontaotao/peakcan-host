# UDS Flash Pipeline Phase 2 缺漏修复实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复 Phase 2 spec ↔ 实现之间的 10 项缺漏（FunctionalId/AddressingMode 接线/PreCheck/Verify 自动算/删除/对齐/Up-Down）

**Architecture:** 数据模型层（FlashProfile/FlashStep/Snapshot）→ 协议层（executor PreCheck case）→ UI 层（属性面板 + 删除命令 + AddressingMode 灰掉）。AddressingMode 做成 0x28 专用（其他步骤灰掉），FunctionalId 默认 0x7DF。

**Tech Stack:** C# / WPF .NET 10 / CommunityToolkit.Mvvm / xUnit

## Global Constraints

- Core 不引用 App（依赖方向）
- 现有 1561 tests 必须保持通过
- FlashDriver 单文件化（`ObservableCollection<FlashDriver>` → `FlashDriver?`）
- Verify 地址+CRC 从 Segment 自动算（UI 只读展示）
- PreCheck = RoutineControl(StartRoutine, routineId)，确认返回非空
- AddressingMode ComboBox 仅 0x28 可选，其他步骤 `IsEnabled=false`
- PKM capture 不触发（CLAUDE.md 硬约束：中间任务不触发）

---

## 文件结构

| 文件 | 变更类型 | 责任 |
|---|---|---|
| `src/PeakCan.Host.Core/Uds/FlashPipeline/FlashProfile.cs` | 修改 | FlashDriver 单文件 + FunctionalId 默认值 |
| `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashStep.cs` | 修改 | PreCheckParams + VerifyParams 改 Segment 引用 |
| `src/PeakCan.Host.Core/Uds/FlashPipeline/FlashStepSnapshot.cs` | 修改 | PreCheckSnapshot + VerifySnapshot 调整 |
| `src/PeakCan.Host.Core/Uds/FlashPipeline/PipelineExecutor.cs` | 修改 | PreCheck case 执行 RoutineControl |
| `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashPanelViewModel.cs` | 修改 | Remove 命令 + SelectedFirmwareFile + AddressingMode 灰掉 + NotifyCanExecuteChangedFor |
| `src/PeakCan.Host.App/Windows/UdsWindow.xaml` | 修改 | 属性面板调整 + 对齐 + Verify 只读字段 + PreCheck 组 |
| `tests/PeakCan.Host.Core.Tests/Uds/FlashPipeline/*.cs` | 新增 | PreCheck / FunctionalId / Verify 测试 |
| `tests/PeakCan.Host.App.Tests/ViewModels/Uds/FlashPipeline/*.cs` | 新增 | Remove 命令 / Up-Down / AddressingMode 测试 |

---

## Task 1: FlashProfile — FlashDriver 单文件 + FunctionalId

**Files:**
- Modify: `src/PeakCan.Host.Core/Uds/FlashPipeline/FlashProfile.cs`

**Interfaces:**
- Produces: `FlashProfile.FlashDriver` (single, nullable), `CanIdConfig.FunctionalId = 0x7DF` default

- [ ] **Step 1: 改 FlashDriver 为单文件**

将 `FlashProfile.cs` 的 `FlashDrivers` 属性改为单个：

```csharp
/// <summary>
/// Phase 2: The single loaded flash driver. A flash driver is a small routine
/// (DLL or binary) downloaded to ECU RAM before the main firmware, executed to
/// perform erase/write. Single (not a collection) — an ECU has exactly one
/// flashing routine active at a time; loading a new one replaces the previous.
/// </summary>
public FlashDriver? FlashDriver { get; set; }
```

- [ ] **Step 2: ProgrammingCanId 加 FunctionalId 默认值**

```csharp
public CanIdConfig ProgrammingCanId { get; set; } = new()
{
    RequestId = 0x714,
    ResponseId = 0x760,
    FunctionalId = 0x7DF,  // OBD-II broadcast for 0x28
};
```

- [ ] **Step 3: CreateDefault() 同步更新**

```csharp
ProgrammingCanId = new CanIdConfig
{
    RequestId = 0x714,
    ResponseId = 0x760,
    FunctionalId = 0x7DF,
},
```

- [ ] **Step 4: 编译验证**

Run: `dotnet build src/PeakCan.Host.Core/ src/PeakCan.Host.App/`
Expected: 0 errors（`FlashDrivers` 引用在 tests 目录不存在，前面 grep 已确认）

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Core/Uds/FlashPipeline/FlashProfile.cs
git commit -m "refactor(uds-flash): FlashDriver 单文件化 + ProgrammingCanId 默认 FunctionalId=0x7DF"
```

---

## Task 2: FlashStep — PreCheckParams + VerifyParams Segment 引用

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashStep.cs`

**Interfaces:**
- Produces: `PreCheckParams`, `VerifyParams.SegmentIndex`, `FlashStep.PreCheck` property

- [ ] **Step 1: 新增 PreCheckParams 记录**

在 FlashStep.cs 顶部（SecurityAccessParams 前）加：

```csharp
/// <summary>PreCheck (预编程校验) parameters. Only meaningful when Kind == PreCheck.</summary>
public sealed record PreCheckParams
{
    public ushort RoutineId { get; set; } = 0x0000;  // 操作员填写预检查 routine
}
```

- [ ] **Step 2: VerifyParams 改为 Segment 引用**

```csharp
/// <summary>Verify (0x31 RoutineControl for checksum) parameters.</summary>
public sealed record VerifyParams
{
    public ChecksumAlgorithm Algorithm { get; set; } = ChecksumAlgorithm.Crc32;
    public int SegmentIndex { get; set; }  // 引用 FirmwareFiles 扁平化 Segment 列表
    // ExpectedChecksum/StartAddress/EndAddress 从 Segment 自动算，UI 只读展示
}
```

- [ ] **Step 3: FlashStep 加 PreCheck 属性 + 构造函数初始化**

在属性声明区（SecurityAccess 旁）加：

```csharp
/// <summary>PreCheck parameters. Non-null only when Kind == PreCheck.</summary>
public PreCheckParams? PreCheck { get; private set; }
```

在构造函数 switch 里加：

```csharp
case FlashStepKind.PreCheck:
    PreCheck = new PreCheckParams();
    break;
```

- [ ] **Step 4: 编译验证**

Run: `dotnet build src/PeakCan.Host.App/`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashStep.cs
git commit -m "feat(uds-flash): PreCheckParams + VerifyParams 改 Segment 引用"
```

---

## Task 3: FlashStepSnapshot — PreCheckSnapshot + VerifySnapshot 调整

**Files:**
- Modify: `src/PeakCan.Host.Core/Uds/FlashPipeline/FlashStepSnapshot.cs`

**Interfaces:**
- Produces: `PreCheckSnapshot`, `VerifySnapshot` 保留（executor 读 StartAddress/EndAddress/ExpectedChecksum）

- [ ] **Step 1: 新增 PreCheckSnapshot 记录**

在 SecurityAccessSnapshot 前加：

```csharp
public sealed record PreCheckSnapshot(
    ushort RoutineId);
```

- [ ] **Step 2: FlashStepSnapshot 加 PreCheck 属性**

```csharp
public PreCheckSnapshot? PreCheck { get; init; }
```

- [ ] **Step 3: VerifySnapshot 保持不变**

Verify 的 StartAddress/EndAddress/ExpectedChecksum 仍由 UI 层从 Segment 计算后填入 snapshot。

- [ ] **Step 4: 编译验证**

Run: `dotnet build src/PeakCan.Host.Core/`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Core/Uds/FlashPipeline/FlashStepSnapshot.cs
git commit -m "feat(uds-flash): FlashStepSnapshot 加 PreCheckSnapshot"
```

---

## Task 4: PipelineExecutor — PreCheck case 执行 RoutineControl

**Files:**
- Modify: `src/PeakCan.Host.Core/Uds/FlashPipeline/PipelineExecutor.cs`

**Interfaces:**
- Consumes: `step.PreCheck?.RoutineId`

- [ ] **Step 1: PreCheck case 改为执行 RoutineControl**

```csharp
case FlashStepKind.PreCheck:
    var preCheck = step.PreCheck ?? throw new InvalidOperationException("PreCheck params missing");
    var preCheckRoutineId = preCheck.RoutineId;
    // 调 RoutineControl(StartRoutine, routineId, null) — 返回非空即成功
    await client.RoutineControlAsync(StartRoutine, preCheckRoutineId, data: null, ct).ConfigureAwait(false);
    break;
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build src/PeakCan.Host.Core/`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/PeakCan.Host.Core/Uds/FlashPipeline/PipelineExecutor.cs
git commit -m "feat(uds-flash): PreCheck case 执行 RoutineControl 预检查 routine"
```

---

## Task 5: FlashPanelViewModel — Remove 命令 + SelectedFirmwareFile + AddressingMode + NotifyCanExecuteChangedFor

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashPanelViewModel.cs`

**Interfaces:**
- Produces: `RemoveFirmwareFileCommand`, `RemoveFlashDriverCommand`, `SelectedFirmwareFile`, `CanMoveUp/CanMoveDown` 正确刷新

- [ ] **Step 1: 加 SelectedFirmwareFile 属性**

在 SelectedStep 后加：

```csharp
[ObservableProperty]
private FirmwareFile? _selectedFirmwareFile;
```

- [ ] **Step 2: 加 RemoveFirmwareFileCommand**

```csharp
[RelayCommand(CanExecute = nameof(CanEditSteps))]
private void RemoveFirmwareFile()
{
    if (SelectedFirmwareFile is { } file)
        CurrentProfile.FirmwareFiles.Remove(file);
}
```

- [ ] **Step 3: 改 AddFlashDriver 为替换语义 + 加 RemoveFlashDriverCommand**

```csharp
[RelayCommand(CanExecute = nameof(CanEditSteps))]
private void AddFlashDriver()
{
    var path = _fileDialog.ShowOpenDialog(
        "Flash driver (*.dll;*.bin)|*.dll;*.bin|DLL files (*.dll)|*.dll|Binary (*.bin)|*.bin|All files|*.*");
    if (path is null) return;
    try
    {
        var bytes = File.ReadAllBytes(path);
        CurrentProfile.FlashDriver = new FlashDriver(path, bytes);  // 替换语义
        StatusMessage = $"Loaded flash driver: {IOPath.GetFileName(path)} ({bytes.Length} bytes)";
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to load flash driver {Path}", path);
        Status = FlashStatus.Failed;
        StatusMessage = $"Failed to load driver: {ex.Message}";
    }
}

[RelayCommand(CanExecute = nameof(CanEditSteps))]
private void RemoveFlashDriver()
{
    CurrentProfile.FlashDriver = null;
}
```

- [ ] **Step 4: SelectedStep 补 NotifyCanExecuteChangedFor**

```csharp
[ObservableProperty]
[NotifyCanExecuteChangedFor(nameof(RemoveStepCommand))]
[NotifyCanExecuteChangedFor(nameof(SelectDllCommand))]
[NotifyCanExecuteChangedFor(nameof(SelectFirmwareCommand))]
[NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
[NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
private FlashStep? _selectedStep;
```

- [ ] **Step 5: ToSnapshot() 加 PreCheck + Verify 自动计算**

```csharp
PreCheck = step.PreCheck is { } pc ? new PreCheckSnapshot(pc.RoutineId) : null,
Verify = step.Verify is { } v ? new VerifySnapshot(
    (byte)v.Algorithm,
    /* ExpectedChecksum */ ExpectedChecksumFromSegment(v.SegmentIndex),
    /* StartAddress */ StartAddressFromSegment(v.SegmentIndex),
    /* EndAddress */ EndAddressFromSegment(v.SegmentIndex)) : null,
```

加三个 helper 方法：

```csharp
private uint ExpectedChecksumFromSegment(int index)
{
    var seg = SegmentAtIndex(index);
    return seg?.Crc32 ?? 0;
}
private uint StartAddressFromSegment(int index)
{
    var seg = SegmentAtIndex(index);
    return seg?.StartAddress ?? 0;
}
private uint EndAddressFromSegment(int index)
{
    var seg = SegmentAtIndex(index);
    return seg?.EndAddress ?? 0;
}
private Segment? SegmentAtIndex(int index)
{
    var all = CurrentProfile.FirmwareFiles.SelectMany(f => f.Segments).ToList();
    return (index >= 0 && index < all.Count) ? all[index] : null;
}
```

- [ ] **Step 6: 编译验证**

Run: `dotnet build src/PeakCan.Host.App/`
Expected: 0 errors

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashPanelViewModel.cs
git commit -m "feat(uds-flash): Remove 命令 + SelectedFirmwareFile + Up/Down 刷新 + Verify 自动算"
```

---

## Task 6: UdsWindow.xaml — 属性面板 + 对齐 + PreCheck 组

**Files:**
- Modify: `src/PeakCan.Host.App/Windows/UdsWindow.xaml`

**Interfaces:**
- Consumes: 所有 VM 新增命令和属性

- [ ] **Step 1: Firmware Files 区域加删除按钮**

在 Firmware Files DockPanel 里 ListBox 后加：

```xml
<StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,4,0,0">
    <Button Content="+ Add Firmware File" Padding="4,2" Margin="0,0,4,0"
            Command="{Binding Flash.AddFirmwareFileCommand}"
            ToolTip="Load a .hex / .s19 / .bin firmware file"/>
    <Button Content="- Remove" Padding="4,2"
            Command="{Binding Flash.RemoveFirmwareFileCommand}"
            ToolTip="Remove selected firmware file"/>
</StackPanel>
<ListBox ItemsSource="{Binding Flash.CurrentProfile.FirmwareFiles}"
         SelectedItem="{Binding Flash.SelectedFirmwareFile, Mode=TwoWay}"
         Height="80" DisplayMemberPath="Path"/>
```

- [ ] **Step 2: Flash Driver 区域改为单文件 + 删除**

```xml
<DockPanel Grid.Column="1">
    <TextBlock DockPanel.Dock="Top" Text="Flash Driver:" FontWeight="SemiBold" Margin="0,0,0,4"/>
    <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,4,0,0">
        <Button Content="+ Add Flash Driver" Padding="4,2" Margin="0,0,4,0"
                Command="{Binding Flash.AddFlashDriverCommand}"
                ToolTip="Load a flash driver DLL or binary"/>
        <Button Content="- Remove" Padding="4,2"
                Command="{Binding Flash.RemoveFlashDriverCommand}"
                ToolTip="Remove flash driver"/>
    </StackPanel>
    <Border Height="80" BorderBrush="#CCC" BorderThickness="1" Padding="4">
        <TextBlock Text="{Binding Flash.CurrentProfile.FlashDriver.Path, TargetNullValue='(no driver loaded)'}"
                   TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
    </Border>
</DockPanel>
```

- [ ] **Step 3: "Apply Multi-Level Template" 按钮移出 Flash Driver 列**

从 Flash Driver DockPanel 删除该按钮，加到 Step 按钮行（Add Step / Remove Step 旁）：

```xml
<StackPanel Orientation="Horizontal" Margin="0,0,0,8">
    <ComboBox x:Name="KindPicker" ItemsSource="{Binding Flash.AddableKinds}"
              SelectedIndex="3" Width="140" Margin="0,0,4,0"
              ToolTip="Step kind to add"/>
    <Button Content="Add Step" Padding="8,3" MinWidth="72" Margin="0,0,4,0"
            Command="{Binding Flash.AddStepCommand}"
            CommandParameter="{Binding SelectedItem, ElementName=KindPicker}"
            ToolTip="Append a new step of the selected kind"/>
    <Button Content="Remove Step" Padding="8,3" MinWidth="80" Margin="0,0,4,0"
            Command="{Binding Flash.RemoveStepCommand}"
            ToolTip="Remove the selected step"/>
    <Button Content="Apply Multi-Level Template" Padding="4,2"
            Command="{Binding Flash.ApplyMultiLevelTemplateCommand}"
            ToolTip="Replace steps with a typical dual-region flash flow template"/>
</StackPanel>
```

- [ ] **Step 4: AddressingMode ComboBox 仅 0x28 可选**

```xml
<Grid Margin="0,0,0,8">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="100"/>
        <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>
    <TextBlock Grid.Column="0" Text="Addressing:" VerticalAlignment="Center"/>
    <ComboBox Grid.Column="1"
              ItemsSource="{Binding Flash.AddressingModes}"
              SelectedItem="{Binding Flash.SelectedStep.AddressingMode}"
              IsEnabled="{Binding Flash.SelectedStep.CommunicationControl, Converter={StaticResource NullToBooleanConverter}}"/>
</Grid>
```

> 注意：需要新增 `NullToBooleanConverter`（null → false, non-null → true）。如果不想加 converter，可以用 DataTrigger 替代。

- [ ] **Step 5: 加 PreCheck 属性面板组**

在 SecurityAccess 组之前加：

```xml
<!-- PreCheck properties -->
<StackPanel Visibility="{Binding Flash.SelectedStep.PreCheck, Converter={StaticResource NullToVisibilityConverter}}">
    <TextBlock Text="PreCheck" FontWeight="SemiBold" Margin="0,0,0,4"/>
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="100"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>
        <Grid.RowDefinitions>
            <RowDefinition/>
        </Grid.RowDefinitions>
        <TextBlock Grid.Row="0" Grid.Column="0" Text="Routine ID:" VerticalAlignment="Center"/>
        <TextBox Grid.Row="0" Grid.Column="1" Text="{Binding Flash.SelectedStep.PreCheck.RoutineId, StringFormat=0x{0:X4}}"/>
    </Grid>
</StackPanel>
```

- [ ] **Step 6: Verify 组改为只读展示**

```xml
<!-- Verify properties -->
<StackPanel Visibility="{Binding Flash.SelectedStep.Verify, Converter={StaticResource NullToVisibilityConverter}}">
    <TextBlock Text="Verify" FontWeight="SemiBold" Margin="0,8,0,4"/>
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="100"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>
        <Grid.RowDefinitions>
            <RowDefinition/><RowDefinition/><RowDefinition/><RowDefinition/>
        </Grid.RowDefinitions>
        <TextBlock Grid.Row="0" Grid.Column="0" Text="Algorithm:" VerticalAlignment="Center"/>
        <ComboBox Grid.Row="0" Grid.Column="1"
                  ItemsSource="{Binding Flash.ChecksumAlgorithms}"
                  SelectedItem="{Binding Flash.SelectedStep.Verify.Algorithm}"/>
        <TextBlock Grid.Row="1" Grid.Column="0" Text="Segment:" VerticalAlignment="Center"/>
        <ComboBox Grid.Row="1" Grid.Column="1" ItemsSource="{Binding Flash.CurrentProfile.FirmwareFiles}"
                  SelectedIndex="{Binding Flash.SelectedStep.Verify.SegmentIndex}"/>
        <TextBlock Grid.Row="2" Grid.Column="0" Text="Expected CRC:" VerticalAlignment="Center"/>
        <TextBlock Grid.Row="2" Grid.Column="1" Text="{Binding Flash.SelectedStep.Verify.ExpectedChecksum, StringFormat=0x{0:X8}}"/>
        <TextBlock Grid.Row="3" Grid.Column="0" Text="Address:" VerticalAlignment="Center"/>
        <TextBlock Grid.Row="3" Grid.Column="1">
            <Run Text="0x"/><Run Text="{Binding Flash.SelectedStep.Verify.StartAddress, StringFormat={}{0:X8}}"/>
            <Run Text=" - 0x"/><Run Text="{Binding Flash.SelectedStep.Verify.EndAddress, StringFormat={}{0:X8}}"/>
        </TextBlock>
    </Grid>
</StackPanel>
```

> 注意：VerifyParams 的 ExpectedChecksum/StartAddress/EndAddress 需要改为可通知属性或在 getter 里从 Segment 计算。最简单方案：保留 `{ get; set; }`，在 `FlashPanelViewModel.ToSnapshot()` 里从 Segment 计算后赋值给 snapshot（UI 显示用单独的计算属性或直接在 View 绑定到 Segment 数据）。

**更简洁方案**：VerifyParams 保留 `{ get; set; }`，UI 绑定到 `Flash.CurrentProfile.FirmwareFiles[Verify.SegmentIndex]` 的对应字段。这样 VerifyParams 本身不存地址/CRC，UI 直接显示 Segment 数据。

- [ ] **Step 7: 编译验证**

Run: `dotnet build src/PeakCan.Host.App/`
Expected: 0 errors

- [ ] **Step 8: Commit**

```bash
git add src/PeakCan.Host.App/Windows/UdsWindow.xaml
git commit -m "feat(uds-flash): 属性面板加 PreCheck/Verify 只读/删除按钮/对齐/AddressingMode 灰掉"
```

---

## Task 7: AddableKinds 加回 PreCheck

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashPanelViewModel.cs`

- [ ] **Step 1: AddableKinds 加回 PreCheck**

```csharp
public static IReadOnlyList<FlashStepKind> AddableKinds { get; } =
[
    FlashStepKind.PreCheck,         // 加回
    FlashStepKind.SecurityAccess,
    FlashStepKind.Erase,
    FlashStepKind.DownloadTransfer,
    FlashStepKind.Verify,
    FlashStepKind.EcuReset,
    FlashStepKind.CommunicationControl,
    FlashStepKind.DtcControl,
];
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build src/PeakCan.Host.App/`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashPanelViewModel.cs
git commit -m "feat(uds-flash): AddableKinds 加回 PreCheck"
```

---

## Task 8: 测试 — Core 层

**Files:**
- Modify/Create: `tests/PeakCan.Host.Core.Tests/Uds/FlashPipeline/PipelineExecutorTests.cs`

- [ ] **Step 1: PreCheck 步骤执行 RoutineControl 测试**

```csharp
[Fact]
public async Task PreCheckStep_With_RoutineId_Sends_RoutineControl_0x31()
{
    // Arrange
    var mockClient = new MockUdsClient();
    mockClient.SetRoutineControlResponse(0xFF01, new byte[] { 0x01, 0x02 });  // 非空返回
    var snapshot = new FlashStepSnapshot
    {
        Kind = FlashStepKind.PreCheck,
        IsEnabled = true,
        PreCheck = new PreCheckSnapshot(0xFF01),
    };

    // Act
    await PipelineExecutor.ExecuteAsync(mockClient, new[] { snapshot },
        (_, __) => null, null, null, CancellationToken.None);

    // Assert
    Assert.Contains(mockClient.SentFrames, f => f.Data[0] == 0x31 && f.Data[1] == 0xFF && f.Data[2] == 0x01);
}
```

- [ ] **Step 2: 运行测试**

Run: `dotnet test --filter "FullyQualifiedName~PreCheckStep_With_RoutineId" -v`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add tests/PeakCan.Host.Core.Tests/Uds/FlashPipeline/PipelineExecutorTests.cs
git commit -m "test(uds-flash): PreCheck 步骤执行 RoutineControl 测试"
```

---

## Task 9: 测试 — App 层

**Files:**
- Modify/Create: `tests/PeakCan.Host.App.Tests/ViewModels/Uds/FlashPipeline/FlashPanelViewModelTests.cs`

- [ ] **Step 1: RemoveFirmwareFile 测试**

```csharp
[Fact]
public void RemoveFirmwareFile_Selected_Removes_From_Collection()
{
    // Arrange
    var vm = CreateVm();
    var file = new FirmwareFile("test.hex", FirmwareFormat.IntelHex, Array.Empty<Segment>());
    vm.CurrentProfile.FirmwareFiles.Add(file);
    vm.SelectedFirmwareFile = file;

    // Act
    vm.RemoveFirmwareFileCommand.Execute(null);

    // Assert
    Assert.DoesNotContain(file, vm.CurrentProfile.FirmwareFiles);
}
```

- [ ] **Step 2: RemoveFlashDriver 测试**

```csharp
[Fact]
public void RemoveFlashDriver_Sets_Null()
{
    // Arrange
    var vm = CreateVm();
    vm.CurrentProfile.FlashDriver = new FlashDriver("driver.dll", new byte[] { 0x01 });

    // Act
    vm.RemoveFlashDriverCommand.Execute(null);

    // Assert
    Assert.Null(vm.CurrentProfile.FlashDriver);
}
```

- [ ] **Step 3: Up/Down 按钮刷新测试**

```csharp
[Fact]
public void SelectedStep_Notifies_MoveUp_CanExecute()
{
    // Arrange
    var vm = CreateVm();
    vm.CurrentProfile.Steps.Add(new FlashStep(FlashStepKind.Erase));
    vm.CurrentProfile.Steps.Add(new FlashStep(FlashStepKind.DownloadTransfer));
    var firstStep = vm.CurrentProfile.Steps[0];

    // Act
    vm.SelectedStep = firstStep;

    // Assert — MoveUp 在第一位应禁用
    Assert.False(vm.MoveUpCommand.CanExecute(null));
    Assert.True(vm.MoveDownCommand.CanExecute(null));
}
```

- [ ] **Step 4: FlashDriver 单文件替换测试**

```csharp
[Fact]
public void AddFlashDriver_Twice_Replaces_Previous()
{
    // Arrange
    var vm = CreateVm();
    var dialog = new Mock<IFileDialogService>();
    dialog.SetupSequence(x => x.ShowOpenDialog(It.IsAny<string>()))
          .Returns("driver1.dll")
          .Returns("driver2.dll");
    vm.SetFileDialog(dialog.Object);

    // Act
    vm.AddFlashDriverCommand.Execute(null);
    vm.AddFlashDriverCommand.Execute(null);

    // Assert
    Assert.NotNull(vm.CurrentProfile.FlashDriver);
    Assert.EndsWith("driver2.dll", vm.CurrentProfile.FlashDriver.Path);
}
```

- [ ] **Step 5: 运行测试**

Run: `dotnet test --filter "FullyQualifiedName~FlashPanelViewModel" -v`
Expected: all PASS

- [ ] **Step 6: Commit**

```bash
git add tests/PeakCan.Host.App.Tests/ViewModels/Uds/FlashPipeline/FlashPanelViewModelTests.cs
git commit -m "test(uds-flash): Remove 命令 + Up/Down + FlashDriver 单文件测试"
```

---

## Task 10: 全量测试验证

- [ ] **Step 1: 运行全量测试**

Run: `dotnet test --filter "FullyQualifiedName~PeakCan" --no-build`
Expected: 1561+ tests pass, 0 fail

- [ ] **Step 2: 如有失败，修复后重新运行**

- [ ] **Step 3: 最终 Commit（如有修复）**

```bash
git add -A
git commit -m "fix(uds-flash): 全量测试修复" --allow-empty
```

---

## 验证清单

修复完成后确认：

- [ ] PreCheck 在 AddableKinds 下拉栏可见
- [ ] PreCheck 属性面板显示 RoutineId 输入框
- [ ] Firmware Files 有 [- Remove] 按钮
- [ ] Flash Driver 单文件 + 有 [- Remove]
- [ ] 两框对齐（Apply Template 按钮移出）
- [ ] Up/Down 按钮选中步骤后启用
- [ ] Verify 组显示 Segment 下拉 + 只读 CRC/地址
- [ ] AddressingMode ComboBox 仅 0x28 可选
- [ ] 0x28 CommunicationControl 运行时用 FunctionalId 发送
- [ ] 全量测试通过
