# Restbus M3: Template Generalization + Legacy Import + Trial Run Full Check

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement spec §14 M3 — 模板库持久化（%APPDATA%）+ "另存为模板" + 实例值剥离 + M2 遗留（Legacy node/EcuScript 导入 + TrialRunner 完整握手检查 + Studio suite save/load 接线）。

**Architecture:** M2 建立了 hil-core `Gbt27930ChargerTemplate` seed + studio EnvironmentTab 骨架。M3：(A) studio 新增 `RestbusTemplateLibrary`（%APPDATA% JSON、atomic write，仿 SequenceLibrary 模式），加载合并 seed + user templates；(B) EnvironmentNodeViewModel 加 "另存为模板" 命令（默认剥离 SignalOverrides，可选保留）；(C) `RestbusNodeImportService` 旧 NodeModel/EcuScript JSON → RestbusNode；(D) host `TrialRunner` 接 `IFrameReceivedSubscription` 实现完整握手超时诊断；(E) studio suite save/load 接入 Environment 字段。

**Tech Stack:** C# / .NET 10, WPF (studio), xUnit, System.Text.Json, ICanChannel.FrameReceived (host), DbcParser (hil-core)。

**Spec:** `D:\claude_proj2\peakcan-host\docs\superpowers\specs\2026-09-03-restbus-node-unification-design.md` (Draft v3)

## Global Constraints

- hil-core Core 层零 I/O（NetArchTest 红线不变）— TemplateLibrary 落 studio 层。
- 新增序列化字段一律可空默认。
- Conventional commits (feat/fix/chore)。
- 三铁律不变：模板是普通 UI 规则唯一来源；DBC 是几何事实源；环境是 suite 属性。
- 用户模板生成新的稳定 `templateId`（GUID 或 slug）；seed 模板 ID 不变。
- suite 保存时嵌入完整 RestbusNode 快照；执行期不读 %APPDATA%。

---

## File Structure

### studio (`PeakCan.Studio.App`)
```
Services/Environment/
├── RestbusTemplateLibrary.cs       — %APPDATA% JSON 持久化（atomic write）
└── RestbusNodeImportService.cs     — 旧 NodeModel / EcuScript JSON → RestbusNode
ViewModels/Environment/
├── EnvironmentTabViewModel.cs      — 扩展：加载 user templates + SaveAsTemplate + suite save/load
└── EnvironmentNodeViewModel.cs     — 扩展：SaveAsTemplate command + SignalOverrides strip
```

### host (`PeakCan.Host.Infrastructure`)
```
HIL/Environment/
└── TrialRunner.cs                  — 扩展：IFrameReceivedSubscription 接线 + timeout + diagnostic
```

### hil-core (`PeakCan.HIL.Core`)
```
Templates/
└── RestbusTemplateCatalog.cs       — seed 模板注册表（静态纯数据）
```

---
### Task 1: RestbusTemplateLibrary (%APPDATA% JSON 持久化)

**Files:**
- Create: `peakcan-studio/src/PeakCan.Studio.App/Services/Environment/RestbusTemplateLibrary.cs`
- Test: `peakcan-studio/tests/PeakCan.Studio.App.Tests/Environment/RestbusTemplateLibraryTests.cs`

**Interfaces:**
- Consumes: `RestbusNode` (hil-core), JSON serialization
- Produces: `RestbusTemplateLibrary.Load()` → `IReadOnlyList<RestbusNode>`; `.Save(node)` → `void`; `.Delete(templateId)` → `bool`; `.TemplatePath` → `string`

- [ ] **Step 1: Write failing test**

```csharp
using Xunit;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Studio.App.Services.Environment;

namespace PeakCan.Studio.App.Tests.Environment;

public class RestbusTemplateLibraryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"restbus-tpl-{Guid.NewGuid():N}");

    [Fact]
    public void Save_Then_Load_RoundTrips()
    {
        var lib = new RestbusTemplateLibrary(_tempDir);
        var node = new RestbusNode
        {
            Name = "TestTpl", Identity = new RawCanNodeIdentity(),
            Messages = [new NodeMessage(new CanMessageRef(0x100, false), 100, new FixedHexSource("01"))],
            Trial = new TrialContract("test-tpl", [], [])
        };
        lib.Save(node);
        var loaded = lib.Load();
        var found = Assert.Single(loaded, n => n.Name == "TestTpl");
        Assert.Equal("test-tpl", found.Trial!.TemplateId);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var lib = new RestbusTemplateLibrary(_tempDir);
        Assert.Empty(lib.Load());
    }

    [Fact]
    public void Delete_RemovesTemplate()
    {
        var lib = new RestbusTemplateLibrary(_tempDir);
        var node = new RestbusNode { Name = "Del", Identity = new RawCanNodeIdentity() };
        lib.Save(node);
        Assert.True(lib.Delete("Del"));
        Assert.Empty(lib.Load());
    }

    public void Dispose() { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
}
```

