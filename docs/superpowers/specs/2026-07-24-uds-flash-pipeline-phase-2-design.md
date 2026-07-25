# UDS 刷写 pipeline Phase 2 设计 spec — Segment 模型 + HEX/S19 + 属性面板

## Context

### 为什么需要 Phase 2

当前 Phase 1.1 实现了多文件、文件浏览、Profile 保存加载，但底层数据模型仍是 **raw binary + 手填协议参数**。真实汽车刷写的核心矛盾：

1. **HEX/S19 是行业标准**（90%+ OEM 用 HEX/S19），文件自带地址和大小 → 手填 `MemoryAddress` 的设计不再成立
2. **DataGrid 统一列布局**把所有协议字段平铺给操作员 → 巨大误解空间（每行都看到 SecMode/DllPath/Routine/Addr/FW Path/Reset，不管该步骤是否用到）
3. **Checksum/Erase 范围缺失** → 无法验证写入正确性、无法告诉 ECU 擦哪块
4. **AutoReset vs Reset 命名混淆** → 操作员看不出"正常重启"和"故障保险"的区别

### 设计目标

| 当前（Phase 1.1） | 目标（Phase 2） |
|---|---|
| raw binary 为中心 | HEX / S19 / raw binary 统一支持 |
| 手填 MemoryAddress | 地址从文件解析自动提取 |
| DataGrid 统一列 | 属性面板，按 Kind 只显示相关字段 |
| 一个 FirmwareImage = 一个文件 | 一个文件 → 多 Segment（非连续地址区域） |
| 无 Checksum | 每个 Segment 下载后自动 Verify (CRC32) |
| AutoReset/Reset 混淆 | 语义清晰化 + UI 分层 |

---

## 一、数据模型重构

### 1.1 核心变化：引入 Segment

**当前**：`FlashStep.DownloadTransfer` 直接持有 `FirmwarePath` + `MemoryAddress`

**目标**：固件文件解析为 Segment 列表，Download 步骤引用 Segment 索引

```csharp
/// <summary>
/// Parsed firmware file — one file yields one or more address-contiguous segments.
/// HEX/S19 naturally produce multiple segments (e.g. bootloader @ 0x0800 + app @ 0x10000).
/// Raw binary yields a single segment whose address the operator must specify.
/// </summary>
public sealed record FirmwareFile
{
    public required string Path { get; init; }
    public required FirmwareFormat Format { get; init; }
    public required IReadOnlyList<Segment> Segments { get; init; }
}

public sealed record Segment
{
    public required uint StartAddress { get; init; }  // 文件自带（HEX/S19）或操作员指定（raw）
    public required byte[] Data { get; init; }
    /// <remarks>
    /// byte[] is mutable — do not modify after construction. The <c>init</c> only
    /// protects the reference reassignment, not the array contents. If a future
    /// caller needs post-construction patch/overlay, copy-on-write or switch to
    /// <c>ReadOnlyMemory&lt;byte&gt;</c> is required.
    /// </remarks>
    public uint Length => (uint)Data.Length;
    public uint EndAddress => checked(StartAddress + (uint)Data.Length - 1);  // checked 防溢出
    public uint Crc32 { get; init; }  // Phase 2 新增：解析时自动计算
}

public enum FirmwareFormat { RawBinary, IntelHex, MotorolaS19 }
```

### 1.2 FlashStep 按 Kind 分离参数

**当前**：一个 `FlashStep` 类持有所有 Kind 的所有参数（平权）

**目标**：`FlashStep` 只保留通用字段 + Kind 标签，参数按 Kind 分组

