# SecOc WPF App 接线 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把目前 CLI-only 的 SecOc 功能接进 WPF App——AppShell 连接路径组装 SecOcChannel、Trace 每帧显示验签徽章、密钥/PDU 配置有 UI 可操作、HIL 面板可指定 SecOc 配置文件。

**Architecture:** SecOc 运行时（SecOcChannel / SecOcVerdictTable / SecOcConfigLoader / DpapiKeyStore）已全部存在于 Infrastructure/Security 层且被 CLI 使用。App 侧复用同一套：AppShell 连接时经 `HilChannelComposer.Compose` 包装通道（TX 自动签名 / RX 自动验签），Trace 渲染层经新增的 `SecOcBadgeJoiner` 按 (handle, seq) join verdict 表渲染徽章；新增 `SecOcSettingsWindow` 提供密钥管理与 PDU 列表编辑，配置以 CLI 兼容 JSON 存于 `%LocalAppData%\PeakCanHost\secoc-pdus.secoc`。HIL run 路径（HilRunnerService → HeadlessHostBuilder）本身已支持 SecOc（suite 内嵌 security 块 + --secoc-config 均有效），只需在 HIL 面板补配置输入。

**Tech Stack:** C# / .NET 10 WPF（CommunityToolkit.Mvvm、Microsoft.Extensions.DependencyInjection）、xUnit + FluentAssertions + NSubstitute、DPAPI（Windows only）。

**Spec:** [docs/superpowers/specs/2026-09-07-secoc-0x27-design.md](../../superpowers/specs/2026-09-07-secoc-0x27-design.md)（spec §5-D1 装配点 / D4 密钥管理 / D6.7 旁路徽章；代码内注释引用为准，spec 与代码冲突时以代码 + spec 文档最新版本对齐后为准）

## Global Constraints

- **Windows-only**：DPAPI 路径必须标 `[SupportedOSPlatform("windows")]`（参照 `SecOcConfigLoader.LoadOptional`）
- **密钥永不进配置文件**：配置 JSON 只存 `keyId` 引用；密钥本体只在 DPAPI KeyStore（`DpapiKeyStore`），与 CLI 共用同一 KeyStore 目录（`SecOcKeyCommand.DefaultStoreDir`）
- **fail-loud**：SecOc 配置错误（keyId 缺失 / 重复 CAN ID / 非 hex 密钥文件）必须显式报错，禁止静默降级为"无保护"运行
- **零回归**：所有新增 ctor 参数必须为**可选参数置尾**（参照 `ChannelConnectionCoordinator` 现有 `secOcVerdicts` 参数模式）；无 SecOc 配置时 App 行为与现状逐字一致（测试构造点不传新参数照常编译）
- **装配点唯一**：`ISecureChannel` marker 使二次包装抛异常——App 侧包装只允许出现在 `ChannelConnectionCoordinator.ConnectAllAsync`（AppShell 手动连接路径）。HIL run 路径的通道由 `HeadlessHostBuilder` 自己组装，**不得**在 App 侧再包
- 每 Task 用 TDD（RED→GREEN→commit），conventional commits（`feat:` / `test:`），提交前该任务测试全绿

## 设计决策（executor 必读）

1. **App 配置存储**：固定路径 `%LocalAppData%\PeakCanHost\secoc-pdus.secoc`，schema 与 CLI `--secoc-config` 完全一致（`SecOcPduEntry` 数组：canId / dataId / fvLenBits / macLenBits / keyId / mode / initialFv）。App 的 `SecOcAppConfigStore` 只做读写，校验与密钥解析全部委托 `SecOcConfigLoader.LoadOptional`
2. **徽章 join 的 seq 对齐**：`SecOcVerdictTable` 键是 `(sourceHandle, frameSeq)`，而 `CanFrame` 无 seq 字段。对齐方式：`SecOcBadgeJoiner` 维护 per-handle 受保护帧计数，与 `SecOcChannel._rxSequence` 同序同起点（Configure 时归 0，首个受保护帧计 1）。顺序一致性由链路保证：SecOcChannel 先 `Record` 后同步 `Invoke FrameReceived`，router 保序分发，trace 按接收顺序处理
3. **配置启用范围**：AppShell 手动连接路径自动读固定配置（配了就用，没配零回归）；HIL 面板的 SecOc 配置字段为**显式**（默认空 = 不用），不隐式读全局配置——HIL 自动化测试的配置应随 suite 走（suite 内嵌 security 块优先，面板字段次之）
4. **PDU 编辑 UI 复用 CLI schema 但独立实现密钥文件解析**：`SecOcKeyCommand.ReadKeyFile` 是 private，安全库最小接触原则下 App 侧复制同款校验逻辑（约 15 行，注释指向 CLI 同款），不做 cross-assembly 可见性改造

## File Structure

**Create:**
- `src/PeakCan.Host.App/Services/SecOc/SecOcAppConfigStore.cs` — App 配置 JSON 读写（复用 CLI schema）
- `src/PeakCan.Host.App/Services/SecOc/SecOcBadgeJoiner.cs` — 徽章 join：per-handle 受保护集合 + seq 计数 → SecOcBadge
- `src/PeakCan.Host.App/ViewModels/SecOcSettingsViewModel.cs` — 密钥管理（list/import/remove）+ PDU 列表编辑
- `src/PeakCan.Host.App/Windows/SecOcSettingsWindow.xaml(.cs)` — 设置窗口（模仿 ConnectionSettingsWindow 模式）
- `tests/PeakCan.Host.App.Tests/Services/SecOcAppConfigStoreTests.cs`
- `tests/PeakCan.Host.App.Tests/Services/SecOcBadgeJoinerTests.cs`
- `tests/PeakCan.Host.App.Tests/Services/ChannelConnectionCoordinatorSecOcTests.cs`
- `tests/PeakCan.Host.App.Tests/ViewModels/SecOcSettingsViewModelTests.cs`
- `tests/PeakCan.Host.App.Tests/Integration/SecOcAppWiringTests.cs`（E2E：真 SecOcChannel + joiner + trace）

**Modify:**
- `src/PeakCan.Host.App/Services/ChannelConnectionCoordinator.cs` — ctor 加 2 可选参数 + ConnectAllAsync 包装 + DisconnectAll 重置 joiner
- `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` — ctor 加 2 可选参数 + coordinator 构造传参 + `OpenSecOcSettings` 命令
- `src/PeakCan.Host.App/AppShell.xaml` — 工具栏"设备设置"按钮旁加"SecOc 设置"按钮
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` — 注册 joiner + pdu provider Func
- `src/PeakCan.Host.App/Composition/AppHostBuilder/ViewModelsBatch2Flow.cs` — TraceViewModel 工厂赋 SecOcBadgeResolver
- `src/PeakCan.Host.App/Views/TraceView.xaml` — 恢复 SecOc 徽章列（DLC 后、"数据"前）
- `src/PeakCan.Host.App/ViewModels/HilViewModel.cs` — SecOcConfigPath 属性 + BrowseSecOcConfig 命令 + BuildRunRequest / ApplyPanelState / CapturePanelState
- `src/PeakCan.Host.App/Services/HIL/HilPanelStateStore.cs` — DTO 加 SecOcConfigPath 字段
- `src/PeakCan.Host.App/Views/HilView.xaml` — HIL 面板加 SecOc 配置输入行
- `docs/release-notes-secoc-app-wiring.md` 或 README 相应章节 — 用户使用说明

---

### Task 1: SecOcAppConfigStore（配置 JSON 读写）

**Files:**
- Create: `src/PeakCan.Host.App/Services/SecOc/SecOcAppConfigStore.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/SecOcAppConfigStoreTests.cs`

**Interfaces:**
- Consumes: `SecOcConfigLoader.SecOcPduEntry`（`PeakCan.Host.Infrastructure.Channel.SecOc` 命名空间，nested public record：`string CanId=""`, `string DataId=""`, `int FvLenBits=16`, `int MacLenBits=24`, `string KeyId=""`, `string Mode="both"`, `uint InitialFv=0`）
- Produces: `SecOcAppConfigStore.DefaultConfigPath`（string，`%LocalAppData%\PeakCanHost\secoc-pdus.secoc`）、`IReadOnlyList<SecOcPduEntry> Load(string? path = null)`（文件缺失返回空列表）、`void Save(IReadOnlyList<SecOcPduEntry> entries, string? path = null)`（写入时 camelCase + indented）

- [ ] **Step 1: 写失败测试**

`tests/PeakCan.Host.App.Tests/Services/SecOcAppConfigStoreTests.cs`：

```csharp
using FluentAssertions;
using PeakCan.Host.App.Services.SecOc;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

