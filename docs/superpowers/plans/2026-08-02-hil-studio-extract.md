# HIL Configurator Studio 提取到独立仓库 实现 Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 peakcan-host 的 HIL Configurator Studio 提取为独立 private GitHub 仓库 `peakcan-studio`（自包含、可独立构建），并从 peakcan-host 移除 Studio UI（保留 HIL 执行引擎）。

**Architecture:** 新仓库两层结构：`PeakCan.Studio.Core`（net10.0 领域模型，复制自 PeakCan.Host.Core 子集 + PeakCan.Host.Infrastructure/HIL 子集）+ `PeakCan.Studio.App`（net10.0-windows WPF，复制自 App 层 Studio 文件）。命名空间全量改名 `PeakCan.Host.*` → `PeakCan.Studio.*`。peakcan-host 同分支删除 Studio 文件并清理接线，保留引擎，两仓库靠"格式冻结"约定保证 suite/script 互操作。

**Tech Stack:** WPF / .NET 10 / CommunityToolkit.Mvvm 8.4.2 / Microsoft.Extensions.Logging.Abstractions / xunit + FluentAssertions

## Global Constraints

- **仓库名**: `peakcan-studio`，GitHub **private**，owner `jasontaotao`。
- **Namespace 映射（全量替换，含 XAML `clr-namespace:`）**:
  - `PeakCan.Host.Core` → `PeakCan.Studio.Core`
  - `PeakCan.Host.Infrastructure` → `PeakCan.Studio.Core`（Infrastructure/HIL 子集并入 Core 层）
  - `PeakCan.Host.App` → `PeakCan.Studio.App`
  - 替换顺序无关冲突（三个前缀不重叠），用 `sed -i` 一次性处理 `.cs` + `.xaml`。
- **禁止复制引擎**：`TestSuiteEngine`、`StepExecutor/*`、`EcuSimulatorHost`、`VirtualEcu`/`StatefulVirtualEcu`、`HilRunnerService`、`HILAssertionContext`、`MatrixConfig*`、`CircularBuffer`、`Diff/*`、`Analysis/*`、`Reporting/*`、`HilRunRequest*`、`HilMode`、`TestCaseResult`、`TestSuiteResult`、`StepResult`、`StepStatus`。若复制中发现 Studio 意外依赖这些类型，**停下评估**，不得悄悄带过。
- **格式冻结**：suite/script JSON 模型签名（字段/`$type` 名）不得改动。
- **peakcan-host 修改分支**：`feature/hil-studio-phase1`（当前分支，含 Phase 3 全部代码）。不得 `git stash -u`（工作树有长期未提交 docs 变更）。
- **不碰用户未提交 docs**：`docs/user-manual-hil.html`(M)、`docs/hil-configuration-studio-guide.html`(??)、`docs/hil-test-script-tutorial.html`(??) 一律不 add/不修改。
- **新仓库路径**: `D:\claude_proj2\peakcan-studio`（独立 git 仓库，非 submodule）。
- **验证基线**：peakcan-host 现测试 Core 798 pass / Infrastructure 337 pass / App 1187 pass+4 pre-existing fail（TraceViewer/Tmtrace，与本工作无关，可忽略）。

---

### Task 1: 创建 private 仓库 + 新仓库脚手架

**Files:**
- Create: `D:\claude_proj2\peakcan-studio\PeakCan.Studio.slnx`
- Create: `D:\claude_proj2\peakcan-studio\Directory.Build.props`
- Create: `D:\claude_proj2\peakcan-studio\Directory.Packages.props`
- Create: `D:\claude_proj2\peakcan-studio\.gitignore`
- Create: `D:\claude_proj2\peakcan-studio\.editorconfig`
- Create: `src/PeakCan.Studio.Core/PeakCan.Studio.Core.csproj`
- Create: `src/PeakCan.Studio.App/PeakCan.Studio.App.csproj`（引用 Studio.Core）
- Create: `tests/PeakCan.Studio.Core.Tests/PeakCan.Studio.Core.Tests.csproj`
- Create: `tests/PeakCan.Studio.App.Tests/PeakCan.Studio.App.Tests.csproj`（引用 Studio.Core + Studio.App，`net10.0-windows`）
- Create: `src/PeakCan.Studio.App/App.xaml` `App.xaml.cs`（WPF 入口骨架）
- Create: `src/PeakCan.Studio.App/MainWindow.xaml` `MainWindow.xaml.cs`（占位主窗口）

**Interfaces:**
- Produces: 空项目骨架，四个 csproj 均可 build。App 层 WPF 入口 `App` 类 + `MainWindow`。后续 task 填充真实代码。

- [ ] **Step 1: 创建本地目录 + 空 git 仓库**

```bash
mkdir -p /d/claude_proj2/peakcan-studio/src/PeakCan.Studio.Core
mkdir -p /d/claude_proj2/peakcan-studio/src/PeakCan.Studio.App
mkdir -p /d/claude_proj2/peakcan-studio/tests/PeakCan.Studio.Core.Tests
mkdir -p /d/claude_proj2/peakcan-studio/tests/PeakCan.Studio.App.Tests
cd /d/claude_proj2/peakcan-studio && git init -b main
```

- [ ] **Step 2: 创建 private GitHub 仓库**