```csharp
public sealed partial class FlashStep : ObservableObject
{
    public FlashStepKind Kind { get; }
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _autoResetOnFailure = true;

    // 参数按 Kind 分组（只有对应 Kind 的组有意义，其余 null）
    public SecurityAccessParams? SecurityAccess { get; private init; }
    public EraseParams? Erase { get; private init; }
    public DownloadParams? Download { get; private init; }
    public VerifyParams? Verify { get; private init; }
    public EcuResetParams? EcuReset { get; private init; }

    private FlashStep(FlashStepKind kind) { Kind = kind; /* 初始化对应参数组 */ }
}

// 每组参数只在该 Kind 下可见/可编辑
// ⚠️ 使用 { get; set; } 而非 { get; init; } — 属性面板需要双向绑定 + 运行时编辑
// （P7 修复：init-only 会导致 WPF binding 静默失败，改了不回显）
public sealed record SecurityAccessParams
{
    public byte Level { get; set; } = 0x01;
    public SecurityAccessMode Mode { get; set; } = SecurityAccessMode.Manual;
    public string ManualKeyHex { get; set; } = "";
    public string DllPath { get; set; } = "";
}

public sealed record EraseParams
{
    public ushort RoutineId { get; set; } = 0xFF00;
    public uint StartAddress { get; set; }   // Phase 2 新增
    public uint Size { get; set; }            // Phase 2 新增
}

public sealed record DownloadParams
{
    public int SegmentIndex { get; set; }     // 引用 FirmwareFile.Segments[index]
    // MemoryAddress 不再需要 — 从 Segment.StartAddress 自动获取
}

public sealed record VerifyParams
{
    public ChecksumAlgorithm Algorithm { get; set; } = ChecksumAlgorithm.Crc32;
    public uint ExpectedChecksum { get; set; }      // 工具自动计算
    public uint StartAddress { get; set; }           // Phase 2 新增
    public uint EndAddress { get; set; }             // Phase 2 新增
}

public sealed record EcuResetParams
{
    public EcuResetType ResetType { get; set; } = EcuResetType.HardReset;
}

public enum ChecksumAlgorithm { Crc32 = 1, Crc16 = 2, OemDefined = 3 }
```

### 1.3 FlashProfile 重构

```csharp
public sealed class FlashProfile
{
    public CanIdConfig ProgrammingCanId { get; set; } = new(0x714, 0x760);
    public string Name { get; set; } = "Default Flash";
    public ObservableCollection<FlashStep> Steps { get; set; } = [];
    public ObservableCollection<FirmwareFile> FirmwareFiles { get; set; } = [];  // Phase 2 新增
}
```

---

## 二、固件文件解析

### 2.1 FirmwareFileParser 扩展

```csharp
public static class FirmwareFileParser
{
    // Phase 1 已有（保留）
    public static FirmwareImage Parse(byte[] bytes) { ... }

    // Phase 2 新增
    public static FirmwareFile ParseFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".hex" or ".ihx" => ParseIntelHex(path),
            ".s19" or ".srec" or ".mot" => ParseMotorolaS19(path),
            _ => ParseRawBinary(path),
        };
    }

    public static FirmwareFile ParseIntelHex(string path) { /* 解析 :LLAAAATT[DD...]CC */ }
    public static FirmwareFile ParseMotorolaS19(string path) { /* 解析 S1/S2/S3 记录 */ }
    public static FirmwareFile ParseRawBinary(string path); // 单 Segment，地址由操作员指定
}
```

### 2.2 Intel HEX 解析要点

```
:10010000214601360121470136007EFE09D2190140
^^^|^^^^|^^|^^^^^^^^^^^^^^^^^^^^^^^^^^^^^|^^
LL  AAAA TT DD[数据]                     CC
```

- `LL` = 数据字节数, `AAAA` = 地址, `TT` = 类型（00=数据 01=EOF 04=扩展地址）, `CC` = 校验和
- Type 04 记录设置基地址（后续数据地址 = 基地址 + AAAA）
- 非连续地址 → 多个 Segment
- 合并连续地址 → 单个 Segment

### 2.3 Motorola S19 解析要点

```
S1 13 0000 000C0300000000000000000000000000 FC
^  ^  ^^^^ ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ ^^
S1 LL AAAA DD[数据]                           checksum
```

