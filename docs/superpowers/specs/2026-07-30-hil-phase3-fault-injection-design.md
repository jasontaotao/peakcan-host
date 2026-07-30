# HIL Phase 3.1 — 故障注入设计

> Sprint 3 (v3.63.0) 交付了 HIL 核心测试管道（JUnit XML、UDS assertions、WaitForFrame、BLF、WriteAsync loopback、AppShell UI）。
> Phase 3 补齐故障响应验证能力，分三个子项目推进：

| 子项目 | 内容 | 依赖 |
|---|---|---|
| **3.1 故障注入**（本文档） | 内容篡改 + 时序故障 | 无 |
| 3.2 ECU 模拟器 | 虚拟 ECU 节点 | 3.1 |
| 3.3 多 ECU 矩阵 | 跨 ECU 并行编排 | 3.1 + 3.2 |

Out of scope: LLM 辅助分析（Phase 5）、总线关闭 bus-off（需通道状态机，3.x 后续）。

---

## 1. 架构概览

```
TestCaseStepKind.InjectFault (新增)
        │
        ▼
InjectFaultStep record (BaseFrame + FaultDescriptor)
        │
        ▼
InjectFaultStepExecutor
   ├── 内容篡改: 修改 BaseFrame 数据
   │      bitFlip / byteReplace / constantValue / crcCorrupt / dlcTamper
   ├── 时序故障: 控制发送行为
   │      frameDelay / frameRepeat / frameDrop
   └── 发送: ctx.SendFrameAsync(modifiedFrame)
```

**核心决策**：零 `IAssertionContext` 接口改动。Executor 内部篡改后调现有 `SendFrameAsync`。

理由：与 `SendFrameStepExecutor` 对称；故障注入是测试作者的主动声明（非透明装饰），Step 模式最自然。

---

## 2. 新增类型

### 2.1 FaultType 枚举

```csharp
namespace PeakCan.Host.Core.HIL.StepParams;

public enum FaultType
{
    // 内容篡改（Phase 3.1-A）
    BitFlip,        // 翻转指定位
    ByteReplace,    // 替换单个字节
    ConstantValue,  // 整帧填充固定值
    CrcCorrupt,     // CRC 字段写错（计算正确值 +1）
    DlcTamper,      // 修改数据长度（截断或填充），模拟 DLC 不匹配
    
    // 时序故障（Phase 3.1-B）
    FrameDelay,     // 延迟发送
    FrameRepeat,    // 重复发送 N 次
    FrameDrop,      // 丢弃该帧（不发送）
}
```

### 2.2 FaultDescriptor record

```csharp
public record FaultDescriptor(
    FaultType Type,
    int? BitIndex = null,      // BitFlip
    int? ByteIndex = null,     // ByteReplace
    byte? Value = null,        // ByteReplace / ConstantValue
    int? Dlc = null,           // DlcTamper
    int? DelayMs = null,       // FrameDelay
    int? Count = null);        // FrameRepeat
```

### 2.3 InjectFaultStep record

```csharp
public record InjectFaultStep(
    CanId Id,
    byte[] Data,
    FrameFlags Flags,
    FaultDescriptor Fault) : StepParameters;
```

### 2.4 TestCaseStepKind 扩展

```csharp
public enum TestCaseStepKind
{
    // ... existing ...
    InjectFault,    // 新增
}
```

---

## 3. InjectFaultStepExecutor

```csharp
internal sealed class InjectFaultStepExecutor : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.InjectFault;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (InjectFaultStep)step.Parameters;
        
        // 时序故障: FrameDrop — 直接返回成功
        if (p.Fault.Type == FaultType.FrameDrop)
            return new StepResult(0, step.Kind, step.Label, StepStatus.Passed, "Frame dropped", null, null, 0);
        
        // 时序故障: FrameDelay — 延迟后继续
        if (p.Fault.Type == FaultType.FrameDelay && p.Fault.DelayMs > 0)
            await Task.Delay(p.Fault.DelayMs.Value, ct);
        
        // 内容篡改: 修改帧数据
        var modifiedData = ApplyContentFault(p.Data, p.Fault);
        
        var frame = new CanFrame(p.Id, modifiedData, p.Flags, default, default);
        
        // 时序故障: FrameRepeat — 循环发送
        if (p.Fault.Type == FrameRepeat && p.Fault.Count > 1)
        {
            for (int i = 0; i < p.Fault.Count; i++)
            {
                var result = await ctx.SendFrameAsync(frame, ct);
                if (!result.IsSuccess)
                    return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                        $"Repeat {i}/{p.Fault.Count} failed: {result.Error?.Message}", null, null, 0);
            }
            return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"Frame repeated {p.Fault.Count}x", null, null, 0);
        }
        
        // 单次发送
        var sendResult = await ctx.SendFrameAsync(frame, ct);
        return new StepResult(0, step.Kind, step.Label,
            sendResult.IsSuccess ? StepStatus.Passed : StepStatus.Failed,
            sendResult.IsSuccess ? "Fault injected" : sendResult.Error?.Message,
            null, null, 0);
    }
    
    private byte[] ApplyContentFault(byte[] data, FaultDescriptor fault) => fault.Type switch
    {
        FaultType.BitFlip => ApplyBitFlip(data, fault.BitIndex ?? 0),
        FaultType.ByteReplace => ApplyByteReplace(data, fault.ByteIndex ?? 0, fault.Value ?? 0),
        FaultType.ConstantValue => Enumerable.Repeat(fault.Value ?? 0, data.Length).ToArray(),
        FaultType.CrcCorrupt => ApplyCrcCorrupt(data),
        FaultType.DlcTamper => ResizeData(data, fault.Dlc ?? data.Length),
        _ => data
    };
}
```

