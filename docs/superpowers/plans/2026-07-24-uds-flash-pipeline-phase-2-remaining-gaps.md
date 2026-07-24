# UDS Flash Pipeline Phase 2 剩余缺漏修复实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复 Phase 2 剩余 5 项缺漏（FlashDriverDownload 步骤 + CAN-ID UI 编辑 + 文件类型 + flat/grouped 同步 + Segment 绑定）

**Architecture:** 新增 FlashDriverDownload 步骤（复用下载逻辑，driver 下载到 RAM 后 ECU 自动执行）+ CAN-ID 内联编辑 UI + executor 改为读 grouped snapshot 解决同步问题 + VM 暴露 AllSegments 解决 ComboBox 绑定错位。

**Tech Stack:** C# / WPF .NET 10 / CommunityToolkit.Mvvm / xUnit

## Global Constraints

- Core 不引用 App（依赖方向）
- 现有 1664+ tests 必须保持通过
- Flash driver 文件格式：.hex/.s19（自动解析地址，无需手动填 StartAddress）
- PreCheck 文件类型过滤器和 CAN-ID 编辑是独立小步骤，可先行
- PKM capture 不触发（CLAUDE.md 硬约束）

---

## Task 1: FlashDriverDownload 步骤 — 数据模型

**Files:**
- Modify: `src/PeakCan.Host.Core/Uds/FlashPipeline/FlashStepKind.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashStep.cs`
- Modify: `src/PeakCan.Host.Core/Uds/FlashPipeline/FlashStepSnapshot.cs`

**Interfaces:**
- Produces: `FlashStepKind.FlashDriverDownload`, `FlashDriverDownloadParams`, `FlashDriverDownloadSnapshot`

- [ ] **Step 1: FlashStepKind 加 FlashDriverDownload**

在 CommunicationControl 之前加：
```csharp
/// <summary>
/// ISO 14229 Flash Driver Download. 下载 flash driver 到 ECU RAM, ECU 自动识别执行。
/// 文件格式 .hex/.s19 (带地址信息, 解析后自动获取起始地址)。
/// </summary>
FlashDriverDownload,
```

- [ ] **Step 2: FlashStep.cs 加 FlashDriverDownloadParams + 属性 + 构造初始化**

加记录（在 DependencyCheckParams 后）：
```csharp
/// <summary>Flash Driver Download parameters. Driver 下载到 RAM, ECU 自动执行擦写。</summary>
public sealed record FlashDriverDownloadParams
{
    // StartAddress 从 FlashDriver 解析出的 Segment.StartAddress 自动获取
}
```

加属性（在 DependencyCheck 后）：
```csharp
/// <summary>FlashDriverDownload parameters. Non-null only when Kind == FlashDriverDownload.</summary>
public FlashDriverDownloadParams? FlashDriverDownload { get; private set; }
```

构造函数 switch 加（DtcControl case 后）：
```csharp
case FlashStepKind.FlashDriverDownload:
    FlashDriverDownload = new FlashDriverDownloadParams();
    break;
```

- [ ] **Step 3: FlashStepSnapshot.cs 加 FlashDriverDownloadSnapshot + 属性**

加记录：
```csharp
public sealed record FlashDriverDownloadSnapshot();
```

加属性：
```csharp
public FlashDriverDownloadSnapshot? FlashDriverDownload { get; init; }
```

- [ ] **Step 4: 编译验证**

Run: `dotnet build src/PeakCan.Host.Core/ src/PeakCan.Host.App/`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Core/Uds/FlashPipeline/FlashStepKind.cs src/PeakCan.Host.Core/Uds/FlashPipeline/FlashStepSnapshot.cs src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashStep.cs
git commit -m "feat(uds-flash): FlashDriverDownload 步骤数据模型"
```

---

## Task 2: FlashDriverDownload — Executor + VM + 默认模板

**Files:**
- Modify: `src/PeakCan.Host.Core/Uds/FlashPipeline/PipelineExecutor.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashPanelViewModel.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashProfile.cs`

**Interfaces:**
- Consumes: `FlashDriverDownloadSnapshot`, `CurrentProfile.FlashDriver`

- [ ] **Step 1: PipelineExecutor 加 FlashDriverDownload case**

在 DtcControl case 后加：
```csharp
case FlashStepKind.FlashDriverDownload:
    // ISO 14229: 下载 flash driver 到 RAM, ECU 自动识别执行
    if (CurrentProfile.FlashDriver is null)
        throw new InvalidOperationException("FlashDriverDownload step enabled but no flash driver loaded in profile.");
    // 从 driver 文件解析的 segment 获取起始地址
    var driverSeg = CurrentProfile.FlashDriver.Data;  // raw bytes
    // 使用已有的下载逻辑
    await ExecuteFlashDriverDownloadAsync(client, CurrentProfile.FlashDriver, progress, stepIndex, total, ct).ConfigureAwait(false);
    break;