```bash
cd /d/claude_proj2/peakcan-studio
gh repo create peakcan-studio --private --source=. --remote=origin \
  --description "HIL Configurator Studio — visual DBC browser, test suite builder, and ECU simulator script editor"
```

- [ ] **Step 3: 写 `.gitignore`（复制自 peakcan-host，含 bin/obj/.vs/artifacts）**

```bash
cp /d/claude_proj2/peakcan-host/.gitignore /d/claude_proj2/peakcan-studio/.gitignore
cp /d/claude_proj2/peakcan-host/.editorconfig /d/claude_proj2/peakcan-studio/.editorconfig
```

- [ ] **Step 4: 写 `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

- [ ] **Step 5: 写 `Directory.Packages.props`（central package management）**

版本从 peakcan-host `Directory.Packages.props` 原样取值：

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.9" />
    <PackageVersion Include="Microsoft.Extensions.Logging" Version="10.0.9" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Debug" Version="10.0.9" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.9" />
    <PackageVersion Include="System.Text.Encoding.CodePages" Version="9.0.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.4" />
    <PackageVersion Include="FluentAssertions" Version="8.10.0" />
  </ItemGroup>
</Project>
```

> 注：`Microsoft.Extensions.DependencyInjection` + `Logging` + `Logging.Debug` 供 App 启动 DI（Task 3）；`System.Text.Encoding.CodePages` 供 DBC 非 UTF-8（GBK）回退（Task 3 App.xaml.cs 注册）。

- [ ] **Step 6: 写四个 csproj**

`src/PeakCan.Studio.Core/PeakCan.Studio.Core.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
```

`src/PeakCan.Studio.App/PeakCan.Studio.App.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <OutputType>WinExe</OutputType>
    <AssemblyName>PeakCan.Studio</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
    <PackageReference Include="Microsoft.Extensions.Logging.Debug" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="System.Text.Encoding.CodePages" />
    <ProjectReference Include="..\PeakCan.Studio.Core\PeakCan.Studio.Core.csproj" />
  </ItemGroup>
</Project>
```

`tests/PeakCan.Studio.Core.Tests/PeakCan.Studio.Core.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <ProjectReference Include="..\..\src\PeakCan.Studio.Core\PeakCan.Studio.Core.csproj" />
  </ItemGroup>
</Project>
```

`tests/PeakCan.Studio.App.Tests/PeakCan.Studio.App.Tests.csproj`（同上，但 `net10.0-windows` + `UseWPF true`，`ProjectReference` Studio.Core + Studio.App）。

- [ ] **Step 7: 写占位 WPF 入口**（App.xaml + App.xaml.cs + MainWindow）

`src/PeakCan.Studio.App/App.xaml`:
```xml
<Application x:Class="PeakCan.Studio.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml">
    <Application.Resources/>
</Application>
```

`src/PeakCan.Studio.App/App.xaml.cs`:
```csharp
using System.Windows;

namespace PeakCan.Studio.App;

public partial class App : Application
{
}
```

`src/PeakCan.Studio.App/MainWindow.xaml`:
```xml
<Window x:Class="PeakCan.Studio.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="HIL Configurator Studio" Height="800" Width="1200">
    <TextBlock Text="scaffold" />
</Window>
```

`src/PeakCan.Studio.App/MainWindow.xaml.cs`:
```csharp
using System.Windows;

namespace PeakCan.Studio.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
```

- [ ] **Step 8: 写 `PeakCan.Studio.slnx`**（用 `dotnet new sln` 的 .slnx 或手工写 4 项目引用，参照 peakcan-host 的 `PeakCan.Host.slnx` 格式）

- [ ] **Step 9: 全量 build + 首次 commit**

```bash
cd /d/claude_proj2/peakcan-studio
dotnet build PeakCan.Studio.slnx
```
Expected: build 成功（空项目）。
```bash
git add -A && git commit -m "chore: scaffold peakcan-studio solution (Core + App + tests)"
```

- [ ] **Step 10: Commit**

（并入 Step 9 的 commit，无单独 commit。）

---

### Task 2: 复制领域模型到 Studio.Core + namespace 改名