- S1 = 2 字节地址, S2 = 3 字节, S3 = 4 字节
- S9/S8/S7 = EOF
- 同样合并连续地址

---

## 三、UI 重构：属性面板替代统一 DataGrid

### 3.1 布局

```
┌─────────────────────────────────────────────────────────────────┐
│  Flashing                                                    │
├─────────────────────────────────────────────────────────────────┤
│  Programming CAN-ID: Req 0x714 / Resp 0x760                   │
├─────────────────────────────────────────────────────────────────┤
│  Firmware Files:  [+ Add File]                                │
│  ┌─────────────────────────────────────────────────────────┐  │
│  │ app.hex (Intel HEX)                                     │  │
│  │   Segment 0: 0x0800_0000 - 0x0800_FFFF (64KB)          │  │
│  │   Segment 1: 0x0801_0000 - 0x0801_7FFF (32KB)          │  │
│  └─────────────────────────────────────────────────────────┘  │
├──────────────────────────────┬──────────────────────────────────┤
│  Pipeline Steps              │  Properties                      │
│  ┌────────────────────────┐  │  ┌────────────────────────────┐  │
│  │ ▶ 1. SessionControl    │  │  │ SecurityAccess              │  │
│  │   2. SecurityAccess ◀──┼──┼──┤ Level: [0x01]              │  │
│  │   3. Erase             │  │  │ Mode:  [Manual ▼]           │  │
│  │   4. Download          │  │  │ Key:   [AABBCCDD    ]      │  │
│  │   5. Verify            │  │  │ DLL:   [        ][…]       │  │
│  │   6. EcuReset          │  │  └────────────────────────────┘  │
│  │                        │  │                                  │
│  │ [+ Add] [- Remove]     │  │  (只有相关字段可见)              │
│  └────────────────────────┘  └──────────────────────────────────┘ │
├─────────────────────────────────────────────────────────────────┤
│  [Start] [Stop] [Save Profile] [Load Profile]                  │
│  Status: Idle                                                   │
│  [===================progress===================]               │
└─────────────────────────────────────────────────────────────────┘
```

### 3.2 属性面板规则

选中步骤后，右侧只显示该 Kind 的相关字段：

| Kind | 显示的字段 | 隐藏的字段 |
|---|---|---|
| SessionControl | （无参数） | 全部 |
| SecurityAccess | Level, Mode, ManualKeyHex, DllPath | 其余 |
| Erase | RoutineId, StartAddress, Size | 其余 |
| Download | Segment 引用（自动显示地址/大小） | 其余 |
| Verify | Algorithm, ExpectedChecksum, StartAddress, EndAddress | 其余 |
| EcuReset | ResetType（正常/软重启/断电重启） | 其余 |

### 3.3 AutoReset 和 Reset 的 UI 处理

**AutoResetOnFailure**：
- 从每行移除
- 移到"全局设置"区域（顶部工具栏下方的一个 checkbox：`☐ 出错时自动重启 ECU`）
- 默认勾选，语义清晰

**Reset (EcuReset 步骤)**：
- 属性面板中显示为下拉：`重启类型: [硬重启(默认) / 软重启 / 断电重启]`
- 标签明确为"刷完后重启"

---

## 四、Checksum / Verify 实现

### 4.1 流程

```
Download 完成 Segment @ 0x0800_0000 (64KB)
    ↓
Verify 步骤:
  1. 工具对 Segment.Data 算 CRC32 → ExpectedChecksum
  2. RoutineControl(StartRoutine, checksumRoutineId, {startAddr, endAddr, expectedCrc})
  3. ECU 返回 ActualChecksum
  4. 比对: Expected == Actual ? Success : Failed
```

### 4.2 PipelineExecutor 新增 ExecuteVerifyAsync