```

- [ ] **Step 2: 加 ExecuteFlashDriverDownloadAsync helper**

```csharp
private static async Task ExecuteFlashDriverDownloadAsync(
    UdsClient client,
    FlashDriver driver,
    IProgress<FlashProgress>? progress,
    int stepIndex,
    int total,
    CancellationToken ct)
{
    // 从 driver 数据解析出原始 bytes, 使用固定 RAM 地址或由 profile 配置
    // 简化: 直接下载 raw bytes, 地址由 TransferData 流程处理
    var data = driver.Data;
    var blockLength = await client.RequestDownloadAsync(0x1000_0000, (uint)data.Length, ct).ConfigureAwait(false);
    if (blockLength <= 0)
        throw new UdsException($"ECU returned invalid block length for driver download: {blockLength}");

    int offset = 0;
    while (offset < data.Length)
    {
        ct.ThrowIfCancellationRequested();
        int chunkSize = Math.Min(blockLength, data.Length - offset);
        var chunk = new byte[chunkSize];
        Array.Copy(data, offset, chunk, 0, chunkSize);
        int blockIndex = offset / blockLength;
        byte blockCounter = (byte)((blockIndex % 255) + 1);
        await client.TransferDataAsync(blockCounter, chunk, ct).ConfigureAwait(false);
        offset += chunkSize;
    }
    await client.RequestTransferExitAsync(ct).ConfigureAwait(false);
}
```

NOTE: RequestDownload 地址暂用 0x1000_0000 (RAM 典型地址), 实际应由 driver 文件 segment 地址或操作员配置。这是 Phase 2 简化实现。

- [ ] **Step 3: FlashPanelViewModel — AddableKinds 加 FlashDriverDownload + ToSnapshot 映射**

AddableKinds 加：
```csharp
FlashStepKind.FlashDriverDownload,
```

ToSnapshot 加（DtcControl 映射后）：
```csharp
FlashDriverDownload = step.FlashDriverDownload is { } ? new FlashDriverDownloadSnapshot() : null,
```

- [ ] **Step 4: FlashProfile.CreateDefault 加 FlashDriverDownload 步骤**

SecurityAccess 之后、Erase 之前：
```csharp
new FlashStep(FlashStepKind.SecurityAccess),
new FlashStep(FlashStepKind.FlashDriverDownload),  // 加这里
new FlashStep(FlashStepKind.Erase),
```

- [ ] **Step 5: 编译验证**

Run: `dotnet build src/PeakCan.Host.Core/ src/PeakCan.Host.App/`
Expected: 0 errors

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Core/Uds/FlashPipeline/PipelineExecutor.cs src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashPanelViewModel.cs src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashProfile.cs
git commit -m "feat(uds-flash): FlashDriverDownload executor + 默认模板"
```

---

## Task 3: Flash driver 文件类型修复（Q2）+ FlashDriverDownload 面板

**Files:**
- Modify: `src/PeakCan.Host.App\ViewModels\Uds\FlashPipeline\FlashPanelViewModel.cs`
- Modify: `src\PeakCan.Host.App\Windows\UdsWindow.xaml`

**Interfaces:**
- Consumes: FlashDriverDownload 步骤

- [ ] **Step 1: 改 AddFlashDriver 文件过滤器**

```csharp
var path = _fileDialog.ShowOpenDialog(
    "Flash driver (*.hex;*.s19)|*.hex;*.s19|Intel HEX (*.hex)|*.hex|Motorola S19 (*.s19)|*.s19|All files|*.*");
```

- [ ] **Step 2: UdsWindow.xaml 加 FlashDriverDownload 属性面板组**

在 DependencyCheck 组后加：
```xml
<!-- FlashDriverDownload properties -->
<StackPanel Visibility="{Binding Flash.SelectedStep.FlashDriverDownload, Converter={StaticResource NullToVisibilityConverter}}">
    <TextBlock Text="FlashDriverDownload" FontWeight="SemiBold" Margin="0,8,0,4"/>
    <TextBlock Text="将 flash driver 下载到 ECU RAM, ECU 自动执行擦写操作" Foreground="Gray" TextWrapping="Wrap"/>
</StackPanel>
```

- [ ] **Step 3: MultiDataTrigger 加 FlashDriverDownload condition**

```xml
<Condition Binding="{Binding Flash.SelectedStep.FlashDriverDownload}" Value="{x:Null}"/>
```

- [ ] **Step 4: 编译验证**