**Files:**
- 复制（源 `D:\claude_proj2\peakcan-host\src\PeakCan.Host.Core\` → 目标 `D:\claude_proj2\peakcan-studio\src\PeakCan.Studio.Core\`，保持相对目录）:
  - `HIL/StepParams/*`（16 文件：`StepParameters.cs` `StepParametersFactory.cs` `StepParametersExporter.cs` `AssertDtcStep.cs` `AssertNrcStep.cs` `AssertRangeStep.cs` `AssertResponseTimeStep.cs` `AssertSignalStep.cs` `ClearFaultStep.cs` `CommentStep.cs` `DelayStep.cs` `ExpectFrameStep.cs` `InjectFaultStep.cs` `SendFrameStep.cs` `WaitForSignalStep.cs`）
  - `HIL/Serialization/HILJsonOptions.cs` `HIL/Serialization/ByteArrayJsonConverter.cs`
  - `HIL/TestCase.cs` `HIL/TestCaseStep.cs` `HIL/TestCaseStepKind.cs` `HIL/TestCaseStepJsonConverter.cs` `HIL/TestSuite.cs` `HIL/TestSuiteConfig.cs`
  - `HIL/Contracts/` 子集（**以编译闭包为准，预期至少**）：`EcuResponse.cs` `EcuStateMachine.cs` `EcuStateTransition.cs` `FaultDirection.cs` `FaultRule.cs` `IEcuResponseGenerator.cs` `UdsResponseRule.cs`
  - `Dbc/*` 全量 21 文件
  - `Uds/Odx/*` 子集（预期）：`OdxParser.cs` `OdxDocument.cs` `OdxErrorCode.cs` `OdxImportResult.cs` `DiagLayer.cs` `DiagService.cs` `DidDop.cs` `DidFieldType.cs` `DidValueDecoder.cs` `DtcDop.cs` `EcuJob.cs` `RequestBasedMappers.cs` `SecurityAccessConfig.cs` `SecurityAccessExtractor.cs` `OdxStateChartExtractor.cs` `OdxStateChartInfo.cs` `CompuMethodParser.cs` `PdxReader.cs` `DecodedField.cs`
  - `IFileDialogService.cs`
- 复制（源 `D:\claude_proj2\peakcan-host\src\PeakCan.Host.Infrastructure\HIL\` → 目标 `D:\claude_proj2\peakcan-studio\src\PeakCan.Studio.Core\HIL\`）:
  - `EcuScript.cs` `EcuScriptLoader.cs` `DbcLookupKey.cs` `HeadlessDbcLookup.cs`
  - `Generators/*` 全部 8 文件
  - `Odx/OdxEcuScriptImporter.cs` `Odx/OdxToEcuScriptAdapter.cs`
- **新提取**（目标 `D:\claude_proj2\peakcan-studio\src\PeakCan.Studio.Core\Uds\IsoTp\CanIdConfig.cs`）：从源 `Core/Uds/IsoTp/IsoTpLayer.cs:144` 把 `CanIdConfig` 类（4 属性：`uint RequestId` `uint ResponseId` `uint? FunctionalId` `bool IsExtendedFrame`）**单独提取**为新文件（不复制整个 IsoTpLayer.cs）。

**Interfaces:**
- Consumes: Task 1 的 `PeakCan.Studio.Core.csproj`。
- Produces: `PeakCan.Studio.Core` 编译通过的领域模型。**引用方注意**：`CanIdConfig` 在 `PeakCan.Studio.Core.Uds.IsoTp` namespace；`EcuScriptLoader`/`EcuScript` 在 `PeakCan.Studio.Core.HIL`；`BuiltInGenerators` 在 `PeakCan.Studio.Core.HIL.Generators`；`OdxEcuScriptImporter` 在 `PeakCan.Studio.Core.HIL.Odx`。Task 3 的 App 层引用这些命名空间。

- [ ] **Step 1: 复制领域模型文件到目标目录（保持相对结构）**

```bash
SRC=/d/claude_proj2/peakcan-host/src
DST=/d/claude_proj2/peakcan-studio/src/PeakCan.Studio.Core
mkdir -p $DST/HIL/StepParams $DST/HIL/Serialization $DST/HIL/Contracts \
         $DST/HIL/Generators $DST/HIL/Odx $DST/Dbc $DST/Uds/Odx $DST/Uds/IsoTp
cp $SRC/PeakCan.Host.Core/HIL/StepParams/*.cs $DST/HIL/StepParams/
cp $SRC/PeakCan.Host.Core/HIL/Serialization/*.cs $DST/HIL/Serialization/
cp $SRC/PeakCan.Host.Core/HIL/TestCase.cs $SRC/PeakCan.Host.Core/HIL/TestCaseStep.cs \
   $SRC/PeakCan.Host.Core/HIL/TestCaseStepKind.cs $SRC/PeakCan.Host.Core/HIL/TestCaseStepJsonConverter.cs \
   $SRC/PeakCan.Host.Core/HIL/TestSuite.cs $SRC/PeakCan.Host.Core/HIL/TestSuiteConfig.cs $DST/HIL/
cp $SRC/PeakCan.Host.Core/HIL/Contracts/EcuResponse.cs $SRC/PeakCan.Host.Core/HIL/Contracts/EcuStateMachine.cs \
   $SRC/PeakCan.Host.Core/HIL/Contracts/EcuStateTransition.cs $SRC/PeakCan.Host.Core/HIL/Contracts/FaultDirection.cs \
   $SRC/PeakCan.Host.Core/HIL/Contracts/FaultRule.cs $SRC/PeakCan.Host.Core/HIL/Contracts/IEcuResponseGenerator.cs \
   $SRC/PeakCan.Host.Core/HIL/Contracts/UdsResponseRule.cs $DST/HIL/Contracts/
cp $SRC/PeakCan.Host.Core/Dbc/*.cs $DST/Dbc/
cp $SRC/PeakCan.Host.Core/IFileDialogService.cs $DST/
cp $SRC/PeakCan.Host.Infrastructure/HIL/EcuScript.cs $SRC/PeakCan.Host.Infrastructure/HIL/EcuScriptLoader.cs \
   $SRC/PeakCan.Host.Infrastructure/HIL/DbcLookupKey.cs $SRC/PeakCan.Host.Infrastructure/HIL/HeadlessDbcLookup.cs $DST/HIL/
cp $SRC/PeakCan.Host.Infrastructure/HIL/Generators/*.cs $DST/HIL/Generators/
cp $SRC/PeakCan.Host.Infrastructure/HIL/Odx/OdxEcuScriptImporter.cs $SRC/PeakCan.Host.Infrastructure/HIL/Odx/OdxToEcuScriptAdapter.cs $DST/HIL/Odx/
```

- [ ] **Step 2: 提取 `CanIdConfig` 为独立文件**

读源 `Core/Uds/IsoTp/IsoTpLayer.cs` 的 `CanIdConfig` 定义（`IsoTpLayer.cs:144-166`），在 `$DST/Uds/IsoTp/CanIdConfig.cs` 写出等价的独立类（namespace `PeakCan.Studio.Core.Uds.IsoTp`，public，4 属性同签名）。若 `CanIdConfig` 的完整定义需要 `using` 依赖，一并复制对应 `using`。**不修改源 IsoTpLayer.cs**。

- [ ] **Step 3: namespace 全量替换（.cs）**

```bash
cd /d/claude_proj2/peakcan-studio/src/PeakCan.Studio.Core
grep -rl "PeakCan.Host" --include="*.cs" . | while read f; do
  sed -i 's/PeakCan\.Host\.Core/PeakCan.Studio.Core/g; s/PeakCan\.Host\.Infrastructure/PeakCan.Studio.Core/g; s/PeakCan\.Host\.App/PeakCan.Studio.App/g' "$f"
done
```

- [ ] **Step 4: build 并修编译错误**

```bash
cd /d/claude_proj2/peakcan-studio && dotnet build src/PeakCan.Studio.Core/PeakCan.Studio.Core.csproj
```
Expected: build 成功。若报缺失类型（Contracts/Odx 子集不全），用 codegraph 或 grep 在源仓库定位该类型所在文件并复制（**仅复制纯模型/解析类型，禁止引擎类型**）。循环直到 build 绿。

- [ ] **Step 5: 验证无引擎类型泄漏**

```bash
grep -rnE "TestSuiteEngine|StepExecutor|EcuSimulatorHost|VirtualEcu|HilRunnerService|MatrixConfig|CircularBuffer" /d/claude_proj2/peakcan-studio/src/PeakCan.Studio.Core || echo "CLEAN"
```
Expected: `CLEAN`（无匹配）。

- [ ] **Step 6: Commit**

```bash
cd /d/claude_proj2/peakcan-studio
git add -A && git commit -m "feat(core): copy HIL domain models + DBC + Odx into PeakCan.Studio.Core
Namespace migrated PeakCan.Host.* to PeakCan.Studio.Core.*; CanIdConfig extracted
from IsoTpLayer.cs into its own file. Engine types (TestSuiteEngine/StepExecutor/
VirtualEcu etc.) intentionally NOT copied."
```

---

### Task 3: 复制 Studio UI 到 Studio.App + namespace 改名

**Files:**
- 复制（源 `D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\` → 目标 `D:\claude_proj2\peakcan-studio\src\PeakCan.Studio.App\`）:
  - `ViewModels/HilStudioViewModel.cs` + `ViewModels/HilStudioViewModel/DbcLoadingFlow.partial.cs` `DbcSearchFlow.partial.cs`
  - `ViewModels/HilStudioDbcMessageRow.cs` `ViewModels/HilStudioDbcSignalRow.cs`
  - `ViewModels/TestSuiteBuilder/*.cs`（8 文件）
  - `ViewModels/EcuSimulator/*.cs`（6 文件）
  - `ViewModels/DispatcherExtensions.cs`
  - `Windows/HilStudioWindow.xaml` `.cs`
  - `Controls/EcuStatePreview.cs` `Controls/EcuResponseModeToVisibilityConverter.cs`
  - `Services/DbcService.cs` `Services/DbcOptions.cs`
  - `Services/WpfFileDialogService.cs`
  - `Services/Trace/WpfMessageBoxPrompt.cs` + 复制 `IMessageBoxPrompt` 接口（源 `Services/Trace/TraceSessionAutoSaver.cs:89`，复制该接口定义到 Studio 新文件 `Services/IMessageBoxPrompt.cs`）
  - `Composition/Converters/NullToVisibilityConverter.cs`
- 修改: `src/PeakCan.Studio.App/App.xaml.cs`（加 DI 启动）`MainWindow` → 改为承载 `HilStudioWindow`（或 `HilStudioWindow` 作主窗口）。

**Interfaces:**
- Consumes: Task 2 的 Studio.Core（`HILJsonOptions` `EcuScriptLoader` `BuiltInGenerators` `OdxEcuScriptImporter` `DbcEncodeService` 等）+ `IFileDialogService`。
- Produces: 可启动的 WPF 应用，`HilStudioWindow` 为主窗口。DI 容器注册 `DbcService`(singleton)、`HilStudioViewModel`(singleton)、`IFileDialogService`→`WpfFileDialogService`、`IMessageBoxPrompt`→`WpfMessageBoxPrompt`。

- [ ] **Step 1: 复制 UI 文件**

```bash
SRC=/d/claude_proj2/peakcan-host/src/PeakCan.Host.App
DST=/d/claude_proj2/peakcan-studio/src/PeakCan.Studio.App
mkdir -p $DST/ViewModels/HilStudioViewModel $DST/ViewModels/TestSuiteBuilder $DST/ViewModels/EcuSimulator \
         $DST/Windows $DST/Controls $DST/Services $DST/Composition/Converters
cp $SRC/ViewModels/HilStudioViewModel.cs $DST/ViewModels/
cp $SRC/ViewModels/HilStudioViewModel/*.cs $DST/ViewModels/HilStudioViewModel/
cp $SRC/ViewModels/HilStudioDbcMessageRow.cs $SRC/ViewModels/HilStudioDbcSignalRow.cs $DST/ViewModels/
cp $SRC/ViewModels/TestSuiteBuilder/*.cs $DST/ViewModels/TestSuiteBuilder/
cp $SRC/ViewModels/EcuSimulator/*.cs $DST/ViewModels/EcuSimulator/
cp $SRC/ViewModels/DispatcherExtensions.cs $DST/ViewModels/
cp $SRC/Windows/HilStudioWindow.xaml $SRC/Windows/HilStudioWindow.xaml.cs $DST/Windows/
cp $SRC/Controls/EcuStatePreview.cs $SRC/Controls/EcuResponseModeToVisibilityConverter.cs $DST/Controls/
cp $SRC/Services/DbcService.cs $SRC/Services/DbcOptions.cs $SRC/Services/WpfFileDialogService.cs $DST/Services/
cp $SRC/Services/Trace/WpfMessageBoxPrompt.cs $DST/Services/
cp $SRC/Composition/Converters/NullToVisibilityConverter.cs $DST/Composition/Converters/
```

- [ ] **Step 2: 复制 `IMessageBoxPrompt` 接口**

读 `$SRC/Services/Trace/TraceSessionAutoSaver.cs:85-115` 的 `IMessageBoxPrompt` 接口定义（含 `ShowAsync` + `ShowInformationAsync`），写入 `$DST/Services/IMessageBoxPrompt.cs`（namespace `PeakCan.Studio.App.Services`）。

- [ ] **Step 3: namespace 全量替换（.cs + .xaml）**

```bash
cd /d/claude_proj2/peakcan-studio/src/PeakCan.Studio.App
grep -rl "PeakCan.Host" --include="*.cs" --include="*.xaml" . | while read f; do
  sed -i 's/PeakCan\.Host\.Core/PeakCan.Studio.Core/g; s/PeakCan\.Host\.Infrastructure/PeakCan.Studio.Core/g; s/PeakCan\.Host\.App/PeakCan.Studio.App/g' "$f"
done
```

- [ ] **Step 4: 重写 WPF 入口承载 HilStudioWindow**

删除 Task 1 的 `MainWindow.xaml`/`.cs`。`App.xaml` 去掉 `StartupUri`，`App.xaml.cs` 写 DI 启动：

```csharp
using System.Text;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PeakCan.Studio.App.Services;
using PeakCan.Studio.App.ViewModels;
using PeakCan.Studio.App.Windows;
using PeakCan.Studio.Core;

namespace PeakCan.Studio.App;

public partial class App : Application
{
    public static ServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 与 peakcan-host App.xaml.cs 一致：注册 OEM code pages，DBC 非 UTF-8（GBK/CP936）加载依赖它
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var sc = new ServiceCollection();
        sc.AddSingleton<DbcService>();
        sc.AddSingleton<HilStudioViewModel>();
        sc.AddSingleton<IFileDialogService, WpfFileDialogService>();
        sc.AddSingleton<IMessageBoxPrompt, WpfMessageBoxPrompt>();
        sc.AddLogging(b => b.AddDebug());
        Services = sc.BuildServiceProvider();

        var win = new HilStudioWindow(Services.GetRequiredService<HilStudioViewModel>());
        win.Show();
    }
}
```

`HilStudioWindow.xaml.cs` 构造函数签名若为 `HilStudioWindow(HilStudioViewModel vm)`（核对源文件，若非则按源签名）。`HilStudioWindow` 的 `DataContext` 由构造注入设置。

**注意**：`HilStudioViewModel` 构造签名是 `(DbcService svc, ILogger<HilStudioViewModel> logger, IFileDialogService? fileDialog = null, DbcEncodeService? encodeService = null)`（核对 Task 2 复制的源码）。DI 按此解析：`DbcService` + `ILogger<HilStudioViewModel>`（AddLogging 提供）+ `IFileDialogService` + `DbcEncodeService`。若 `DbcEncodeService` 需注册（无参构造则 `AddSingleton`），补注册。

- [ ] **Step 5: build + 修编译错误**

```bash
cd /d/claude_proj2/peakcan-studio && dotnet build src/PeakCan.Studio.App/PeakCan.Studio.App.csproj
```
Expected: build 成功。报错驱动补齐：缺失的 Studio.Core 类型 → 回 Task 2 范围补充复制（仅模型）；XAML `clr-namespace` 已由 Step 3 sed 处理；`NullToVisibilityConverter` 已在项目内；`DbcService` internal ctor 需 `InternalsVisibleTo("PeakCan.Studio.App.Tests")`（见 Task 4）。

- [ ] **Step 6: 冒烟启动验证**

```bash
cd /d/claude_proj2/peakcan-studio && dotnet run --project src/PeakCan.Studio.App
```
Expected: 窗口打开显示 HilStudioWindow（三面板 UI）。手动关闭。（无 UI 自动化环境则跳过此步并记录。）

- [ ] **Step 7: Commit**

```bash
cd /d/claude_proj2/peakcan-studio
git add -A && git commit -m "feat(app): copy Studio UI (HilStudioWindow + 3-panel VMs) into PeakCan.Studio.App
WPF entry rewritten with DI (DbcService/HilStudioViewModel/IFileDialogService/
IMessageBoxPrompt). IMessageBoxPrompt + NullToVisibilityConverter copied locally."
```

---

### Task 4: 复制 Studio 测试 + 全绿

**Files:**
- 复制（源 `D:\claude_proj2\peakcan-host\tests\` → 目标 `D:\claude_proj2\peakcan-studio\tests\`）:
  - `PeakCan.Host.App.Tests/ViewModels/EcuSimulator/`（`EcuSimulatorViewModelTests.cs` `EditableEcuScriptTests.cs`）→ `PeakCan.Studio.App.Tests/ViewModels/EcuSimulator/`
  - `PeakCan.Host.App.Tests/ViewModels/TestSuiteBuilder/`（`EditableModelTests.cs` `SendFrameComposerViewModelTests.cs` `StepFieldDescriptorsTests.cs` `TestSuiteBuilderViewModelTests.cs`）→ `PeakCan.Studio.App.Tests/ViewModels/TestSuiteBuilder/`
  - `PeakCan.Host.App.Tests/ViewModels/HilStudioProjectionTests.cs` `HilStudioViewModelTests.cs` → `PeakCan.Studio.App.Tests/ViewModels/`
  - 源 `PeakCan.Host.Core.Tests/` 的 HIL 模型/解析/DBC 测试（复制 `HIL/` + `Dbc/` 目录中涉及复制类型的测试；编译依赖为准）→ `PeakCan.Studio.Core.Tests/`

**Interfaces:**
- Consumes: Task 2 Studio.Core + Task 3 Studio.App。
- Produces: 两个测试项目全绿。注意 `DbcService.SetCurrentForTests`/internal ctor 需要 `InternalsVisibleTo("PeakCan.Studio.App.Tests")`。

- [ ] **Step 1: 复制 App.Tests 的 Studio 测试**

```bash
SRC=/d/claude_proj2/peakcan-host/tests
DST=/d/claude_proj2/peakcan-studio/tests
mkdir -p $DST/PeakCan.Studio.App.Tests/ViewModels/EcuSimulator \
         $DST/PeakCan.Studio.App.Tests/ViewModels/TestSuiteBuilder
cp $SRC/PeakCan.Host.App.Tests/ViewModels/EcuSimulator/*.cs $DST/PeakCan.Studio.App.Tests/ViewModels/EcuSimulator/
cp $SRC/PeakCan.Host.App.Tests/ViewModels/TestSuiteBuilder/*.cs $DST/PeakCan.Studio.App.Tests/ViewModels/TestSuiteBuilder/
cp $SRC/PeakCan.Host.App.Tests/ViewModels/HilStudioProjectionTests.cs $SRC/PeakCan.Host.App.Tests/ViewModels/HilStudioViewModelTests.cs $DST/PeakCan.Studio.App.Tests/ViewModels/
```

- [ ] **Step 2: 复制 Core.Tests 的 HIL/DBC 测试**

```bash
# 先复制整个 HIL + Dbc 测试目录，删除引用了未复制类型的测试（编译驱动）
cp -r $SRC/PeakCan.Host.Core.Tests/HIL $DST/PeakCan.Studio.Core.Tests/HIL
cp -r $SRC/PeakCan.Host.Core.Tests/Dbc $DST/PeakCan.Studio.Core.Tests/Dbc
```

- [ ] **Step 3: namespace 替换 + 测试项目 InternalsVisibleTo**

```bash
cd /d/claude_proj2/peakcan-studio/tests
grep -rl "PeakCan.Host" --include="*.cs" . | while read f; do
  sed -i 's/PeakCan\.Host\.Core/PeakCan.Studio.Core/g; s/PeakCan\.Host\.Infrastructure/PeakCan.Studio.Core/g; s/PeakCan\.Host\.App/PeakCan.Studio.App/g' "$f"
done
```

在 `src/PeakCan.Studio.Core/PeakCan.Studio.Core.csproj` 加（`DbcEncodeService` 等若 internal 需此；若无 internal 类型可省略，编译报错再补）：
```xml
<ItemGroup>
  <InternalsVisibleTo Include="PeakCan.Studio.Core.Tests" />
</ItemGroup>
```
在 `src/PeakCan.Studio.App/PeakCan.Studio.App.csproj` 加：
```xml
<ItemGroup>
  <InternalsVisibleTo Include="PeakCan.Studio.App.Tests" />
</ItemGroup>
```
（源 `DbcService` 用 `InternalsVisibleTo PeakCan.Host.App.Tests` 属性 → 目标改 `PeakCan.Studio.App.Tests`。核对 Task 3 复制的 DbcService 是否声明该属性，若在 csproj 则改为新名。）

- [ ] **Step 4: 全量测试 + 修失败**

```bash
cd /d/claude_proj2/peakcan-studio && dotnet test PeakCan.Studio.slnx
```
Expected: 全绿。失败驱动：删引用未复制类型的测试；补 namespace 引用；补缺失模型类型（回 Task 2 范围）。

- [ ] **Step 5: 验证无引擎测试泄漏**

```bash
grep -rnE "TestSuiteEngine|VirtualEcu|HilRunner|MatrixConfig|StepExecutor" /d/claude_proj2/peakcan-studio/tests || echo "CLEAN"
```
Expected: `CLEAN` 或仅剩复制的引擎相关测试待删（Step 4 已删）。

- [ ] **Step 6: Commit**

```bash
cd /d/claude_proj2/peakcan-studio
git add -A && git commit -m "test: copy Studio VM tests + HIL/DBC model tests, all green"
```

---

### Task 5: peakcan-host 移除 Studio + 全绿

**Files:**
- Delete (peakcan-host `D:\claude_proj2\peakcan-host\src\PeakCan.Host.App\`):
  - `ViewModels/HilStudioViewModel.cs` `ViewModels/HilStudioViewModel/`(dir)
  - `ViewModels/HilStudioDbcMessageRow.cs` `ViewModels/HilStudioDbcSignalRow.cs`
  - `ViewModels/TestSuiteBuilder/`(dir) `ViewModels/EcuSimulator/`(dir)
  - `Windows/HilStudioWindow.xaml` `.cs`
  - `Controls/EcuStatePreview.cs` `Controls/EcuResponseModeToVisibilityConverter.cs`
  - `Composition/Converters/NullToVisibilityConverter.cs` — **保留**（已确认 DbcView 等在用）
- Delete (peakcan-host tests):
  - `tests/PeakCan.Host.App.Tests/ViewModels/EcuSimulator/` `tests/PeakCan.Host.App.Tests/ViewModels/TestSuiteBuilder/`
  - `tests/PeakCan.Host.App.Tests/ViewModels/HilStudioProjectionTests.cs` `HilStudioViewModelTests.cs`
- Modify (peakcan-host):
  - `ViewModels/AppShellViewModel/ViewSwitchFlow.cs`（删 `ShowHilStudioCommand`→`SyncEcuScriptPath` 段 + `_hilStudioWindow` field）
  - `ViewModels/AppShellViewModel.cs`（删 `_hilStudioViewModel` field + 构造参数 + `EcuSimulator` 相关）
  - `Composition/AppHostBuilder.cs`（删 `HilStudioViewModel` 注册 + AppShell 注入参数）
  - `AppShellViewModel` 相关测试：`AppShellViewModelTests.cs` `AppShellViewModelMessageBoxPromptTests.cs` `Windows/UdsWindowTests.cs` 中构造 `HilStudioViewModel` 的代码

**Interfaces:**
- Consumes: 无新接口。peakcan-host 保留 `HilViewModel`/`EcuScriptEditor`/`DbcService`/`DispatcherExtensions` 原样。
- Produces: peakcan-host 编译 + 测试全绿，无 Studio 残留。

- [ ] **Step 1: 删除 Studio 源文件**

```bash
cd /d/claude_proj2/peakcan-host
git rm -r src/PeakCan.Host.App/ViewModels/HilStudioViewModel src/PeakCan.Host.App/ViewModels/HilStudioViewModel.cs \
  src/PeakCan.Host.App/ViewModels/HilStudioDbcMessageRow.cs src/PeakCan.Host.App/ViewModels/HilStudioDbcSignalRow.cs \
  src/PeakCan.Host.App/ViewModels/TestSuiteBuilder src/PeakCan.Host.App/ViewModels/EcuSimulator \
  src/PeakCan.Host.App/Windows/HilStudioWindow.xaml src/PeakCan.Host.App/Windows/HilStudioWindow.xaml.cs \
  src/PeakCan.Host.App/Controls/EcuStatePreview.cs src/PeakCan.Host.App/Controls/EcuResponseModeToVisibilityConverter.cs
```
（保留 `NullToVisibilityConverter.cs`。）

- [ ] **Step 2: 删除 Studio 测试文件**

```bash
cd /d/claude_proj2/peakcan-host
git rm -r tests/PeakCan.Host.App.Tests/ViewModels/EcuSimulator tests/PeakCan.Host.App.Tests/ViewModels/TestSuiteBuilder \
  tests/PeakCan.Host.App.Tests/ViewModels/HilStudioProjectionTests.cs tests/PeakCan.Host.App.Tests/ViewModels/HilStudioViewModelTests.cs
```

- [ ] **Step 3: 删 AppShell 的 Studio 接线**

读 `ViewModels/AppShellViewModel/ViewSwitchFlow.cs` 与 `ViewModels/AppShellViewModel.cs`：
- 删 `ShowHilStudio` 方法、`OnEcuScriptPathSetExternally`、`OnEcuSimulatorPropertyChanged`、`SyncEcuScriptPath`（ViewSwitchFlow.cs 的 `ShowHilStudioCommand` → `SyncEcuScriptPath` 段）。
- 删 `_hilStudioWindow` field。
- 删 `AppShellViewModel.cs` 的 `_hilStudioViewModel` field、构造参数、`EcuSimulator.PropertyChanged` 订阅。
- **保留** `ShowEcuScriptEditorCommand` + `OnOpenEcuEditorRequested` + EcuScriptEditor 的 `LoadInitialPath(_hilViewModel.EcuScriptPath)` 同步。

- [ ] **Step 4: 删 DI 注册**

`Composition/AppHostBuilder.cs`：
- 删 `builder.Services.AddSingleton<ViewModels.HilStudioViewModel>();`
- 删 AppShell 构造注入的 `sp.GetRequiredService<ViewModels.HilStudioViewModel>()` 参数。
- `EcuScriptEditorViewModel` 注册保留。

- [ ] **Step 5: 修测试引用**

编译报错驱动：`AppShellViewModelTests.cs` `AppShellViewModelMessageBoxPromptTests.cs` `Windows/UdsWindowTests.cs` 中创建 `HilStudioViewModel` 的 `NewVm`/`MakeVm` 辅助删掉对应参数。逐个修复直到 build 绿。

- [ ] **Step 6: 全量 build + 测试**

```bash
cd /d/claude_proj2/peakcan-host && dotnet build PeakCan.Host.slnx
cd /d/claude_proj2/peakcan-host && dotnet test PeakCan.Host.slnx
```
Expected: build 绿；测试 Core 798 / Infrastructure 337 / App 1187+4 pre-existing fail（同基线，无新增失败）。

- [ ] **Step 7: 确认无 Studio 残留引用**

```bash
cd /d/claude_proj2/peakcan-host
grep -rn "HilStudio\|TestSuiteBuilder\|EcuSimulator" src --include="*.cs" --include="*.xaml" | grep -v obj || echo "CLEAN"
```
Expected: `CLEAN`（仅可能剩 `EcuScriptEditor` 相关，那是独立功能）。

- [ ] **Step 8: Commit**

```bash
cd /d/claude_proj2/peakcan-host
git add -A && git commit -m "refactor(hil): remove HIL Configurator Studio from peakcan-host
Studio (HilStudioViewModel + TestSuiteBuilder + EcuSimulator + HilStudioWindow)
extracted to standalone private repo peakcan-studio. HIL execution engine,
DBC View, and EcuScriptEditor stay. NullToVisibilityConverter retained (used by
DbcView/ReplayView/TraceViewer/UdsWindow)."
```

---

### Task 6: E2E 互操作验证 + README + push

**Files:**
- Create: `D:\claude_proj2\peakcan-studio\README.md`
- Modify: `D:\claude_proj2\peakcan-host\README.md`（格式冻结约束段落，若已有 HIL 文档则追加）
- Modify: 两仓库模型代码若有格式差异（正常不应有）

**Interfaces:**
- Consumes: Task 2-5 的成品。

- [ ] **Step 1: Studio 保存产物 → peakcan-host 加载执行验证**

1. 用 Studio（`dotnet run`）或直接用 Studio.Core 的序列化代码生成一份 `ecu-script.json` + 一份 suite JSON（或复制 peakcan-host 现有 fixture `bms_sim.json` 对应的脚本样例）。
2. 在 peakcan-host 用 `EcuScriptLoader.Parse`（通过现有 `EcuScriptEditorViewModel` 或测试）加载该 JSON，确认解析成功。
3. 在 peakcan-host 用 `HILJsonOptions.Default` 反序列化 suite，确认 step 类型正确（参照 `TestSuiteBuilderViewModelTests.ToSuite_RoundTrips_Through_HILJsonOptions` 的断言方式）。
Expected: 两边格式互操作通过（这是格式冻结的验收）。

- [ ] **Step 2: 写 peakcan-studio README**

内容含：项目简介、构建/测试命令（`dotnet build` / `dotnet test`）、**格式冻结约束**段落（suite/script JSON 模型签名变更必须与 peakcan-host 同步）、与 peakcan-host 的关系说明。

- [ ] **Step 3: peakcan-host README 追加格式冻结约束**

在 peakcan-host README 的 HIL 相关段落追加：HIL Studio 已移至独立仓库 `peakcan-studio`（private）；suite/script 格式由两边共享模型保证，变更须同步。

- [ ] **Step 4: peakcan-studio 全量验证 + push**

```bash
cd /d/claude_proj2/peakcan-studio
dotnet build PeakCan.Studio.slnx && dotnet test PeakCan.Studio.slnx
git add -A && git commit -m "docs: README with build/test commands + format-freeze constraint"
git push -u origin main
```

- [ ] **Step 5: 汇报**

报告：Studio 仓库 URL、peakcan-host 移除结果、互操作验证证据、两仓库当前测试数。

---

## Self-Review 记录

- **Spec coverage**：仓库创建(T1) / Studio.Core 复制(T2) / Studio.App 复制(T3) / 测试(T4) / peakcan-host 移除(T5) / E2E+README(T6)——覆盖 spec 全部章节。
- **已知偏差**：spec 说"16 个 UI 文件"，plan 精确到 29 文件（含 DbcService/DispatcherExtensions/IMessageBoxPrompt 等依赖）；spec 说 NullToVisibilityConverter "若仅 Studio 用则删"，plan 已确认非仅 Studio 用故保留。
- **Type consistency**：`HilStudioViewModel` 构造签名 `(DbcService, ILogger<HilStudioViewModel>, IFileDialogService?, DbcEncodeService?)` 在 T3/T4 一致；`CanIdConfig` 命名空间 `PeakCan.Studio.Core.Uds.IsoTp` 在 T2 定义、T4 测试引用一致。