```csharp
private static async Task ExecuteVerifyAsync(UdsClient client, FlashStepSnapshot step, CancellationToken ct)
{
    var verify = step.VerifyParams ?? throw new InvalidOperationException("Verify params missing");
    var request = BuildChecksumRequest(verify.StartAddress, verify.EndAddress, verify.ExpectedChecksum);
    var response = await client.RoutineControlAsync(StartRoutine, verify.RoutineId, request, ct).ConfigureAwait(false);
    var actual = ParseChecksumResponse(response);
    if (actual != verify.ExpectedChecksum)
        throw new UdsException($"Checksum mismatch @ 0x{verify.StartAddress:X8}-0x{verify.EndAddress:X8}: " +
            $"expected 0x{verify.ExpectedChecksum:X8}, ECU returned 0x{actual:X8}");
}
```

### 4.3 自动 CRC32 计算

`FirmwareFileParser` 解析 Segment 时同步计算：
```csharp
public static Segment CreateSegment(uint address, byte[] data)
{
    return new Segment
    {
        StartAddress = address,
        Data = data,
        Crc32 = Crc32.Compute(data),  // 自动计算
    };
}
```

---

## 五、ODX 导入清空 Bug 修复

### 5.1 当前问题

加载第二个 ODX 文件时，**旧 ODX 数据没有被清除**——新旧数据混在一起。

根因：`DidDatabase` / `RoutineDatabase` / `DtcDatabase` 只有 `AddRange`（追加），没有 `Clear()` 方法；`OdxImportService.ImportAsync` 直接调 `AddRange` 不清旧数据。

### 5.2 修复方案

```csharp
// OdxImportService.ImportAsync 开头加：
_dids.Clear();       // 清 ODX 导入项，保留内置默认（VIN 等）
_routines.Clear();   // 全清
_dtc.Clear();        // 全清
// 然后才 AddRange 新 ODX
```

每个 Database 加 `Clear()`：
- `DidDatabase.Clear()` → 移除所有 ODX 导入项，保留内置默认
- `RoutineDatabase.Clear()` → 清空
- `DtcDatabase.Clear()` → 清空

### 5.3 涉及文件

- `src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs` — 加 Clear()
- `src/PeakCan.Host.Core/Uds/Database/RoutineDatabase.cs` — 加 Clear()
- `src/PeakCan.Host.Core/Uds/Database/DtcDatabase.cs` — 加 Clear()
- `src/PeakCan.Host.App/Services/OdxImportService.cs` — ImportAsync 开头调 Clear()

---

## 六、Functional 寻址 + CommunicationControl (0x28) + 0x3E 80

### 6.1 当前缺口

| 功能 | 现状 | 影响 |
|---|---|---|
| Functional 寻址广播（如 0x7DF） | `CanIdConfig.FunctionalId` 声明了但没有代码读取 | 无法发广播帧通知所有 ECU 静默 |
| CommunicationControl (0x28) | 完全没实现 | 刷写前无法关闭其他 ECU 通信 |
| 0x3E 80 (TesterPresent suppress) | 只有 0x3E 00 | 刷写中保活会干扰刷写帧 |
| 刷写中 TesterPresent 保活 | 完全暂停（依赖 S3 timeout） | 长刷写可能 S3 timeout 中断 |

### 6.2 修复方案

#### Functional 寻址
`IsoTpLayer` 发送路径支持 `FunctionalId`：
```csharp
// 发广播帧时用 FunctionalId 代替 RequestId
var targetId = isFunctional ? _config.FunctionalId : _config.RequestId;
```

#### CommunicationControl (0x28)
`UdsClient` 新增（P5 修复：去掉冗余的 type/subFunc 二选一，只保留一个枚举）：
```csharp
public async Task CommunicationControlAsync(
    CommunicationSubFunction subFunc, CancellationToken ct)
// 0x00=EnableRxAndTx, 0x01=EnableRxDisableTx, 0x02=DisableRxAndTx, ...
```