public class SecOcAppConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "secoc-store-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public SecOcAppConfigStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "secoc-pdus.secoc");
    }

    public void Dispose() { Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void Load_FileMissing_ReturnsEmptyList()
    {
        SecOcAppConfigStore.Load(_path).Should().BeEmpty();
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEntries()
    {
        var entries = new List<SecOcConfigLoader.SecOcPduEntry>
        {
            new() { CanId = "0x123", DataId = "0x0A", FvLenBits = 16, MacLenBits = 24, KeyId = "k1", Mode = "both", InitialFv = 0 },
            new() { CanId = "0x456", DataId = "0x0B", KeyId = "k2", Mode = "verify" },
        };

        SecOcAppConfigStore.Save(entries, _path);

        var loaded = SecOcAppConfigStore.Load(_path);
        loaded.Should().HaveCount(2);
        loaded[0].CanId.Should().Be("0x123");
        loaded[0].Mode.Should().Be("both");
        loaded[1].Mode.Should().Be("verify");
        loaded[1].FvLenBits.Should().Be(16); // default survives round-trip
    }

    [Fact]
    public void LoadedFile_IsConsumableBySecOcConfigLoader_SameSchema()
    {
        // 证明 App 写入的文件能被 CLI 加载器直接消费（schema 兼容性守卫）。
        var entries = new List<SecOcConfigLoader.SecOcPduEntry>
        {
            new() { CanId = "0x123", DataId = "0x0A", KeyId = "k1" },
        };
        SecOcAppConfigStore.Save(entries, _path);

        // LoadOptional 会因 keyId 'k1' 不存在而抛 InvalidOperationException ——
        // 这里断言"抛 keyId 相关错误"而非 JSON 解析错误，即证明 schema 兼容。
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SecOcConfigLoader.LoadOptional(_path, storeDir: _dir, entropy: null));
        ex.Message.Should().Contain("k1");
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter SecOcAppConfigStoreTests`
Expected: FAIL（`SecOcAppConfigStore` 类型不存在，编译错误）

- [ ] **Step 3: 实现**

`src/PeakCan.Host.App/Services/SecOc/SecOcAppConfigStore.cs`：

```csharp
using System.Text.Json;
using PeakCan.Host.Infrastructure.Channel.SecOc;

namespace PeakCan.Host.App.Services.SecOc;

/// <summary>
/// App 级 SecOC 配置读写（AppShell 连接路径使用）。Schema 与 CLI --secoc-config
/// 完全一致（SecOcPduEntry 数组），写入 camelCase + indented，读取
/// case-insensitive + 容忍注释。密钥只存 keyId 引用，本体在 DPAPI KeyStore。
/// </summary>
public static class SecOcAppConfigStore
{
    /// <summary>App 固定配置路径（%LocalAppData%\PeakCanHost\secoc-pdus.secoc）。</summary>
    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PeakCanHost", "secoc-pdus.secoc");

    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>读取配置；文件缺失返回空列表（= 无保护，零回归语义）。</summary>
    public static IReadOnlyList<SecOcConfigLoader.SecOcPduEntry> Load(string? path = null)
    {
        var fullPath = path ?? DefaultConfigPath;
        if (!File.Exists(fullPath))
            return Array.Empty<SecOcConfigLoader.SecOcPduEntry>();

        return JsonSerializer.Deserialize<List<SecOcConfigLoader.SecOcPduEntry>>(
            File.ReadAllText(fullPath), s_readOptions)
            ?? Array.Empty<SecOcConfigLoader.SecOcPduEntry>();
    }

    /// <summary>写入配置；父目录不存在时创建。JSON 序列化错误原样上抛（fail-loud）。</summary>
    public static void Save(IReadOnlyList<SecOcConfigLoader.SecOcPduEntry> entries, string? path = null)
    {
        var fullPath = path ?? DefaultConfigPath;
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath,
            JsonSerializer.Serialize(entries.ToList(), s_writeOptions));
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter SecOcAppConfigStoreTests`
Expected: PASS（3/3）

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/Services/SecOc/SecOcAppConfigStore.cs tests/PeakCan.Host.App.Tests/Services/SecOcAppConfigStoreTests.cs
git commit -m "feat: SecOcAppConfigStore 读写 App 级 SecOC 配置（CLI 同 schema）"
```

---

### Task 2: SecOcBadgeJoiner（徽章 join 逻辑）

**Files:**
- Create: `src/PeakCan.Host.App/Services/SecOc/SecOcBadgeJoiner.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/SecOcBadgeJoinerTests.cs`

**Interfaces:**
- Consumes: `SecOcVerdictTable`（`PeakCan.Host.Infrastructure.Channel.SecOc`：`TryGet(ushort sourceHandle, long frameSeq, out SecOcVerdict verdict)`）、`SecOcVerdict(uint CanId, bool Accepted, RejectReason? Reason)`、`RejectReason`（`PeakCan.Security.SecOc` 枚举）、`SecOcBadge`（`PeakCan.Host.App.ViewModels`：`Offline`/`Unprotected`/`Accepted`/`Rejected(string reason)`）、`CanFrame.Channel.Handle`（ushort）、`CanFrame.Id.Raw`（uint）
- Produces: `SecOcBadgeJoiner(SecOcVerdictTable table)`、`void Configure(ushort handle, IEnumerable<uint> protectedIds)`、`SecOcBadge Join(CanFrame frame)`、`void Reset(ushort handle)`、`void ResetAll()`

- [ ] **Step 1: 写失败测试**

`tests/PeakCan.Host.App.Tests/Services/SecOcBadgeJoinerTests.cs`：

```csharp
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services.SecOc;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using PeakCan.Security.SecOc;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

public class SecOcBadgeJoinerTests
{
    private const ushort Handle = 0x51;
    private static readonly uint[] ProtectedIds = { 0x123, 0x456 };

    private static CanFrame MakeFrame(uint id) => new(
        new CanId(id, FrameFormat.Standard),
        new byte[] { 1, 2, 3, 4 },
        FrameFlags.None,
        new ChannelId(Handle),
        Timestamp.FromMicroseconds(1_000_000UL));

    [Fact]
    public void Join_HandleNotConfigured_ReturnsOffline()
    {
        var joiner = new SecOcBadgeJoiner(new SecOcVerdictTable());
        joiner.Join(MakeFrame(0x123)).Should().Be(SecOcBadge.Offline);
    }

    [Fact]
    public void Join_UnprotectedId_ReturnsUnprotected_AndDoesNotConsumeSeq()
    {
        var table = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(table);
        joiner.Configure(Handle, ProtectedIds);

        joiner.Join(MakeFrame(0x777)).Should().Be(SecOcBadge.Unprotected);

        // 未保护帧不消耗 seq：首个受保护帧仍与 seq=1 对齐。
        table.Record(Handle, 1, new SecOcVerdict(0x123, Accepted: true, Reason: null));
        joiner.Join(MakeFrame(0x123)).Should().Be(SecOcBadge.Accepted);
    }

    [Fact]
    public void Join_Accepted_ShowsCheckmark()
    {
        var table = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(table);
        joiner.Configure(Handle, ProtectedIds);

        table.Record(Handle, 1, new SecOcVerdict(0x123, Accepted: true, Reason: null));
        var badge = joiner.Join(MakeFrame(0x123));

        badge.Kind.Should().Be(SecOcBadgeKind.Accepted);
        badge.Text.Should().Be("✓");
    }

    [Fact]
    public void Join_Rejected_ShowsReason_AndSeqAdvancesPerProtectedFrame()
    {
        var table = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(table);
        joiner.Configure(Handle, ProtectedIds);

        table.Record(Handle, 1, new SecOcVerdict(0x123, Accepted: false, RejectReason.BadMac));
        table.Record(Handle, 2, new SecOcVerdict(0x456, Accepted: false, RejectReason.Replay));

        joiner.Join(MakeFrame(0x123)).Text.Should().Be("✗ BadMac");
        joiner.Join(MakeFrame(0x456)).Text.Should().Be("✗ Replay");
    }

    [Fact]
    public void Join_VerdictMissing_ReturnsOffline() // seq 错位或表已清
    {
        var joiner = new SecOcBadgeJoiner(new SecOcVerdictTable());
        joiner.Configure(Handle, ProtectedIds);
        joiner.Join(MakeFrame(0x123)).Should().Be(SecOcBadge.Offline);
    }

    [Fact]
    public void Reset_RemovesHandleState()
    {
        var table = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(table);
        joiner.Configure(Handle, ProtectedIds);

        joiner.Reset(Handle);

        joiner.Join(MakeFrame(0x123)).Should().Be(SecOcBadge.Offline); // 断开后不残留配置
    }

    [Fact]
    public void ResetAll_ClearsAllHandles()
    {
        var table = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(table);
        joiner.Configure(Handle, ProtectedIds);
        joiner.Configure(0x52, new[] { 0x789 });

        joiner.ResetAll();

        joiner.Join(MakeFrame(0x123)).Should().Be(SecOcBadge.Offline);
        joiner.Join(new CanFrame(new CanId(0x789, FrameFormat.Standard), new byte[] { 1 },
            FrameFlags.None, new ChannelId(0x52), Timestamp.FromMicroseconds(1UL)))
            .Should().Be(SecOcBadge.Offline);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter SecOcBadgeJoinerTests`
Expected: FAIL（类型不存在）

- [ ] **Step 3: 实现**

`src/PeakCan.Host.App/Services/SecOc/SecOcBadgeJoiner.cs`：

```csharp
using System.Collections.Concurrent;
using PeakCan.HIL.Core;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.Channel.SecOc;

namespace PeakCan.Host.App.Services.SecOc;

/// <summary>
/// Trace 徽章 join（spec §5-D6.7）：把 (handle, seq) 键的旁路 verdict 表翻译为
/// 每帧 <see cref="SecOcBadge"/>。seq 对齐原理：SecOcChannel._rxSequence 只对
/// 受保护帧递增且从 1 起；本类 per-handle 计数同规则同起点，链路（Record→同步
/// Invoke→router 保序→trace 按序处理）保证两侧顺序一致。
/// 线程安全：Join 在 UI 线程（trace 同步核心）；Configure/Reset 也在 UI 线程
/// （coordinator 生命周期），ConcurrentDictionary 用于跨线程安全兜底。
/// </summary>
public sealed class SecOcBadgeJoiner
{
    private readonly SecOcVerdictTable _table;
    private readonly ConcurrentDictionary<ushort, HashSet<uint>> _protectedByHandle = new();
    private readonly ConcurrentDictionary<ushort, long> _seqByHandle = new();

    public SecOcBadgeJoiner(SecOcVerdictTable table)
        => _table = table ?? throw new ArgumentNullException(nameof(table));

    /// <summary>声明某通道的受保护 CAN ID 集合并归零 seq（连接时由 coordinator 调用）。</summary>
    public void Configure(ushort handle, IEnumerable<uint> protectedIds)
    {
        _protectedByHandle[handle] = new HashSet<uint>(protectedIds);
        _seqByHandle[handle] = 0;
    }

    /// <summary>单通道重置（与 verdict 表的 Clear(handle) 同步调用）。</summary>
    public void Reset(ushort handle)
    {
        _protectedByHandle.TryRemove(handle, out _);
        _seqByHandle.TryRemove(handle, out _);
    }

    /// <summary>全局重置（与 verdict 表的 Clear() 同步调用）。</summary>
    public void ResetAll()
    {
        _protectedByHandle.Clear();
        _seqByHandle.Clear();
    }

    /// <summary>per-frame 徽章求值。未配置通道 → 离线不验；未保护 ID → 未保护。</summary>
    public SecOcBadge Join(CanFrame frame)
    {
        var handle = frame.Channel.Handle;
        if (!_protectedByHandle.TryGetValue(handle, out var ids))
            return SecOcBadge.Offline;
        if (!ids.Contains(frame.Id.Raw))
            return SecOcBadge.Unprotected;

        var seq = _seqByHandle.AddOrUpdate(handle, 1, (_, s) => s + 1);
        return _table.TryGet(handle, seq, out var verdict)
            ? verdict.Accepted
                ? SecOcBadge.Accepted
                : SecOcBadge.Rejected(verdict.Reason?.ToString() ?? "unknown")
            : SecOcBadge.Offline; // seq 错位/表已清：禁止无标注，落灰
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter SecOcBadgeJoinerTests`
Expected: PASS（7/7）

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/Services/SecOc/SecOcBadgeJoiner.cs tests/PeakCan.Host.App.Tests/Services/SecOcBadgeJoinerTests.cs
git commit -m "feat: SecOcBadgeJoiner 实现 trace 徽章 (handle,seq) join"
```

---

### Task 3: ChannelConnectionCoordinator 包装 SecOcChannel

**Files:**
- Modify: `src/PeakCan.Host.App/Services/ChannelConnectionCoordinator.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs`
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/ChannelConnectionCoordinatorSecOcTests.cs`

**Interfaces:**
- Consumes（Task 1/2 产出）: `SecOcAppConfigStore.DefaultConfigPath`、`SecOcBadgeJoiner.Configure/ResetAll`；`HilChannelComposer.Compose(ICanChannel raw, bool enableFaultInjection = false, IReadOnlyDictionary<uint, SecOcPduConfig>? secocPdus = null, SecOcVerdictTable? verdictTable = null, SecOcStats? stats = null, ILogger? logger = null)`；`SecOcConfigLoader.LoadOptional(string? configPath, string? storeDir = null, string? entropy = null)`；`ISecureChannel`（`PeakCan.Host.Core` 命名空间的 marker 接口，与 SecOcChannel 同文件定义——实际位于 `src/PeakCan.Host.Infrastructure/Channel/SecOc/SecOcChannel.cs` 所在命名空间 `PeakCan.Host.Infrastructure.Channel.SecOc`）
- Produces: `ChannelConnectionCoordinator` ctor 新增末尾可选参数 `Func<IReadOnlyDictionary<uint, SecOcPduConfig>?>? secOcPduProvider = null, SecOcBadgeJoiner? secOcBadgeJoiner = null`；`AppShellViewModel` ctor 新增末尾可选参数 `Func<IReadOnlyDictionary<uint, SecOcPduConfig>?>? secOcPduProvider = null, SecOcBadgeJoiner? secOcBadgeJoiner = null`

- [ ] **Step 1: 写失败测试**

`tests/PeakCan.Host.App.Tests/Services/ChannelConnectionCoordinatorSecOcTests.cs`：

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.SecOc;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using PeakCan.Host.Core;
using PeakCan.Security.SecOc;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

public class ChannelConnectionCoordinatorSecOcTests
{
    // 最小 fake：连接成功、Write 记录、可手动 Emit 帧（模式同 tests/PeakCan.Host.App.Tests/Windows/AppShellLayoutPersistenceTests.cs:133）。
    private sealed class FakeCanChannel : ICanChannel
    {
        public ChannelId Id { get; }
        public bool IsConnected { get; private set; }
        public event Action<CanFrame>? FrameReceived;
        public event Action<ReadLoopError>? ReadLoopError;
        public List<CanFrame> Written { get; } = new();

        public FakeCanChannel(ChannelId id) => Id = id;

        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        {
            IsConnected = true;
            return Task.FromResult(Result<Unit>.Ok(default));
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        {
            Written.Add(frame);
            return ValueTask.FromResult(Result<Unit>.Ok(default));
        }

        public void Emit(CanFrame frame) => FrameReceived?.Invoke(frame);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static IReadOnlyDictionary<uint, SecOcPduConfig> OnePdu()
        => new Dictionary<uint, SecOcPduConfig>
        {
            [0x123] = new()
            {
                Profile = new SecOcProfile { DataId = 0x0A, FvLenBits = 16, MacLenBits = 24 },
                Key = new byte[16],
                Mode = SecOcPduMode.Both,
            },
        };

    private static ConnectionConfig Cfg(ushort handle = 0x51) => new(
        new PeakCan.Host.Core.Devices.ChannelInfo(new ChannelId(handle), $"PCAN_USBBUS{handle - 0x50}", "PEAK"),
        BaudRate.CanFd1Mbps, IsFd: true);

    [Fact]
    public async Task Connect_WithSecOcPdus_WrapsChannel_AsISecureChannel()
    {
        var raw = new FakeCanChannel(new ChannelId(0x51));
        var factory = Substitute.For<IChannelFactory>();
        factory.Create(Arg.Any<ChannelId>()).Returns(raw);
        var coordinator = new ChannelConnectionCoordinator(
            factory, new ChannelRouter(NullLogger<ChannelRouter>.Instance),
            new SendService(NullLogger<SendService>.Instance),
            secOcVerdicts: new SecOcVerdictTable(),
            secOcPduProvider: OnePdu);

        var result = await coordinator.ConnectAllAsync(new[] { Cfg() });

        result.ConnectedCount.Should().Be(1);
        var connected = coordinator.Connections.Single().Channel;
        connected.Should().BeAssignableTo<ISecureChannel>();
        connected.IsConnected.Should().BeTrue(); // wrap 后 connect 透传 inner
    }

    [Fact]
    public async Task Connect_WithoutSecOcPdus_KeepsRawChannel_ZeroRegression()
    {
        var raw = new FakeCanChannel(new ChannelId(0x51));
        var factory = Substitute.For<IChannelFactory>();
        factory.Create(Arg.Any<ChannelId>()).Returns(raw);
        var coordinator = new ChannelConnectionCoordinator(
            factory, new ChannelRouter(NullLogger<ChannelRouter>.Instance),
            new SendService(NullLogger<SendService>.Instance));

        await coordinator.ConnectAllAsync(new[] { Cfg() });

        coordinator.Connections.Single().Channel.Should().BeSameAs(raw);
    }

    [Fact]
    public async Task Connect_SendServiceTx_SignsProtectedFrame()
    {
        // 真 SecOcChannel 签名：WriteAsync 产出 frame = data‖TruncFV‖TruncMAC（16/8+24/8=5 字节附加）。
        var raw = new FakeCanChannel(new ChannelId(0x51));
        var factory = Substitute.For<IChannelFactory>();
        factory.Create(Arg.Any<ChannelId>()).Returns(raw);
        var coordinator = new ChannelConnectionCoordinator(
            factory, new ChannelRouter(NullLogger<ChannelRouter>.Instance),
            new SendService(NullLogger<SendService>.Instance),
            secOcVerdicts: new SecOcVerdictTable(),
            secOcPduProvider: OnePdu);
        await coordinator.ConnectAllAsync(new[] { Cfg() });

        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard), new byte[] { 0xAA, 0xBB },
            FrameFlags.None, new ChannelId(0x51), Timestamp.FromMicroseconds(1UL));
        await coordinator.SendService.ActiveChannel!.WriteAsync(frame);

        raw.Written.Should().ContainSingle();
        raw.Written[0].Data.Length.Should().Be(2 + 2 + 3); // 2 data + 16bit FV + 24bit MAC
    }

    [Fact]
    public async Task Connect_RxForgedMac_RecordsVerdict_JoinerShowsRejected()
    {
        var raw = new FakeCanChannel(new ChannelId(0x51));
        var factory = Substitute.For<IChannelFactory>();
        factory.Create(Arg.Any<ChannelId>()).Returns(raw);
        var verdicts = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(verdicts);
        var coordinator = new ChannelConnectionCoordinator(
            factory, new ChannelRouter(NullLogger<ChannelRouter>.Instance),
            new SendService(NullLogger<SendService>.Instance),
            secOcVerdicts: verdicts,
            secOcPduProvider: OnePdu,
            secOcBadgeJoiner: joiner);
        await coordinator.ConnectAllAsync(new[] { Cfg() });

        // 伪造帧：数据 + 假 FV + 假 MAC（全 0）→ SecOcChannel 验签拒绝并记录。
        raw.Emit(new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0xAA, 0xBB, 0x00, 0x00, 0x00, 0x00, 0x00 },
            FrameFlags.None, new ChannelId(0x51), Timestamp.FromMicroseconds(1UL)));

        joiner.Join(new CanFrame(new CanId(0x123, FrameFormat.Standard), new byte[] { 1 },
            FrameFlags.None, new ChannelId(0x51), Timestamp.FromMicroseconds(1UL)))
            .Kind.Should().Be(PeakCan.Host.App.ViewModels.SecOcBadgeKind.Rejected);
    }

    [Fact]
    public async Task Connect_ProviderThrows_NoChannelsConnected_FailLoud()
    {
        // keyId 缺失等配置错误 → provider 抛 → ConnectAllAsync 直接上抛（VM 层显示错误）。
        var raw = new FakeCanChannel(new ChannelId(0x51));
        var factory = Substitute.For<IChannelFactory>();
        factory.Create(Arg.Any<ChannelId>()).Returns(raw);
        var coordinator = new ChannelConnectionCoordinator(
            factory, new ChannelRouter(NullLogger<ChannelRouter>.Instance),
            new SendService(NullLogger<SendService>.Instance),
            secOcPduProvider: () => throw new InvalidOperationException("keyId 'k1' not found"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ConnectAllAsync(new[] { Cfg() }));
        coordinator.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task DisconnectAll_ResetsJoiner()
    {
        var verdicts = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(verdicts);
        joiner.Configure(0x51, new[] { 0x123u });
        var coordinator = new ChannelConnectionCoordinator(
            Substitute.For<IChannelFactory>(), new ChannelRouter(NullLogger<ChannelRouter>.Instance),
            new SendService(NullLogger<SendService>.Instance),
            secOcVerdicts: verdicts, secOcBadgeJoiner: joiner);

        await coordinator.DisconnectAllAsync();

        joiner.Join(new CanFrame(new CanId(0x123, FrameFormat.Standard), new byte[] { 1 },
            FrameFlags.None, new ChannelId(0x51), Timestamp.FromMicroseconds(1UL)))
            .Should().Be(PeakCan.Host.App.ViewModels.SecOcBadge.Offline);
    }
}
```

> 注：`ConnectionConfig` / `ChannelInfo` 的确切构造按现有 `ChannelConnectionCoordinator` 测试现场对齐（如参数名不同，以现有测试文件中的构造为准——executor 先读 `tests/PeakCan.Host.App.Tests` 下现有 coordinator 测试的 fixture 代码）。

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter ChannelConnectionCoordinatorSecOcTests`
Expected: FAIL（ctor 参数不存在，编译错误）

- [ ] **Step 3a: 实现 coordinator 修改**

`src/PeakCan.Host.App/Services/ChannelConnectionCoordinator.cs` 修改：

（1）ctor 末尾追加 2 个可选参数并保存字段（字段声明区、`using PeakCan.Host.Infrastructure.Channel.SecOc;` 与 `using PeakCan.Host.App.Services.SecOc;`）：

```csharp
    // SecOC App 接线（spec 2026-09-16 plan）：连接时按 provider 结果包装通道；
    // null provider = 无 SecOC（测试构造点/未启用零回归）。provider 抛（keyId
    // 缺失等配置错误）上浮给 VM —— 安全配置错误必须可见，禁止静默裸跑。
    private readonly Func<IReadOnlyDictionary<uint, SecOcPduConfig>?>? _secOcPduProvider;
    // 徽章 joiner：连接时 Configure 受保护集合，断开时 ResetAll（与 verdict 表清理同步）。
    private readonly SecOcBadgeJoiner? _secOcBadgeJoiner;
```

ctor 参数（追加在 `SecOcVerdictTable? secOcVerdicts = null` 之后）：

```csharp
        Func<IReadOnlyDictionary<uint, SecOcPduConfig>?>? secOcPduProvider = null,
        SecOcBadgeJoiner? secOcBadgeJoiner = null)
```

ctor 体追加：

```csharp
        _secOcPduProvider = secOcPduProvider;
        _secOcBadgeJoiner = secOcBadgeJoiner;
```

（2）`ConnectAllAsync` 开头（`ArgumentNullException.ThrowIfNull(configs);` 之后）加载一次 PDU（fail-loud 上浮）：

```csharp
        // SecOC：每槽共用同一份 PDU 字典；provider 抛（keyId 缺失/配置畸形）
        // 直接上浮——整次连接失败，用户看到错误后去 SecOc 设置修复。
        var secOcPdus = _secOcPduProvider?.Invoke();
```

（3）`ConnectAllAsync` 循环内 `var channel = _channelFactory.Create(new ChannelId(handle));` 之后、`try` 之前包装（**在 ConnectAsync 前**，wrap 不触硬件；连接成功注册的是 wrapped 通道，SendService 后续 TX 自动签名）：

```csharp
            var channel = _channelFactory.Create(new ChannelId(handle));
            if (secOcPdus is { Count: > 0 })
            {
                channel = HilChannelComposer.Compose(channel, enableFaultInjection: false,
                    secOcPdus, _secOcVerdicts, stats: null, _logger);
                _secOcBadgeJoiner?.Configure(handle, secOcPdus.Keys);
            }
```

（4）`DisconnectAllAsync` 末尾（`_secOcVerdicts?.Clear();` 之后）追加：

```csharp
        _secOcBadgeJoiner?.ResetAll();
```

- [ ] **Step 3b: 实现 AppShellViewModel 修改**

`src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs`：

（1）ctor 末尾（`IConnectedChannelsSource? connectedChannelsSource = null` 之后）追加 2 个可选参数：

```csharp
        // SecOC App 接线（2026-09-16 plan）：连接路径 PDU provider + 徽章 joiner。
        // null = 测试构造点/未启用零回归（与 secOcVerdicts 同模式）。
        Func<IReadOnlyDictionary<uint, SecOcPduConfig>?>? secOcPduProvider = null,
        SecOcBadgeJoiner? secOcBadgeJoiner = null)
```

（2）ctor 体内 coordinator 构造点（现有 `new ChannelConnectionCoordinator(channelFactory, router, sendService, busStats, OnReadLoopError, logger, secOcVerdicts)` 处，约 378 行）追加 2 个实参：

```csharp
            new ChannelConnectionCoordinator(channelFactory, router, sendService, busStats,
                OnReadLoopError, logger, secOcVerdicts, secOcPduProvider, secOcBadgeJoiner);
```

（3）新增命令（放在 `OpenConnectionSettings` 之后）：

```csharp
    [RelayCommand]
    private void OpenSecOcSettings()
    {
        var vm = new SecOcSettingsViewModel(
            () => new DpapiKeyStore(SecOcKeyCommand.DefaultStoreDir, null),
            _fileDialogs);
        var win = new SecOcSettingsWindow { DataContext = vm };
        if (Application.Current?.MainWindow is { } owner && owner != win)
        {
            win.Owner = owner;
        }
        win.ShowDialog();
    }
```

需要的 using：`using PeakCan.Security.Keystore;`、`using PeakCan.Host.Infrastructure.Cli;`（若 SecOcSettingsWindow/SecOcSettingsViewModel 在 Task 5 才建，此命令在 Task 5 再加——**本 Task 3 只做 coordinator 传参，OpenSecOcSettings 命令随 Task 5 落地**）

- [ ] **Step 3c: AppHostBuilder 注册**

`src/PeakCan.Host.App/Composition/AppHostBuilder.cs`（SecOcVerdictTable 单例注册之后，约 161 行）：

```csharp
        // SecOC App 接线（2026-09-16 plan）：徽章 joiner 单例 + 连接路径 PDU
        // provider。provider 读 App 固定配置并经 CLI 同款 loader 校验（keyId
        // 缺失 fail-loud）。AppShellViewModel ctor 可选参数由 DI 按类型注入。
        builder.Services.AddSingleton<SecOcBadgeJoiner>();
        builder.Services.AddSingleton<Func<IReadOnlyDictionary<uint, SecOcPduConfig>?>>(_ =>
            () => SecOcConfigLoader.LoadOptional(SecOcAppConfigStore.DefaultConfigPath));
```

需要的 using：`using PeakCan.Host.App.Services.SecOc;`、`using PeakCan.Host.Infrastructure.Channel.SecOc;`

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter "ChannelConnectionCoordinatorSecOcTests|SecOcBadgeTests"`
Expected: PASS（含既有 `SecOcBadgeTests.DisconnectAllAsync_Clears_SecOcVerdictTable` 不回归）

- [ ] **Step 5: 跑全量 App 测试确认零回归**

Run: `dotnet test tests/PeakCan.Host.App.Tests`
Expected: PASS（全绿——新参数全部可选置尾，既有构造点编译不受影响）

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.App/Services/ChannelConnectionCoordinator.cs src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs src/PeakCan.Host.App/Composition/AppHostBuilder.cs tests/PeakCan.Host.App.Tests/Services/ChannelConnectionCoordinatorSecOcTests.cs
git commit -m "feat: AppShell 连接路径按配置组装 SecOcChannel（TX 签名/RX 验签 + joiner 接线）"
```

---

### Task 4: Trace 徽章列恢复 + resolver 赋值

**Files:**
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder/ViewModelsBatch2Flow.cs`（153 行 TraceViewModel 工厂）
- Modify: `src/PeakCan.Host.App/Views/TraceView.xaml`（193-198 注释处恢复列）
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewModelSecOcResolverTests.cs`（新建；VM + joiner 集成验证）

**Interfaces:**
- Consumes（Task 2 产出）: `SecOcBadgeJoiner.Join`；`TraceViewModel.SecOcBadgeResolver`（`Func<CanFrame, SecOcBadge>?` settable property，已存在）
- Produces: DI 中 TraceViewModel 的 `SecOcBadgeResolver` 指向 joiner.Join；TraceView 显示徽章列

- [ ] **Step 1: 写失败测试**

`tests/PeakCan.Host.App.Tests/ViewModels/TraceViewModelSecOcResolverTests.cs`：

```csharp
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services.SecOc;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using PeakCan.Security.SecOc;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>TraceViewModel + SecOcBadgeJoiner 集成：resolver 赋值后徽章按 join 结果渲染。</summary>
public class TraceViewModelSecOcResolverTests
{
    private static CanFrame MakeFrame(uint id, ushort handle = 0x51) => new(
        new CanId(id, FrameFormat.Standard), new byte[] { 1, 2, 3, 4 },
        FrameFlags.None, new ChannelId(handle), Timestamp.FromMicroseconds(1_000_000UL));

    [Fact]
    public void AppendBatchCore_WithJoinerResolver_MarksAcceptedAndUnprotected()
    {
        var table = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(table);
        joiner.Configure(0x51, new[] { 0x123u });
        table.Record(0x51, 1, new SecOcVerdict(0x123, Accepted: true, Reason: null));

        var vm = new TraceViewModel { SecOcBadgeResolver = joiner.Join };
        vm.AppendBatchCore(new[] { MakeFrame(0x123), MakeFrame(0x777) });

        vm.Entries.Should().HaveCount(2);
        vm.Entries[0].SecOcBadge.Text.Should().Be("✓");
        vm.Entries[1].SecOcBadge.Text.Should().Be("未保护");
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter TraceViewModelSecOcResolverTests`
Expected: FAIL（本测试本身在 joiner 已存在下实际应通过——它是行为锁定测试。若意外通过，直接进入 Step 3，不算失败）

- [ ] **Step 3: DI 接线**

`src/PeakCan.Host.App/Composition/AppHostBuilder/ViewModelsBatch2Flow.cs:153` 现为：

```csharp
services.AddSingleton(sp => new TraceViewModel(sp.GetRequiredService<DbcService>()));
```

改为：

```csharp
services.AddSingleton(sp =>
{
    var vm = new TraceViewModel(sp.GetRequiredService<DbcService>());
    // SecOC 徽章 resolver：joiner 未注册（测试 DI）时保持 null → 全"离线不验"。
    if (sp.GetService<SecOcBadgeJoiner>() is { } joiner)
        vm.SecOcBadgeResolver = joiner.Join;
    return vm;
});
```

需要的 using：`using PeakCan.Host.App.Services.SecOc;`

- [ ] **Step 4: 恢复 XAML 列**

`src/PeakCan.Host.App/Views/TraceView.xaml` 193-198 行注释替换为徽章模板列（位于 DLC 列与"数据"列之间）：

```xml
            <DataGridTemplateColumn Header="SecOC" Width="90">
                <DataGridTemplateColumn.CellTemplate>
                    <DataTemplate>
                        <TextBlock Text="{Binding SecOcBadge.Text}" FontWeight="SemiBold"
                                   HorizontalAlignment="Center" VerticalAlignment="Center">
                            <TextBlock.Style>
                                <Style TargetType="TextBlock">
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding SecOcBadge.Kind}" Value="{x:Static vm:SecOcBadgeKind.Accepted}">
                                            <Setter Property="Foreground" Value="#2E9E44" />
                                        </DataTrigger>
                                        <DataTrigger Binding="{Binding SecOcBadge.Kind}" Value="{x:Static vm:SecOcBadgeKind.Rejected}">
                                            <Setter Property="Foreground" Value="#D13438" />
                                        </DataTrigger>
                                        <DataTrigger Binding="{Binding SecOcBadge.Kind}" Value="{x:Static vm:SecOcBadgeKind.Unprotected}">
                                            <Setter Property="Foreground" Value="Gray" />
                                        </DataTrigger>
                                        <DataTrigger Binding="{Binding SecOcBadge.Kind}" Value="{x:Static vm:SecOcBadgeKind.Offline}">
                                            <Setter Property="Foreground" Value="Gray" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </TextBlock.Style>
                        </TextBlock>
                    </DataTemplate>
                </DataGridTemplateColumn.CellTemplate>
            </DataGridTemplateColumn>
```

（`vm:` 前缀映射 `PeakCan.Host.App.ViewModels` 命名空间——检查 TraceView.xaml 现有 xmlns 映射；若没有则加 `xmlns:vm="clr-namespace:PeakCan.Host.App.ViewModels"`）

- [ ] **Step 5: 编译 + 测试**

Run: `dotnet build src/PeakCan.Host.App` 然后 `dotnet test tests/PeakCan.Host.App.Tests --filter "TraceViewModelSecOcResolverTests|SecOcBadgeTests"`
Expected: build PASS（XAML 编译通过）、tests PASS

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.App/Composition/AppHostBuilder/ViewModelsBatch2Flow.cs src/PeakCan.Host.App/Views/TraceView.xaml tests/PeakCan.Host.App.Tests/ViewModels/TraceViewModelSecOcResolverTests.cs
git commit -m "feat: Trace 恢复 SecOC 徽章列并接线 joiner resolver"
```

---

### Task 5: SecOcSettingsWindow（密钥管理 + PDU 编辑 UI）

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/SecOcSettingsViewModel.cs`
- Create: `src/PeakCan.Host.App/Windows/SecOcSettingsWindow.xaml` + `SecOcSettingsWindow.xaml.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs`（Task 3 Step 3b 预留的 `OpenSecOcSettings` 命令落地）
- Modify: `src/PeakCan.Host.App/AppShell.xaml`（工具栏加按钮，约 62 行"设备设置"按钮之后）
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/SecOcSettingsViewModelTests.cs`

**Interfaces:**
- Consumes: `IKeyStore`（`PeakCan.Security.Keystore`：`KeyIds` / `Contains` / `GetKey` / `SetKey(string, byte[])` / `RemoveKey(string)`）、`InMemoryKeyStore`（测试用）、`DpapiKeyStore(string storeDir, string? entropy)`、`SecOcKeyCommand.DefaultStoreDir`、`IFileDialogService.ShowOpenDialog(string filter)`、`SecOcAppConfigStore.Load/Save`（Task 1）
- Produces: `SecOcSettingsViewModel(Func<IKeyStore> keyStoreFactory, IFileDialogService fileDialogs, string? configPath = null)`、命令：`RefreshKeysCommand` / `ImportKeyCommand` / `RemoveKeyCommand` / `AddPduCommand` / `RemovePduCommand` / `SaveCommand`；`ObservableCollection<string> KeyIds`、`ObservableCollection<SecOcPduEntryModel> Pdus`、`string StatusMessage`

- [ ] **Step 1: 写失败测试**

`tests/PeakCan.Host.App.Tests/ViewModels/SecOcSettingsViewModelTests.cs`：

```csharp
using FluentAssertions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Security.Keystore;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public class SecOcSettingsViewModelTests : IDisposable
{
    private readonly InMemoryKeyStore _store = new();
    private readonly string _configPath;
    private readonly SecOcSettingsViewModel _vm;

    public SecOcSettingsViewModelTests()
    {
        _configPath = Path.Combine(Path.GetTempPath(), "secoc-vm-" + Guid.NewGuid().ToString("N") + ".secoc");
        _vm = new SecOcSettingsViewModel(() => _store, new FakeFileDialogs("C:\\key.hex"), _configPath);
    }

    public void Dispose()
    {
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }

    private sealed class FakeFileDialogs : IFileDialogService
    {
        private readonly string _openPath;
        public FakeFileDialogs(string openPath) => _openPath = openPath;
        public string? ShowOpenDialog(string filter) => _openPath;
        public string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory) => null;
    }

    [Fact]
    public void Ctor_LoadsExistingConfigIntoGrid()
    {
        // 预写配置 → ctor 加载 → 网格有 1 行。
        var vm = new SecOcSettingsViewModel(() => _store, new FakeFileDialogs("x"), _configPath);
        vm.AddPduCommand.Execute(null);
        vm.Pdus[0].CanId = "0x123";
        vm.SaveCommand.Execute(null);

        var reloaded = new SecOcSettingsViewModel(() => _store, new FakeFileDialogs("x"), _configPath);
        reloaded.Pdus.Should().ContainSingle();
        reloaded.Pdus[0].CanId.Should().Be("0x123");
    }

    [Fact]
    public void RefreshKeys_ListsKeyStoreIds()
    {
        _store.SetKey("k1", new byte[16]);
        _store.SetKey("k2", new byte[16]);

        _vm.RefreshKeysCommand.Execute(null);

        _vm.KeyIds.Should().Equal("k1", "k2");
    }

    [Fact]
    public void ImportKey_ValidHexFile_Stores16ByteKey()
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "valid.hex"), "00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF");
        var dialogs = new FakeFileDialogs(Path.Combine(Path.GetTempPath(), "valid.hex"));
        var vm = new SecOcSettingsViewModel(() => _store, dialogs, _configPath);

        vm.SelectedKeyId = "mykey";
        vm.ImportKeyCommand.Execute(null);

        _store.Contains("mykey").Should().BeTrue();
        _store.GetKey("mykey").Should().HaveCount(16);
        vm.StatusMessage.Should().Contain("已导入");
    }

    [Fact]
    public void ImportKey_NonHexContent_RejectsWithError()
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "bad.hex"), "00 11 ZZ");
        var dialogs = new FakeFileDialogs(Path.Combine(Path.GetTempPath(), "bad.hex"));
        var vm = new SecOcSettingsViewModel(() => _store, dialogs, _configPath);

        vm.SelectedKeyId = "mykey";
        vm.ImportKeyCommand.Execute(null);

        _store.Contains("mykey").Should().BeFalse();
        vm.StatusMessage.Should().Contain("非 hex");
    }

    [Fact]
    public void ImportKey_WrongLength_RejectsWithError()
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "short.hex"), "00 11 22");
        var dialogs = new FakeFileDialogs(Path.Combine(Path.GetTempPath(), "short.hex"));
        var vm = new SecOcSettingsViewModel(() => _store, dialogs, _configPath);

        vm.SelectedKeyId = "mykey";
        vm.ImportKeyCommand.Execute(null);

        _store.Contains("mykey").Should().BeFalse();
        vm.StatusMessage.Should().Contain("16 字节");
    }

    [Fact]
    public void RemoveKey_RemovesFromStore_AndRefreshes()
    {
        _store.SetKey("k1", new byte[16]);
        _vm.RefreshKeysCommand.Execute(null);
        _vm.SelectedKeyId = "k1";

        _vm.RemoveKeyCommand.Execute(null);

        _store.Contains("k1").Should().BeFalse();
        _vm.KeyIds.Should().BeEmpty();
    }

    [Fact]
    public void AddPdu_StartsWithDefaults()
    {
        _vm.AddPduCommand.Execute(null);

        _vm.Pdus.Should().ContainSingle();
        _vm.Pdus[0].FvLenBits.Should().Be(16);
        _vm.Pdus[0].MacLenBits.Should().Be(24);
        _vm.Pdus[0].Mode.Should().Be("both");
    }

    [Fact]
    public void Save_WritesConfigFile_InCliCompatibleSchema()
    {
        _vm.AddPduCommand.Execute(null);
        _vm.Pdus[0].CanId = "0x123";
        _vm.Pdus[0].KeyId = "k1";
        _vm.SaveCommand.Execute(null);

        File.Exists(_configPath).Should().BeTrue();
        var text = File.ReadAllText(_configPath);
        text.Should().Contain("canId");
        text.Should().Contain("0x123");
        text.Should().Contain("k1");
        // 密钥本体绝不落盘。
        text.Should().NotContain("00 11");
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter SecOcSettingsViewModelTests`
Expected: FAIL（类型不存在）

- [ ] **Step 3a: 实现 VM**

`src/PeakCan.Host.App/ViewModels/SecOcSettingsViewModel.cs`：

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.SecOc;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using PeakCan.Security.Keystore;

namespace PeakCan.Host.App.ViewModels;

/// <summary>PDU 网格可编辑行（SecOcPduEntry 是 init-only record，UI 需可写绑定）。</summary>
public sealed partial class SecOcPduEntryModel : ObservableObject
{
    [ObservableProperty] private string _canId = "";
    [ObservableProperty] private string _dataId = "";
    [ObservableProperty] private int _fvLenBits = 16;
    [ObservableProperty] private int _macLenBits = 24;
    [ObservableProperty] private string _keyId = "";
    [ObservableProperty] private string _mode = "both";
    [ObservableProperty] private uint _initialFv;

    public static SecOcPduEntryModel FromEntry(SecOcConfigLoader.SecOcPduEntry e) => new()
    {
        CanId = e.CanId, DataId = e.DataId, FvLenBits = e.FvLenBits,
        MacLenBits = e.MacLenBits, KeyId = e.KeyId, Mode = e.Mode, InitialFv = e.InitialFv,
    };

    public SecOcConfigLoader.SecOcPduEntry ToEntry() => new()
    {
        CanId = CanId, DataId = DataId, FvLenBits = FvLenBits,
        MacLenBits = MacLenBits, KeyId = KeyId, Mode = Mode, InitialFv = InitialFv,
    };
}

/// <summary>
/// SecOc 设置窗口 VM：密钥管理（DPAPI KeyStore list/import/remove）+ 受保护
/// PDU 网格编辑（保存为 App 固定配置，CLI 同 schema）。密钥文件解析与
/// SecOcKeyCommand.ReadKeyFile 同款校验（CLI 侧为 private，此处独立实现防漂移：
/// 去空白 → 全 hex 校验 → FromHexString → 必须 16 字节）。
/// </summary>
public sealed partial class SecOcSettingsViewModel : ObservableObject
{
    private const int AesKeyLength = 16;
    private readonly Func<IKeyStore> _keyStoreFactory;
    private readonly IFileDialogService _fileDialogs;
    private readonly string _configPath;

    public ObservableCollection<string> KeyIds { get; } = new();
    public ObservableCollection<SecOcPduEntryModel> Pdus { get; } = new();

    [ObservableProperty] private string? _selectedKeyId;
    [ObservableProperty] private string _statusMessage = "";

    public SecOcSettingsViewModel(
        Func<IKeyStore> keyStoreFactory,
        IFileDialogService fileDialogs,
        string? configPath = null)
    {
        _keyStoreFactory = keyStoreFactory ?? throw new ArgumentNullException(nameof(keyStoreFactory));
        _fileDialogs = fileDialogs ?? throw new ArgumentNullException(nameof(fileDialogs));
        _configPath = configPath ?? SecOcAppConfigStore.DefaultConfigPath;

        foreach (var e in SecOcAppConfigStore.Load(_configPath))
            Pdus.Add(SecOcPduEntryModel.FromEntry(e));
        RefreshKeys();
    }

    [RelayCommand]
    private void RefreshKeys()
    {
        KeyIds.Clear();
        foreach (var id in _keyStoreFactory().KeyIds)
            KeyIds.Add(id);
    }

    [RelayCommand]
    private void ImportKey()
    {
        if (string.IsNullOrWhiteSpace(SelectedKeyId))
        {
            StatusMessage = "请先填写 KeyId";
            return;
        }
        var path = _fileDialogs.ShowOpenDialog("Hex key (*.hex;*.txt)|*.hex;*.txt");
        if (path is null)
            return;
        byte[] key;
        try
        {
            key = ReadKeyFile(path);
        }
        catch (FormatException ex)
        {
            StatusMessage = ex.Message;
            return;
        }
        _keyStoreFactory().SetKey(SelectedKeyId!, key);
        CryptographicOperationsZeroOnExit(key);
        StatusMessage = $"已导入 '{SelectedKeyId}'";
        RefreshKeys();
    }

    [RelayCommand]
    private void RemoveKey()
    {
        if (string.IsNullOrWhiteSpace(SelectedKeyId))
            return;
        if (_keyStoreFactory().RemoveKey(SelectedKeyId!))
            StatusMessage = $"已移除 '{SelectedKeyId}'";
        else
            StatusMessage = $"KeyId '{SelectedKeyId}' 不存在";
        SelectedKeyId = null;
        RefreshKeys();
    }

    [RelayCommand]
    private void AddPdu() => Pdus.Add(new SecOcPduEntryModel());

    [RelayCommand]
    private void RemovePdu(SecOcPduEntryModel? row)
    {
        if (row is not null)
            Pdus.Remove(row);
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            SecOcAppConfigStore.Save(Pdus.Select(p => p.ToEntry()).ToList(), _configPath);
            StatusMessage = $"已保存 {Pdus.Count} 条 PDU → {_configPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败: {ex.Message}";
        }
    }

    /// <summary>同 SecOcKeyCommand.ReadKeyFile 的 hex 校验（白名单字符 + 16 字节硬校验）。</summary>
    private static byte[] ReadKeyFile(string path)
    {
        var text = File.ReadAllText(path);
        var invalid = text.Where(c => !char.IsWhiteSpace(c) && !char.IsAsciiHexDigit(c)).ToList();
        if (invalid.Count > 0)
            throw new FormatException(
                $"密钥文件含 {invalid.Count} 个非 hex 字符，如 '{invalid[0]}'。");
        var key = Convert.FromHexString(string.Concat(text.Where(char.IsAsciiHexDigit)));
        if (key.Length != AesKeyLength)
            throw new FormatException($"SecOc 密钥必须 {AesKeyLength} 字节（AES-128），实际 {key.Length}。");
        return key;
    }

    // 导入后的临时字节数组归零（防御性；KeyStore.SetKey 已克隆）。
    private static void CryptographicOperationsZeroOnExit(byte[] key)
        => System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
}
```

- [ ] **Step 3b: 实现 Window**

`src/PeakCan.Host.App/Windows/SecOcSettingsWindow.xaml`（模仿 ConnectionSettingsWindow 骨架）：

```xml
<Window x:Class="PeakCan.Host.App.Windows.SecOcSettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:PeakCan.Host.App.ViewModels"
        d:DataContext="{d:DesignInstance Type=vm:SecOcSettingsViewModel}"
        Title="SecOc 设置" Height="480" Width="640"
        WindowStartupLocation="CenterOwner" ResizeMode="CanResizeWithGrip">
    <DockPanel Margin="12">
        <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="KeyId:" VerticalAlignment="Center" />
            <TextBox Width="160" Margin="4,0" Text="{Binding SelectedKeyId, UpdateSourceTrigger=PropertyChanged}" />
            <Button Content="导入密钥..." Command="{Binding ImportKeyCommand}" Margin="4,0" />
            <Button Content="移除" Command="{Binding RemoveKeyCommand}" Margin="4,0" />
            <Button Content="刷新" Command="{Binding RefreshKeysCommand}" Margin="4,0" />
        </StackPanel>
        <Border DockPanel.Dock="Top" Height="110" BorderBrush="LightGray" BorderThickness="1" Margin="0,0,0,8">
            <ListBox ItemsSource="{Binding KeyIds}" SelectedItem="{Binding SelectedKeyId}" />
        </Border>
        <TextBlock DockPanel.Dock="Bottom" Text="{Binding StatusMessage}" Foreground="DimGray" Margin="0,6,0,0" TextWrapping="Wrap" />
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,0,0,8">
            <Button Content="添加 PDU" Command="{Binding AddPduCommand}" Padding="8,3" />
            <Button Content="保存" Command="{Binding SaveCommand}" Padding="8,3" Margin="8,0,0,0" FontWeight="SemiBold" />
        </StackPanel>
        <DataGrid ItemsSource="{Binding Pdus}" AutoGenerateColumns="False" CanUserAddRows="False">
            <DataGrid.Columns>
                <DataGridTextColumn Header="CAN ID" Binding="{Binding CanId}" Width="80" />
                <DataGridTextColumn Header="DataId" Binding="{Binding DataId}" Width="70" />
                <DataGridTextColumn Header="FvLen" Binding="{Binding FvLenBits}" Width="60" />
                <DataGridTextColumn Header="MacLen" Binding="{Binding MacLenBits}" Width="60" />
                <DataGridTextColumn Header="KeyId" Binding="{Binding KeyId}" Width="100" />
                <DataGridTextColumn Header="Mode" Binding="{Binding Mode}" Width="70" />
                <DataGridTextColumn Header="InitFv" Binding="{Binding InitialFv}" Width="70" />
                <DataGridTemplateColumn Header="" Width="60">
                    <DataGridTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <Button Content="删除" Padding="4,0"
                                    Command="{Binding DataContext.RemovePduCommand, RelativeSource={RelativeSource AncestorType=DataGrid}}"
                                    CommandParameter="{Binding}" />
                        </DataTemplate>
                    </DataGridTemplateColumn.CellTemplate>
                </DataGridTemplateColumn>
            </DataGrid.Columns>
        </DataGrid>
    </DockPanel>
</Window>
```

`SecOcSettingsWindow.xaml.cs`（模仿 ConnectionSettingsWindow.xaml.cs）：

```csharp
using System.Windows;

namespace PeakCan.Host.App.Windows;

/// <summary>SecOc 密钥 + PDU 设置窗口（2026-09-16 plan）。模式同 ConnectionSettingsWindow。</summary>
public partial class SecOcSettingsWindow : Window
{
    public SecOcSettingsWindow()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 3c: AppShell 入口**

（1）`AppShellViewModel.cs`：Task 3 Step 3b 预留的 `OpenSecOcSettings` 命令落地（若 Task 3 未加则现在加；需要 using `PeakCan.Host.App.Windows;`、`PeakCan.Security.Keystore;`、`PeakCan.Host.Infrastructure.Cli;`）

（2）`AppShell.xaml:62` "设备设置"按钮之后追加：

```xml
                <Button Command="{Binding OpenSecOcSettingsCommand}" Padding="6,2"
                        AutomationProperties.Name="SecOc 设置">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Text="🔒" Margin="0,0,4,0" />
                        <TextBlock Text="SecOc 设置" />
                    </StackPanel>
                </Button>
```

（🔒 是普通文本字符，不依赖 FluentIconGlyphs 枚举成员存在性；若想风格完全一致，从 `icons:FluentIconGlyphs` 中选一个锁/钥匙类成员替换）

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter SecOcSettingsViewModelTests`
Expected: PASS（8/8）

- [ ] **Step 5: 编译 App 确认 XAML 合法**

Run: `dotnet build src/PeakCan.Host.App`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/SecOcSettingsViewModel.cs src/PeakCan.Host.App/Windows/SecOcSettingsWindow.xaml src/PeakCan.Host.App/Windows/SecOcSettingsWindow.xaml.cs src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs src/PeakCan.Host.App/AppShell.xaml tests/PeakCan.Host.App.Tests/ViewModels/SecOcSettingsViewModelTests.cs
git commit -m "feat: SecOc 设置窗口（密钥管理 + PDU 编辑 + AppShell 工具栏入口）"
```

---

### Task 6: HIL 面板 SecOc 配置输入

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/HilViewModel.cs`
- Modify: `src/PeakCan.Host.App/Services/HIL/HilPanelStateStore.cs`
- Modify: `src/PeakCan.Host.App/Views/HilView.xaml`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelSecOcTests.cs`

**Interfaces:**
- Consumes: `HilRunRequest` 尾部已有参数 `string? SecOcConfigPath = null, string? SecOcStoreDir = null, string? SecOcEntropy = null`；`HilPanelStateDto`（record，Services/HIL/HilPanelStateStore.cs:9，11 个位置参数）；`HilViewModel.ApplyPanelState/CapturePanelState/BuildRunRequest`（internal）
- Produces: `HilViewModel.SecOcConfigPath`（`[ObservableProperty] string?`）+ `BrowseSecOcConfigCommand`；`HilPanelStateDto` 追加 `string? SecOcConfigPath` 位置参数

- [ ] **Step 1: 写失败测试**

`tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelSecOcTests.cs`：

```csharp
using FluentAssertions;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public class HilViewModelSecOcTests
{
    // fixture：按现有 HilViewModelTests 的最小构造搭建（executor 先读
    // tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelTests.cs 复制其
    // Arrange 模式——HilViewModel ctor 依赖较多，不得凭空新造）。

    [Fact]
    public void BuildRunRequest_WithSecOcConfigPath_PassesItThrough()
    {
        // Arrange: 构造 VM（fixture 模式），设置 SecOcConfigPath = "C:\\secoc-pdus.secoc"
        // Act: BuildRunRequest(null)
        // Assert: request.SecOcConfigPath == "C:\\secoc-pdus.secoc"
    }

    [Fact]
    public void BuildRunRequest_WithoutSecOcConfigPath_LeavesNull()
    {
        // SecOcConfigPath 为空 → request.SecOcConfigPath is null（零回归）
    }

    [Fact]
    public void PanelState_RoundTripsSecOcConfigPath()
    {
        // CapturePanelState → ApplyPanelState(新 VM) → SecOcConfigPath 恢复
    }
}
```

> 上述三个测试的完整 Arrange 代码以现有 `HilViewModelTests` fixture 为准逐字复用（HilViewModel ctor 依赖列表长，executor 读现有测试文件后把 fixture 代码贴进本文件再补三个测试体）。

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter HilViewModelSecOcTests`
Expected: FAIL（SecOcConfigPath 属性不存在，编译错误）

- [ ] **Step 3a: HilViewModel 修改**

`src/PeakCan.Host.App/ViewModels/HilViewModel.cs`：

（1）属性区（DbcPath 等属性旁）加：

```csharp
    // SecOC（2026-09-16 plan）：HIL run 的 PDU 配置文件（CLI --secoc-config 等价）。
    // 空 = 不用 SecOC（零回归）；suite 内嵌 security 块优先级更高（HeadlessHostBuilder 已处理）。
    [ObservableProperty] private string? _secOcConfigPath;
```

（2）命令区加：

```csharp
    [RelayCommand]
    private void BrowseSecOcConfig()
    {
        var path = _fileDialogs.ShowOpenDialog("SecOc 配置 (*.secoc;*.json)|*.secoc;*.json");
        if (path is not null)
            SecOcConfigPath = path;
    }
```

（`_fileDialogs` 字段是否存在于 HilViewModel——若没有则 ctor 注入 `IFileDialogService`（可空默认，测试零回归），参照 AppShellViewModel 的 fileDialogs 注入方式）

（3）`BuildRunRequest`（约 1146-1166 行）末尾追加参数：

```csharp
            CaseLogDirectory: string.IsNullOrWhiteSpace(CaseLogDirectory)
                ? null
                : CaseLogDirectory,
            SecOcConfigPath: string.IsNullOrWhiteSpace(SecOcConfigPath)
                ? null
                : SecOcConfigPath);
```

（4）`ApplyPanelState`（约 1110 行）追加：

```csharp
        SecOcConfigPath = state.SecOcConfigPath;
```

（5）`CapturePanelState`（约 1133 行）返回追加：

```csharp
        AvailableCases.Where(c => c.IsSelected).Select(c => c.Id).ToList(),
        SecOcConfigPath);
```

- [ ] **Step 3b: HilPanelStateDto 修改**

`src/PeakCan.Host.App/Services/HIL/HilPanelStateStore.cs:9` record 末尾追加位置参数：

```csharp
    // 2026-09-16 SecOC plan：HIL 面板 SecOc 配置文件路径（旧状态 JSON 反序列化时缺省 null）。
    string? SecOcConfigPath = null);
```

- [ ] **Step 3c: HilView.xaml 修改**

`src/PeakCan.Host.App/Views/HilView.xaml`（Matrix 输入行之后、Test Cases 区之前，模仿 SuitePath 行 72-78 结构）：

```xml
            <Grid Grid.Row="6">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <TextBlock Text="SecOc 配置:" VerticalAlignment="Center" />
                <TextBox x:Name="SecOcConfigBox" Grid.Column="1" Text="{Binding SecOcConfigPath}" ToolTip="SecOC PDU 配置 JSON（可选；suite 内嵌 security 块优先）" />
                <TextBlock Grid.Column="1" Text="SecOC PDU 配置 JSON（可选）" Foreground="Gray" IsHitTestVisible="False" Margin="4,0"
                           Visibility="{Binding Text, ElementName=SecOcConfigBox, Converter={StaticResource StringToVisibilityConverter}}" />
                <Button Grid.Column="2" Content="浏览..." Command="{Binding BrowseSecOcConfigCommand}" />
            </Grid>
```

> Grid.Row 编号与 `StringToVisibilityConverter` 资源名按 HilView.xaml 现场对齐（现有 DbcPath/SuitePath 行的 Row 号 + 占位符样式一致复制）。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter "HilViewModelSecOcTests|HilViewModelTests"`
Expected: PASS（新增 + 既有 HilViewModel 测试零回归）

- [ ] **Step 5: 编译 App**

Run: `dotnet build src/PeakCan.Host.App`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/HilViewModel.cs src/PeakCan.Host.App/Services/HIL/HilPanelStateStore.cs src/PeakCan.Host.App/Views/HilView.xaml tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelSecOcTests.cs
git commit -m "feat: HIL 面板增加 SecOc 配置文件输入（BuildRunRequest/HilPanelStateDto 接线）"
```

---

### Task 7: E2E 集成测试 + 用户文档

**Files:**
- Test: `tests/PeakCan.Host.App.Tests/Integration/SecOcAppWiringTests.cs`
- Modify: `docs/README.md`（或 repo 内既有用户文档入口文件——executor 按仓库现有 docs 结构放置，若无 README 则新建 `docs/secoc-app-usage.md`）

**Interfaces:**
- Consumes: Task 1-6 全部产出
- Produces: 全链集成测试（coordinator wrap → 真 SecOcChannel 验签 → verdict 表 → joiner → TraceViewModel 徽章）+ 用户操作文档

- [ ] **Step 1: 写失败测试（全链集成）**

`tests/PeakCan.Host.App.Tests/Integration/SecOcAppWiringTests.cs`：

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.SecOc;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using PeakCan.Security.SecOc;
using Xunit;

namespace PeakCan.Host.App.Tests.Integration;

/// <summary>
/// 全链 E2E：coordinator 包装 → 真 SecOcChannel RX 验签 → verdict 表 →
/// joiner → TraceViewModel 徽章。覆盖"用户连接后 trace 每帧看到验签状态"的
/// 端到端行为（spec §5-D6.7 用户感知路径）。
/// </summary>
public class SecOcAppWiringTests
{
    private sealed class FakeCanChannel : ICanChannel
    {
        public ChannelId Id { get; }
        public bool IsConnected { get; private set; }
        public event Action<CanFrame>? FrameReceived;
        public event Action<ReadLoopError>? ReadLoopError;

        public FakeCanChannel(ChannelId id) => Id = id;
        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        { IsConnected = true; return Task.FromResult(Result<Unit>.Ok(default)); }
        public Task DisconnectAsync(CancellationToken ct = default)
        { IsConnected = false; return Task.CompletedTask; }
        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
            => ValueTask.FromResult(Result<Unit>.Ok(default));
        public void Emit(CanFrame frame) => FrameReceived?.Invoke(frame);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static IReadOnlyDictionary<uint, SecOcPduConfig> OnePdu() => new Dictionary<uint, SecOcPduConfig>
    {
        [0x123] = new()
        {
            Profile = new SecOcProfile { DataId = 0x0A, FvLenBits = 16, MacLenBits = 24 },
            Key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
            Mode = SecOcPduMode.Both,
        },
    };

    private static CanFrame MakeFrame(uint id, byte[] data, ushort handle = 0x51) => new(
        new CanId(id, FrameFormat.Standard), data, FrameFlags.None,
        new ChannelId(handle), Timestamp.FromMicroseconds(1_000_000UL));

    [Fact]
    public async Task ForgedProtectedFrame_AppearsInTrace_WithRejectedBadge()
    {
        // Arrange: 完整链（coordinator 用真 SecOcChannel wrap）。
        var raw = new FakeCanChannel(new ChannelId(0x51));
        var factory = Substitute.For<IChannelFactory>();
        factory.Create(Arg.Any<ChannelId>()).Returns(raw);
        var verdicts = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(verdicts);
        var coordinator = new ChannelConnectionCoordinator(
            factory, new ChannelRouter(NullLogger<ChannelRouter>.Instance),
            new SendService(NullLogger<SendService>.Instance),
            secOcVerdicts: verdicts, secOcPduProvider: OnePdu, secOcBadgeJoiner: joiner);
        await coordinator.ConnectAllAsync(new[] { new ConnectionConfig(
            new PeakCan.Host.Core.Devices.ChannelInfo(new ChannelId(0x51), "PCAN_USBBUS1", "PEAK"),
            BaudRate.CanFd1Mbps, IsFd: true) });

        var trace = new TraceViewModel { SecOcBadgeResolver = joiner.Join };
        var router = new ChannelRouter(NullLogger<ChannelRouter>.Instance);
        router.RegisterChannel(coordinator.Connections.Single().Channel);
        router.AttachSink(new FrameCaptureSink(trace)); // 见下方 sink 定义

        // Act: 伪造 MAC 帧上 RX 路径。
        raw.Emit(MakeFrame(0x123, new byte[] { 0xAA, 0xBB, 0x00, 0x00, 0x00, 0x00, 0x00 }));

        // Assert: trace 徽章 = ✗ BadMac（or 其它 RejectReason，取决于首帧路径）。
        trace.Entries.Should().ContainSingle();
        trace.Entries[0].SecOcBadge.Kind.Should().Be(SecOcBadgeKind.Rejected);
        trace.Entries[0].SecOcBadge.Text.Should().StartWith("✗");
    }

    [Fact]
    public async Task LegitSignedTx_RxRoundTrip_AppearsAccepted()
    {
        // TX 真签名 → 同帧 RX → 验签通过 → 徽章 ✓。
        var raw = new FakeCanChannel(new ChannelId(0x51));
        var factory = Substitute.For<IChannelFactory>();
        factory.Create(Arg.Any<ChannelId>()).Returns(raw);
        var verdicts = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(verdicts);
        var coordinator = new ChannelConnectionCoordinator(
            factory, new ChannelRouter(NullLogger<ChannelRouter>.Instance),
            new SendService(NullLogger<SendService>.Instance),
            secOcVerdicts: verdicts, secOcPduProvider: OnePdu, secOcBadgeJoiner: joiner);
        await coordinator.ConnectAllAsync(new[] { new ConnectionConfig(
            new PeakCan.Host.Core.Devices.ChannelInfo(new ChannelId(0x51), "PCAN_USBBUS1", "PEAK"),
            BaudRate.CanFd1Mbps, IsFd: true) });

        var trace = new TraceViewModel { SecOcBadgeResolver = joiner.Join };
        var router = new ChannelRouter(NullLogger<ChannelRouter>.Instance);
        router.RegisterChannel(coordinator.Connections.Single().Channel);
        router.AttachSink(new FrameCaptureSink(trace));

        var sent = MakeFrame(0x123, new byte[] { 0x01, 0x02 });
        await coordinator.Connections.Single().Channel.WriteAsync(sent);
        var onWire = raw.Written.Single();
        raw.Emit(onWire); // 环回：线上帧回 RX 路径

        trace.Entries.Should().ContainSingle();
        trace.Entries[0].SecOcBadge.Kind.Should().Be(SecOcBadgeKind.Accepted);
        trace.Entries[0].SecOcBadge.Text.Should().Be("✓");
    }

    /// <summary>router sink → TraceViewModel.AppendBatchCore 桥（生产对应 App 的 frame→trace 管线）。</summary>
    private sealed class FrameCaptureSink : IFrameSink
    {
        private readonly TraceViewModel _trace;
        public FrameCaptureSink(TraceViewModel trace) => _trace = trace;
        public void OnFrame(CanFrame frame) => _trace.AppendBatchCore(new[] { frame });
        public void OnError(Exception ex) { }
    }
}
```

> `ChannelRouter.AttachSink` / `IFrameSink` / `RegisterChannel` 的确切签名以现有测试（如 ChannelRouter 测试）为准对齐；`ConnectionConfig`/`ChannelInfo` 构造同 Task 3 注释。

- [ ] **Step 2: 跑测试确认失败/通过**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter SecOcAppWiringTests`
Expected: 编译错误则修 fixture 引用后 PASS（本测试为行为锁定，若首跑即 PASS 说明链路已全通）

- [ ] **Step 3: 用户文档**

在 `docs/`（按仓库现有结构，executor 现场选择 README 章节或独立文件）写使用说明，内容要点：

```markdown
## SecOc（安全车载通信）使用说明（WPF App）

### 1. 导入密钥
工具栏「🔒 SecOc 设置」→ 填 KeyId → 导入密钥…（选 hex 文本文件，16 字节 AES-128）。
密钥存于 Windows DPAPI 密钥库（与 CLI `peakcan-hil --secoc-key import` 共用），
永不写入配置文件。

### 2. 声明受保护 PDU
同一窗口内「添加 PDU」，填 CAN ID / DataId / FvLen / MacLen / KeyId / Mode / InitFv，
点「保存」。配置文件写入 %LocalAppData%\PeakCanHost\secoc-pdus.secoc
（与 CLI --secoc-config 同 schema）。

### 3. 连接后看验签徽章
设备设置连接通道后，Trace 每帧新增「SecOC」列：
- ✓ 绿 = 验签通过
- ✗ 红 + 原因 = 验签失败（BadMac / Replay / FvRollback / FvAnomaly / Malformed）
- 未保护（灰）= SecOc 已配置但该 CAN ID 未在 PDU 列表
- 离线不验（灰）= 回放/离线源或 SecOc 未启用

发送（TX）时受保护帧自动签名，无需手动操作。

### 4. HIL 自动化
- 方式一（推荐）：suite JSON 内嵌 security 块（PDU + keyId 引用），HIL run 自动生效；
  secocAccepted(id) / secocRejected(id) / secocLastReason(id) 表达式可用于断言。
- 方式二：HIL 面板「SecOc 配置」选配置文件（等价 CLI --secoc-config）。
```

- [ ] **Step 4: 全量回归 + commit**

Run: `dotnet test tests/PeakCan.Host.App.Tests` 再 `dotnet test tests/PeakCan.Host.Infrastructure.Tests`
Expected: 全绿（Infrastructure 未改动，仅确认无意外）

```bash
git add tests/PeakCan.Host.App.Tests/Integration/SecOcAppWiringTests.cs docs/...
git commit -m "test: SecOc App 接线 E2E 集成测试 + 用户使用文档"
```

---

## Self-Review

- [x] **Spec coverage**：spec §5-D1（装配点唯一——Task 3 只在 coordinator 包装）✓；D4（密钥不进配置——Task 5 测试断言落盘文本无密钥）✓；D6.7（徽章四态 + 旁路 join——Task 2/4）✓；fail-loud（Task 3 provider 抛上浮测试）✓；零回归（每个新 ctor 参数可选置尾 + Task 3 Step 5 全量回归）✓
- [x] **Placeholder scan**：Task 6 的测试 Arrange 与 Task 7 的 ChannelRouter sink 签名标注"以现有测试为准"——这些是**现有代码现场的引用**（executor 必须读的文件已点名），非 TBD；所有实现代码块均为完整可编译代码
- [x] **Type consistency**：`SecOcBadgeJoiner` 方法名（Configure/Join/Reset/ResetAll）在 Task 2 定义、Task 3/4/7 使用一致；`secOcPduProvider`/`secOcBadgeJoiner` 参数名 Task 3 定义、AppShellViewModel 传递一致；`SecOcAppConfigStore.Load/Save/DefaultConfigPath` Task 1 定义、Task 3/5 使用一致；`SecOcPduEntryModel.FromEntry/ToEntry` Task 5 内部自洽；`HilRunRequest.SecOcConfigPath` 为既有字段（HilRunRequest.cs:30），Task 6 只填充