- [ ] **Step 2: Run test to verify it fails** — `RestbusTemplateLibrary` not found
- [ ] **Step 3: Implement**

```csharp
using System.IO;
using System.Text.Json;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;

namespace PeakCan.Studio.App.Services.Environment;

/// <summary>Restbus 用户模板持久化。%APPDATA%\PeakCan.Studio\restbus-templates.json；atomic write。</summary>
public sealed class RestbusTemplateLibrary
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private readonly string _filePath;
    private readonly object _gate = new();

    public RestbusTemplateLibrary(string? baseDir = null)
    {
        var dir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PeakCan.Studio");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "restbus-templates.json");
    }

    public string TemplatePath => _filePath;

    public IReadOnlyList<RestbusNode> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath)) return [];
            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<RestbusNode>>(json, JsonOpts) ?? [];
            }
            catch { return []; }
        }
    }

    public void Save(RestbusNode node)
    {
        lock (_gate)
        {
            var all = LoadInternal();
            all.RemoveAll(n => n.Name == node.Name);
            all.Add(node);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, JsonOpts));
            File.Move(tmp, _filePath, overwrite: true);
        }
    }

    public bool Delete(string templateName)
    {
        lock (_gate)
        {
            var all = LoadInternal();
            var removed = all.RemoveAll(n => n.Name == templateName);
            if (removed > 0)
            {
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(all, JsonOpts));
                File.Move(tmp, _filePath, overwrite: true);
            }
            return removed > 0;
        }
    }

    private List<RestbusNode> LoadInternal() { ... }
}
```

注意：`RestbusNode` 多态序列化需要 hil-core 的 `HILJsonOptions.Default`（已有 `kind` 判别符支持）。实施时确认 `JsonPolymorphic` 属性在 `AOT`/default options 下正确反序列化；必要时注册 `JsonSerializerOptions` 转发到 `HILJsonOptions`。

- [ ] **Step 4: Run tests to verify pass** — all 3 green
- [ ] **Step 5: Commit** — `feat(studio): RestbusTemplateLibrary %APPDATA% persistence with atomic write`

---

### Task 2: "另存为模板" + SignalOverrides 剥离

**Files:**
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/Environment/EnvironmentNodeViewModel.cs` — add SaveAsTemplate
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/Environment/EnvironmentTabViewModel.cs` — wire library
- Test: `peakcan-studio/tests/PeakCan.Studio.App.Tests/Environment/SaveAsTemplateTests.cs`

**Interfaces:**
- Consumes: `RestbusTemplateLibrary` (Task 1), `EnvironmentNodeViewModel.ToNode()`
- Produces: `EnvironmentNodeViewModel.SaveAsTemplate(lib, keepSignalOverrides)` → `RestbusNode` — strips `SignalOverrides` by default; new `templateId = $"user-{Name.ToLower()}"`

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public void SaveAsTemplate_Default_StripsSignalOverrides()
{
    var node = new RestbusNode
    {
        Name = "Test", Identity = new RawCanNodeIdentity(),
        SignalOverrides = new Dictionary<string, double> { ["Msg.Sig"] = 42 }
    };
    var vm = new EnvironmentNodeViewModel(node);
    var saved = vm.SaveAsTemplate(lib, keepSignalOverrides: false);
    Assert.Null(saved.SignalOverrides);
    Assert.StartsWith("user-", saved.Trial!.TemplateId);
}

[Fact]
public void SaveAsTemplate_KeepOverrides_True_PreservesValues()
{
    var node = ...; // with SignalOverrides
    var saved = vm.SaveAsTemplate(lib, keepSignalOverrides: true);
    Assert.NotNull(saved.SignalOverrides);
}
```

- [ ] **Step 2: Run test → FAIL**
- [ ] **Step 3: Implement SaveAsTemplate**

```csharp
public RestbusNode SaveAsTemplate(RestbusTemplateLibrary lib, bool keepSignalOverrides = false)
{
    var template = ToNode() with
    {
        SignalOverrides = keepSignalOverrides ? ToNode().SignalOverrides : null,
        Trial = new TrialContract(
            $"user-{_node.Name.ToLowerInvariant()}",
            _node.Trial?.Handshake ?? [],
            _node.Trial?.RequiredDbcMessages ?? [])
    };
    lib.Save(template);
    return template;
}
```

- [ ] **Step 4: Run test → PASS**
- [ ] **Step 5: Commit** — `feat(studio): SaveAsTemplate with SignalOverrides stripping`

---

### Task 3: EnvironmentTab 加载 seed + user templates + suite save/load

**Files:**
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/Environment/EnvironmentTabViewModel.cs` — ctor accepts `RestbusTemplateLibrary` + `Func<RestbusNode[]>` seed providers
- Test: `peakcan-studio/tests/PeakCan.Studio.App.Tests/Environment/EnvironmentTabTemplateMergeTests.cs`