#### 0x3E 80 (TesterPresent suppress)
`UdsClient.TesterPresentAsync` 加 suppress 参数：
```csharp
public async Task TesterPresentAsync(bool suppressPosResponse, CancellationToken ct)
{
    byte subFunc = suppressPosResponse ? (byte)0x80 : (byte)0x00;
    await SendRequestAsync(0x3E, [subFunc], ct).ConfigureAwait(false);
}
```

#### PipelineExecutor 刷写中保活（P3 修复：后台并行保活）
**原方案有缺陷**：只在每步之前发一次 0x3E 80，无法防止长传输中途 S3 超时（64KB 通过 ISO-TP multi-frame ≈ 5-8s > S3 timeout 5s）。

**修正为后台并行保活循环**：
```csharp
public static async Task ExecuteAsync(...)
{
    // 启动后台保活任务
    using var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var keepAliveTask = KeepAliveLoopAsync(client, keepAliveCts.Token);

    try
    {
        // 原有步骤循环（不变）
        for (int i = 0; i < total; i++) { ... }
    }
    finally
    {
        keepAliveCts.Cancel();
        await keepAliveTask.ConfigureAwait(false);  // 等待保活任务退出
    }
}

private static async Task KeepAliveLoopAsync(UdsClient client, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false);
            // 0x3E 80 优先，不支持则回退 0x3E 00
            await client.TesterPresentAsync(suppressPosResponse: true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 保活失败不中断刷写，静默忽略 */ }
    }
}
```

### 6.3 标准刷写流程（Phase 2 目标）

```
[1] CommunicationControl(0x28, broadcast, DisableRxAndTx)  ← 广播静默所有 ECU
[2] SessionControl(0x10 0x03)                             ← 物理地址进编程会话
[3] SecurityAccess(0x27, level=1)                          ← 解锁
[4] Erase(0x31, routineId, addr, size)                    ← 擦除区域1
[5] DownloadTransfer(segment → addr)                       ← 下载固件1（过程中 0x3E 80 保活）
[6] Verify(0x31, checksumRoutine, expectedCRC)             ← 校验
[7] SecurityAccess(0x27, level=3)                          ← 多级解锁（如需要）
[8] Erase → Download → Verify                             ← 区域2
[9] EcuReset(0x11)                                         ← 重启跑新固件
[10] CommunicationControl(0x28, broadcast, EnableRxAndTx)  ← 恢复通信
```

### 6.4 涉及文件

- `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` — FunctionalId 发送支持
- `src/PeakCan.Host.Core/Uds/UdsClient.cs` — 加 CommunicationControlAsync + TesterPresentAsync(suppress)
- `src/PeakCan.Host.Core/Uds/FlashPipeline/PipelineExecutor.cs` — 刷写中 0x3E 80 保活

---

## 七、多级 SecurityAccess + Level 可配置

### 7.1 当前限制

- 诊断面板按钮 `SecurityAccess(Level 1)` 硬编码，永远发 level=0x01
- 刷写 pipeline `FlashStep.SecurityLevel` 默认 0x01 但可配置（只是 UI 没暴露）
- 无 ODX 关联（ODX 不配置 SecurityAccess）
- 无多级解锁引导

### 7.2 修复方案

#### UI 配置化
- 诊断面板按钮改为 `SecurityAccess`，点击后弹出 level 选择或直接从配置读
- 属性面板 SecurityAccess 步骤的 Level 字段可编辑（1-0x7F）

#### 多级解锁支持
Core 协议层已完整支持（`SecurityAccessAsync(byte level)` 接受任意 level，`UdsSecurity` 用 `Dictionary<byte, SecurityLevelState>` 跟踪），只需 UI 暴露。

#### 多级解锁引导模板
预设模板：
```
SecurityAccess(level=1) → Erase(region1) → Download → Verify
SecurityAccess(level=3) → Erase(region2) → Download → Verify
```

### 7.3 涉及文件

- `src/PeakCan.Host.App/Windows/UdsWindow.xaml` — 按钮文字不再硬编码 Level 1
- `src/PeakCan.Host.App/ViewModels/Uds/SessionPanelViewModel.cs` — SecurityAccessAsync 接受可配置 level
- `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashPanelViewModel.cs` — 多级模板引导