### 3.1 内容篡改实现细节

| FaultType | 实现 | 参数校验 |
|---|---|---|
| BitFlip | `data[byteIdx] ^= (1 << bitInByte)` | BitIndex ∈ [0, 8*data.Length) |
| ByteReplace | `data[ByteIndex] = Value` | ByteIndex ∈ [0, data.Length) |
| ConstantValue | 全帧填充 Value | 无 |
| CrcCorrupt | 正确 CRC +1 写入最后 2 字节 | data.Length ≥ 2 |
| DlcTamper | 修改数据长度：Dlc < data.Length 截断，Dlc > data.Length 补零 | Dlc ∈ [0, 64] |

### 3.2 参数校验策略

- 越界参数 → StepResult.Failed（带明确错误信息），不抛异常
- `FrameDrop` 无任何参数校验
- `FrameDelay.DelayMs ≤ 0` 视为无延迟（不报错，直接跳过）

---

## 4. 断言复用

故障注入后的 ECU 响应验证复用现有断言 Step：

```json
{
  "name": "BMS 过压保护 — bit flip 故障",
  "steps": [
    { "kind": "SendFrame", "id": 0x301, "data": "00 00 9C 00 00 00 00 00" },
    { "kind": "InjectFault", "label": "翻转电压 MSB",
      "id": 0x301, "data": "00 00 9C 00 00 00 00 00",
      "fault": { "type": "bitFlip", "bitIndex": 23 } },
    { "kind": "WaitForFrame", "id": 0x302, "timeoutMs": 500 },
    { "kind": "AssertSignal", "signal": "BMS.FaultStatus", "expected": 2, "tolerance": 0 }
  ]
}
```

---

## 5. JSON 序列化

`TestCaseStepJsonConverter` 已支持 `StepParameters` 多态反序列化。新增 `InjectFaultStep` 需：

1. `StepParametersFactory.Create()` 添加 `TestCaseStepKind.InjectFault` 分支
2. 验证 converter 能正确反序列化 `fault` 嵌套对象
3. `FaultType` 枚举用 `[JsonStringEnumConverter]`（已有 `HILJsonOptions` 全局配置）

### 5.1 序列化示例

```json
{ "kind": "InjectFault", "label": "CRC 错误",
  "id": { "raw": 0x301, "format": "hex", "type": "standard" },
  "data": "00 00 00 00 00 00 00 00",
  "fault": { "type": "crcCorrupt" } }
```

---

## 6. DI 注册

`HeadlessHostBuilder`（硬件模式）和 `AppHostBuilder` 各添加：

```csharp
services.AddSingleton<IStepExecutor, InjectFaultStepExecutor>();
```

`InjectFaultStepExecutor` 无外部依赖（纯内存操作），无需额外服务注册。

---

## 7. 测试策略

| 测试类型 | 覆盖内容 |
|---|---|
| 单元测试 | 每种 FaultType 的篡改结果正确性 |
| 单元测试 | 参数越界返回 Failed 不抛异常 |
| 单元测试 | FrameDrop 不调 SendFrameAsync |
| 单元测试 | FrameRepeat 调用 N 次 SendFrameAsync |
| 单元测试 | FrameDelay 实际延迟 ≥ DelayMs |
| 集成测试 | 端到端：InjectFaultStep → AssertSignal 完整用例 |
| 序列化测试 | JSON ↔ InjectFaultStep 往返 |

---

## 8. 交付阶段

| 阶段 | 内容 | 工作量 |
|---|---|---|
| **Phase 3.1-A** | 内容篡改（5 种 FaultType） + 单元测试 | 小 |
| **Phase 3.1-B** | 时序故障（3 种 FaultType） + 单元测试 | 小 |
| **Phase 3.1-C** | 集成测试 + JSON 序列化验证 + 文档 | 小 |

---

## 9. 风险与缓解

| 风险 | 缓解 |
|---|---|
| CrcCorrupt 实现依赖 CAN CRC 算法细节 | 使用 `CanFrame` 现有 CRC 计算 +1 偏移，不手撸算法 |
| FrameRepeat 循环阻塞 | 每次循环检查 `ct`，支持取消 |
| DLC 篡改与 CAN FD 冲突 | DlcTamper 只改字段值，不做帧格式转换 |

---

## 10. 与 Sprint 3 的关系

- **复用**：`IStepExecutor` 接口、`TestCaseStepKind` 枚举、`StepParameters` 多态序列化、`IAssertionContext.SendFrameAsync`
- **不修改**：`TestSuiteEngine`、`IAssertionContext`、`HilRunRequest`、`HeadlessHostBuilder` DI 架构
- **扩展**：`TestCaseStepKind` +1 枚举值、`StepParametersFactory` +1 分支