**Interfaces:**
- Produces: `EnvironmentTabViewModel(lib, seedProvider)` — merges seed + user templates into `AvailableTemplates`; `SaveToSuite(TestSuite)` / `LoadFromSuite(TestSuite)`

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public void Ctor_MergesSeedAndUserTemplates()
{
    var lib = new RestbusTemplateLibrary(tempDir);
    lib.Save(new RestbusNode { Name = "UserTpl", Identity = new RawCanNodeIdentity() });
    var vm = new EnvironmentTabViewModel(lib, () => [Gbt27930ChargerTemplate.Create()]);
    Assert.Contains(vm.AvailableTemplates, t => t.Id == "gbt27930-charger");
    Assert.Contains(vm.AvailableTemplates, t => t.Id == "user-usertpl");
}
```

- [ ] **Step 2: FAIL → implement merge → PASS**
- [ ] **Step 3: Implement SaveToSuite / LoadFromSuite**

```csharp
public TestSuite SaveToSuite(TestSuite suite) => suite with { Environment = BuildSuiteEnvironment() };
public void LoadFromSuite(TestSuite suite)
{
    Nodes.Clear();
    foreach (var n in suite.Environment ?? []) Nodes.Add(new EnvironmentNodeViewModel(n));
}
```

- [ ] **Step 4: Commit** — `feat(studio): merge seed+user templates; suite save/load wiring`

---

### Task 4: Legacy NodeModel JSON → RestbusNode 导入

**Files:**
- Create: `peakcan-studio/src/PeakCan.Studio.App/Services/Environment/RestbusNodeImportService.cs`
- Test: `peakcan-studio/tests/PeakCan.Studio.App.Tests/Environment/RestbusNodeImportServiceTests.cs`

**Interfaces:**
- Consumes: old `host.App/Services/Nodes/NodeModel.cs` JSON schema (read source for field names)
- Produces: `RestbusNodeImportService.ImportNodeJson(string json)` → `RestbusNode`; `.ImportEcuScriptJson(string json)` → `RestbusNode`

- [ ] **Step 1: Write failing test with actual old NodeModel JSON shape (read NodeModelJsonTests.cs for schema)**
- [ ] **Step 2: FAIL → implement DTO mapping → PASS**
- [ ] **Step 3: IntervalMs < 10 → clamp to 10 + warning list**
- [ ] **Step 4: Commit** — `feat(studio): legacy NodeModel/EcuScript JSON import to RestbusNode`

---

### Task 5: TrialRunner 完整握手检查（IFrameReceivedSubscription）

**Files:**
- Modify: `peakcan-host/src/PeakCan.Host.Infrastructure/HIL/Environment/TrialRunner.cs` — inject frame subscription
- Test: `peakcan-host/tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/TrialRunnerFullCheckTests.cs`

**Interfaces:**
- Consumes: `ICanChannel.FrameReceived` event, `TrialContract.Handshake[].ThenReceive`
- Produces: `TrialRunner.RunTrialAsync(nodes, timeout, ct)` — subscribes to incoming frames, waits for each `ThenReceive` message by name lookup, reports timeout + possibleCauses on failure

- [ ] **Step 1: Write failing test** — mock channel that emits a frame after 100ms; assert TrialRunResult.Passed=true + diagnostic marked received
- [ ] **Step 2: Write timeout test** — no frame emitted within timeout → Passed=false + possibleCauses populated
- [ ] **Step 3: Implement** — TaskCompletionSource per handshake step + frame subscription; message-name → ID lookup via DBC or template RequiredDbcMessages
- [ ] **Step 4: Run tests → PASS**
- [ ] **Step 5: Commit** — `feat(host): TrialRunner full handshake check with frame subscription`

---

### Task 6: 最终验证 + push

- [ ] **Step 1:** hil-core 全量 → PASS
- [ ] **Step 2:** host 全量（Core + Infra + App）→ PASS (pre-existing DidDatabase failures excluded)
- [ ] **Step 3:** studio build → PASS
- [ ] **Step 4:** push 3 repos