---

## 八、ODX 刷写配置桥接（Phase 2 可选）

### 8.1 当前问题

加载 ODX 只填充诊断数据库（DID/Routine/DTC），不影响刷写配置。

### 8.2 目标

ODX/ODX-D 文件中的 ECU-MEMORY/FLASH 描述 → 自动填充：
- 擦除 RoutineId 列表（哪个地址范围对应哪个 routine）
- SecurityAccess level（OEM 定义的解锁级别）
- 校验和算法类型

### 8.3 实现方式

新增 `IFlashConfigurationProvider` 接口：
```csharp
public interface IFlashConfigurationProvider
{
    ushort? GetEraseRoutineId(uint startAddress, uint size);
    SecurityAccessConfig? GetSecurityAccessConfig();
    ChecksumAlgorithm GetChecksumAlgorithm();
}
```

ODX 导入时注册实现 → 刷写面板读取。

---

## 九、关键文件变更清单

### 数据模型 + 解析

| 文件 | 变更类型 |
|---|---|
| `src/PeakCan.Host.Core/Uds/FlashPipeline/FirmwareFileParser.cs` | 重构：加 ParseFile/ParseIntelHex/ParseMotorolaS19 |
| `src/PeakCan.Host.Core/Uds/FlashPipeline/FirmwareFile.cs` | 新增：FirmwareFile + Segment 记录 |
| `src/PeakCan.Host.Core/Uds/FlashPipeline/FirmwareImage.cs` | 保留（raw binary 兼容） |
| `src/PeakCan.Host.Core/Uds/FlashPipeline/FlashStepSnapshot.cs` | 重构：按 Kind 分组参数 |

### 协议层

| 文件 | 变更类型 |
|---|---|
| `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` | FunctionalId 发送支持 |
| `src/PeakCan.Host.Core/Uds/UdsClient.cs` | 加 CommunicationControlAsync(subFunc) + TesterPresentAsync(suppress) |
| `src/PeakCan.Host.Core/Uds/FlashPipeline/PipelineExecutor.cs` | 新增 ExecuteVerifyAsync + ExecuteEraseWithRangeAsync + KeepAliveLoopAsync 后台并行保活 |

### ODX 清空修复

| 文件 | 变更类型 |
|---|---|
| `src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs` | 加 Clear()（保留内置默认） |
| `src/PeakCan.Host.Core/Uds/Database/RoutineDatabase.cs` | 加 Clear() |
| `src/PeakCan.Host.Core/Uds/Database/DtcDatabase.cs` | 加 Clear() |
| `src/PeakCan.Host.App/Services/OdxImportService.cs` | ImportAsync 开头调 Clear() |

### App 层 + UI

| 文件 | 变更类型 |
|---|---|
| `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashStep.cs` | 重构：参数按 Kind 分组 |
| `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashProfile.cs` | 新增 FirmwareFiles 集合 |
| `src/PeakCan.Host.App/ViewModels/Uds/FlashPipeline/FlashPanelViewModel.cs` | 重构：属性面板绑定 + 文件解析 + 自动 CRC + 多级引导 |
| `src/PeakCan.Host.App/ViewModels/Uds/SessionPanelViewModel.cs` | SecurityAccessAsync 接受可配置 level |
| `src/PeakCan.Host.App/Windows/UdsWindow.xaml` | 重构：左侧步骤列表 + 右侧属性面板 + 文件区 + 按钮文字不再硬编码 |

### 测试

| 文件 | 变更类型 |
|---|---|
| `tests/.../FirmwareFileParserTests.cs` | 新增 HEX/S19 解析测试 |
| `tests/.../PipelineExecutorTests.cs` | 新增 Verify/Erase 范围/0x3E 80 保活测试 |
| `tests/.../FlashPanelViewModelTests.cs` | 新增文件解析/属性面板测试 |
| `tests/.../DatabaseTests.cs` | 新增 Did/Routine/Dtc Database.Clear() 测试 |
| `tests/.../OdxImportServiceTests.cs` | 新增二次导入清空测试 |