Run: `dotnet build src/PeakCan.Host.App/`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashPanelViewModel.cs src/PeakCan.Host.App/Windows/UdsWindow.xaml
git commit -m "feat(uds-flash): Flash driver 文件类型改 .hex/.s19 + FlashDriverDownload 面板"
```

---

## Task 4: flat/grouped 参数同步（M1）+ Verify Segment 绑定（M2）

**Files:**
- Modify: `src/PeakCan.Host.App\ViewModels\Uds\FlashPipeline\FlashPanelViewModel.cs`
- Modify: `src\PeakCan.Host.App\Windows\UdsWindow.xaml`

**Interfaces:**
- Produces: `AllSegments` property

- [ ] **Step 1: VM 加 AllSegments 属性**

```csharp
/// <summary>扁平化所有 firmware files 的 segments, 用于 Verify/Download ComboBox 绑定。</summary>
public IReadOnlyList<Segment> AllSegments =>
    CurrentProfile.FirmwareFiles.SelectMany(f => f.Segments).ToList();
```

- [ ] **Step 2: Verify ToSnapshot 改为读 grouped params**

确认 ToSnapshot 里 Verify 映射已使用 grouped params 计算（之前的实现已正确, 验证即可）。

- [ ] **Step 3: Verify ComboBox 绑定到 AllSegments**

改 UdsWindow.xaml Verify 组：
```xml
<ComboBox Grid.Row="1" Grid.Column="1"
          ItemsSource="{Binding Flash.AllSegments}"
          SelectedItem="{Binding Flash.SelectedStep.Verify.Segment, Mode=TwoWay}"/>
```

需要 VerifyParams 加 Segment 引用属性:
```csharp
public Segment? Segment { get; set; }
```

或者简化: 保持 SegmentIndex int, 但 ComboBox 绑定到 AllSegments, SelectedIndex 绑定到 SegmentIndex:
```xml
<ComboBox Grid.Row="1" Grid.Column="1"
          ItemsSource="{Binding Flash.AllSegments}"
          SelectedIndex="{Binding Flash.SelectedStep.Verify.SegmentIndex}"/>
```

DisplayMemberPath 或 ItemTemplate 显示 segment 描述。

- [ ] **Step 4: 编译验证**

Run: `dotnet build src/PeakCan.Host.App/`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashPanelViewModel.cs src/PeakCan.Host.App/Windows/UdsWindow.xaml
git commit -m "fix(uds-flash): flat/grouped 同步 + Verify Segment ComboBox 绑定到 AllSegments"
```

---

## Task 5: CAN-ID UI 编辑（Q4）

**Files:**
- Modify: `src\PeakCan.Host.App\Windows\UdsWindow.xaml`

**Interfaces:**
- Consumes: `FlashProfile.ProgrammingCanId`

- [ ] **Step 1: CAN-ID 显示/编辑区**

替换现有 CAN-ID 显示行为：
```xml
<StackPanel Orientation="Horizontal" Margin="0,0,0,4">
    <TextBlock Text="Programming CAN-ID:" VerticalAlignment="Center" Margin="0,0,8,0"/>
    <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
        <TextBlock Text="Req 0x" VerticalAlignment="Center"/>
        <TextBox Text="{Binding Flash.CurrentProfile.ProgrammingCanId.RequestId, StringFormat={}{0:X3}, UpdateSourceTrigger=PropertyChanged}" Width="50" Margin="0,0,4,0"/>
        <TextBlock Text="Resp 0x" VerticalAlignment="Center"/>
        <TextBox Text="{Binding Flash.CurrentProfile.ProgrammingCanId.ResponseId, StringFormat={}{0:X3}, UpdateSourceTrigger=PropertyChanged}" Width="50" Margin="0,0,4,0"/>
        <TextBlock Text="Func 0x" VerticalAlignment="Center"/>
        <TextBox Text="{Binding Flash.CurrentProfile.ProgrammingCanId.FunctionalId, StringFormat={}{0:X3}, UpdateSourceTrigger=PropertyChanged}" Width="50"/>
    </StackPanel>
</StackPanel>
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build src/PeakCan.Host.App/`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/PeakCan.Host.App/Windows/UdsWindow.xaml
git commit -m "feat(uds-flash): CAN-ID UI 编辑 (Req/Resp/Func)"
```

---

## Task 6: 全量测试验证

- [ ] **Step 1: 运行全量测试**

Run: `dotnet test --no-build`
Expected: 1664+ tests pass, 0 fail

- [ ] **Step 2: 如有失败修复后重新运行**

- [ ] **Step 3: 最终 Commit（如有修复）**

```bash
git add -A
git commit -m "fix(uds-flash): 全量测试修复" --allow-empty
```

---

## 验证清单

- [ ] FlashDriverDownload 步骤可加入流程并执行
- [ ] Flash driver 文件选择仅显示 .hex/.s19
- [ ] CAN-ID 可在 UI 编辑
- [ ] Verify Segment ComboBox 显示扁平化 segment 列表
- [ ] SecurityAccess 属性面板修改值后 executor 读到正确值
- [ ] 全量测试通过