---

## 十、验证计划

### 10.1 单元测试

| 测试 | 验证 |
|---|---|
| `ParseIntelHex_MultiSegment_File_Yields_Two_Segments` | HEX 解析正确拆分非连续区域 |
| `ParseIntelHex_Continuous_Records_Merge_To_One_Segment` | 连续地址合并 |
| `ParseS19_S1_Records_Parse_Address_Data` | S19 解析 |
| `ParseRawBinary_Yields_Single_Segment_With_Zero_Address` | raw binary 单 Segment |
| `Segment_Crc32_Auto_Calculated` | CRC32 自动计算 |
| `VerifyStep_Mismatched_Crc_Throws` | 校验失败检测 |
| `EraseStep_With_StartAddress_And_Size` | 擦除范围传递 |
| `PropertyPanel_Shows_Only_Relevant_Fields_Per_Kind` | 属性面板按 Kind 过滤 |
| `DidDatabase_Clear_Preserves_BuiltIn_Defaults` | 清 ODX 导入但保留 VIN 等 |
| `OdxImport_SecondFile_Clears_FirstFile_Data` | 二次导入清空旧数据 |
| `CommunicationControl_DisableRxAndTx_Sends_0x28` | 0x28 服务发送正确（单参数 API）|
| `TesterPresent_Suppress_0x80_Sends_CorrectSubfunction` | 0x3E 80 抑制版 |
| `KeepAliveLoop_Runs_Parallel_During_Flash` | 后台保活任务与刷写并行 |
| `KeepAliveLoop_Stops_On_Cancellation` | 取消时保活任务退出 |
| `KeepAliveLoop_Falls_Back_To_0x3E00_If_Suppress_Unsupported` | 不支持 suppress 时回退 0x3E 00 |

### 10.2 手动 E2E

1. 选 `app.hex` → 工具显示 2 个 Segment（地址+大小+CRC）
2. 选中 SecurityAccess 步骤 → 右侧只显示 Level/Mode/Key/DLL，Level 可改
3. 选中 Download 步骤 → 右侧显示引用的 Segment 信息（地址自动）
4. 选中 Verify 步骤 → 右侧显示 Algorithm/ExpectedCRC（自动计算）
5. Start → 每个 Segment 执行 Erase+Download+Verify，刷写中 0x3E 80 保活
6. 加载第二个 ODX → 第一个 ODX 数据被清空
7. 查看 StatusMessage 显示 CRC 比对结果

---

## 十一、Out of Scope（明确推迟）

| 项 | 原因 |
|---|---|
| ODX 刷写配置桥接（IFlashConfigurationProvider） | 需要 ODX 解析扩展，Phase 2 可选 |
| 步骤拖拽排序 | 超出 Phase 2 范围 |
| 步骤复制/粘贴 | 超出 Phase 2 范围 |
| 在线刷写日志导出 | 超出 Phase 2 范围 |
| 多 ECU 并行刷写 | 超出 Phase 2 范围 |
| IsFlashing 线程 marshaling（WPF 已处理）| 非阻塞，后续优化 |

---

## 十二、与 Phase 1.1 的关系

Phase 1.1 已完成的功能（多 DownloadTransfer 文件浏览、Profile 保存加载）**保留并扩展**：

| Phase 1.1 | Phase 2 处理 |
|---|---|
| 多 DownloadTransfer 行 | 保留，但每行改为引用 Segment |
| 文件浏览（.bin） | 扩展为 .hex/.s19/.bin |
| Profile Save/Load | 保留，序列化新模型 |
| 步骤增删 | 保留，UI 改为左侧列表 |
| 属性面板 | 新增，替代统一 DataGrid |
| ODX 导入 | 修复清空 bug |
| SecurityAccess | Level 可配置 + 多级解锁 |
