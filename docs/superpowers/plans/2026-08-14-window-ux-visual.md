# Phase 2 视觉（浅色工程风 · 令牌统一）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 建立统一浅色工程视觉体系——Colors.xaml 语义令牌替换 15 个 XAML 的硬编码色、Segoe Fluent Icons 替换 emoji、输出/日志控制台改浅色、AppShell 布局持久化。

**Architecture:** 三层。① 令牌：`Themes/Colors.xaml`（SolidColorBrush 语义名）合并进 `App.xaml`，全应用 `StaticResource` 引用（单浅色主题，不做 DynamicResource）。② 图标：`Composition/Icons/FluentIconGlyphs.cs` 集中码点常量（`{x:Static}` 引用），`GlyphTypeface` 测试验证码点存在。③ 布局：`LayoutStateStore`（`%APPDATA%/PeakCan.Host/layout.json`）由 AppShell 代码后置在 SourceInitialized 恢复 / Closing 保存。

**Tech Stack:** WPF .NET 10 · CommunityToolkit.Mvvm · xUnit + FluentAssertions + NSubstitute · 系统字体 Segoe Fluent Icons（`C:\Windows\Fonts\SegoeIcons.ttf`）

**Spec:** [../specs/2026-08-14-window-ux-visual-design.md](../specs/2026-08-14-window-ux-visual-design.md)（含 §5 令牌全表、§6 图标意图、§7 硬编码色→令牌映射表）

## Global Constraints

- 单浅色主题；令牌一律 `StaticResource`；XAML 禁止裸 hex/命名色（§5 保留的数据色除外）
- 15 个 XAML 换令牌：`AppShell, DbcTreePickerWindow, DbcView, HilView, ReplayView, ScriptView, SendView, SignalView, TraceView, TraceViewerView, TraceViewerViewChatPanel, ConnectionSettingsWindow, EcuScriptEditorWindow, MultiFrameSendWindow, UdsWindow`
- 保留的数据色（不入令牌）：TraceViewerView 图表锚点 `Blue/Green/Red/White`；ChatPanel `#DCF8C6` 及消息类型 `Green/Red`；`Transparent` 任意可用
- WebView2 脚本编辑器保持深色（不换）；MultiFrame RowDetails、输出面板、UDS 日志改浅色
- 测试：App.Tests 全绿；令牌存在性 / 图标码点 / LayoutStateStore / AppShell 布局恢复测试通过
- 不改 Core/Infrastructure 接口契约（NetArchTest）；不重排布局结构

## File Structure

- `src/PeakCan.Host.App/Themes/Colors.xaml` — **新建**：27 语义令牌（§5 全表）+ 2 排版令牌
- `src/PeakCan.Host.App/App.xaml` — **改**：合并 Colors.xaml 到 Application.Resources
- `src/PeakCan.Host.App/Composition/Icons/FluentIconGlyphs.cs` — **新建**：Segoe Fluent 码点常量
- `src/PeakCan.Host.App/Composition/Converters/HilModeToIconConverter.cs` — **改**：emoji → 码点字符串
- `src/PeakCan.Host.App/Services/Ui/LayoutStateStore.cs` — **新建**：布局持久化（DTO + 原子写 + 容错）
- `src/PeakCan.Host.App/AppShell.xaml(.cs)` — **改**：镀铬令牌 + 图标 + 布局恢复/保存
- 15 个视图/窗口 XAML — **改**：按 §7 映射换令牌 + 图标
- 测试：`tests/PeakCan.Host.App.Tests/Themes/ColorTokensTests.cs`、`Composition/FluentIconGlyphsTests.cs`、`Services/Ui/LayoutStateStoreTests.cs`、`NoRawColorGuardTests.cs`、AppShell 布局 STA 测试（新建）

---

### Task 1: Colors.xaml 令牌字典 + 注册 + 存在性测试（P2-1）

**Files:**
- Create: `src/PeakCan.Host.App/Themes/Colors.xaml`
- Modify: `src/PeakCan.Host.App/App.xaml`
- Test: `tests/PeakCan.Host.App.Tests/Themes/ColorTokensTests.cs`

**Interfaces:**
- Produces: 27 个 `SolidColorBrush` + 2 个 `FontFamily` 资源，Key 名见 §5，被 Task 3-7 的 XAML 引用

- [ ] **Step 1: 写失败测试**（先建 Colors.xaml 为空字典占位，让测试编译）

创建 `tests/PeakCan.Host.App.Tests/Themes/ColorTokensTests.cs`：

```csharp
using System.IO;
using System.Windows;
using System.Windows.Media;
using FluentAssertions;
using PeakCan.Host.App.Tests.Collections;
using Xunit;

namespace PeakCan.Host.App.Tests.Themes;

/// <summary>P2-1: 令牌全集存在性 + 色值。用 XamlReader 解析 Colors.xaml
/// （无 Application 依赖），断言每个语义令牌都是期望的 SolidColorBrush。
/// 防止实现时漏令牌或写错色值。</summary>
[Collection(WpfAppTestCollection.Name)]
public class ColorTokensTests
{
    private static readonly string Path =
        System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PeakCan.Host.App", "Themes", "Colors.xaml");

    private static ResourceDictionary Load()
    {
        var xaml = File.ReadAllText(System.IO.Path.GetFullPath(Path));
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    [Theory]
    [InlineData("CanvasBg", "#F3F4F6")]
    [InlineData("Surface", "#FFFFFF")]
    [InlineData("SurfaceSubtle", "#F7F8FA")]
    [InlineData("RowAlternate", "#F7F8FA")]
    [InlineData("RowHover", "#EDF2F9")]
    [InlineData("RowSelected", "#DCECFB")]
    [InlineData("Border", "#D4D9DF")]
    [InlineData("BorderSubtle", "#E4E8EC")]
    [InlineData("Divider", "#E9EDF1")]
    [InlineData("TextPrimary", "#1B1F24")]
    [InlineData("TextSecondary", "#5A6470")]
    [InlineData("TextDisabled", "#9AA3AE")]
    [InlineData("TextOnAccent", "#FFFFFF")]
    [InlineData("Accent", "#0B5CAD")]
    [InlineData("AccentHover", "#094B8F")]
    [InlineData("AccentPressed", "#073D75")]
    [InlineData("Ok", "#1A7F37")]
    [InlineData("OkBg", "#E6F4EA")]
    [InlineData("WarnText", "#8A5B00")]
    [InlineData("WarnBg", "#FFF6E0")]
    [InlineData("WarnBorder", "#E3B64B")]
    [InlineData("Error", "#D62728")]
    [InlineData("ErrorBg", "#FDEBEC")]
    [InlineData("Info", "#0B5CAD")]
    [InlineData("FrameBgFd", "#E3F2FD")]
    [InlineData("FrameBgError", "#FFCDD2")]
    [InlineData("FrameBgHighlight", "#FFFDE7")]
    [InlineData("ConsoleBg", "#FBFBFC")]
    [InlineData("ConsoleFg", "#24292F")]
    [InlineData("ConsoleAccent", "#0550AE")]
    public void Token_Exists_With_Expected_Color(string key, string hex)
    {
        var rd = Load();
        var brush = rd[key].Should().BeOfType<SolidColorBrush>().Subject;
        var expected = (Color)ColorConverter.ConvertFromString(hex);
        brush.Color.Should().Be(expected, $"{key} 色值必须与 §5 一致");
    }

    [Theory]
    [InlineData("FontMono", "Consolas")]
    [InlineData("FontUI", "Segoe UI")]
    public void FontToken_Exists(string key, string family)
    {
        var rd = Load();
        rd[key].Should().BeOfType<FontFamily>()
            .Which.Source.Should().Be(family);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~ColorTokensTests"`
Expected: FAIL（Colors.xaml 不存在 / 空字典，Key 未找到）

- [ ] **Step 3: 写 Colors.xaml**（§5 全表，直接照抄）

创建 `src/PeakCan.Host.App/Themes/Colors.xaml`：

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- 表面 / 层级 -->
    <SolidColorBrush x:Key="CanvasBg" Color="#F3F4F6" />
    <SolidColorBrush x:Key="Surface" Color="#FFFFFF" />
    <SolidColorBrush x:Key="SurfaceSubtle" Color="#F7F8FA" />
    <SolidColorBrush x:Key="RowAlternate" Color="#F7F8FA" />
    <SolidColorBrush x:Key="RowHover" Color="#EDF2F9" />
    <SolidColorBrush x:Key="RowSelected" Color="#DCECFB" />
    <!-- 边框 / 分隔 -->
    <SolidColorBrush x:Key="Border" Color="#D4D9DF" />
    <SolidColorBrush x:Key="BorderSubtle" Color="#E4E8EC" />
    <SolidColorBrush x:Key="Divider" Color="#E9EDF1" />
    <!-- 文本 -->
    <SolidColorBrush x:Key="TextPrimary" Color="#1B1F24" />
    <SolidColorBrush x:Key="TextSecondary" Color="#5A6470" />
    <SolidColorBrush x:Key="TextDisabled" Color="#9AA3AE" />
    <SolidColorBrush x:Key="TextOnAccent" Color="#FFFFFF" />
    <!-- 强调色 -->
    <SolidColorBrush x:Key="Accent" Color="#0B5CAD" />
    <SolidColorBrush x:Key="AccentHover" Color="#094B8F" />
    <SolidColorBrush x:Key="AccentPressed" Color="#073D75" />
    <!-- 语义状态 -->
    <SolidColorBrush x:Key="Ok" Color="#1A7F37" />
    <SolidColorBrush x:Key="OkBg" Color="#E6F4EA" />
    <SolidColorBrush x:Key="WarnText" Color="#8A5B00" />
    <SolidColorBrush x:Key="WarnBg" Color="#FFF6E0" />
    <SolidColorBrush x:Key="WarnBorder" Color="#E3B64B" />
    <SolidColorBrush x:Key="Error" Color="#D62728" />
    <SolidColorBrush x:Key="ErrorBg" Color="#FDEBEC" />
    <SolidColorBrush x:Key="Info" Color="#0B5CAD" />
    <SolidColorBrush x:Key="FrameBgFd" Color="#E3F2FD" />
    <SolidColorBrush x:Key="FrameBgError" Color="#FFCDD2" />
    <SolidColorBrush x:Key="FrameBgHighlight" Color="#FFFDE7" />
    <!-- 控制台 -->
    <SolidColorBrush x:Key="ConsoleBg" Color="#FBFBFC" />
    <SolidColorBrush x:Key="ConsoleFg" Color="#24292F" />
    <SolidColorBrush x:Key="ConsoleAccent" Color="#0550AE" />
    <!-- 排版 -->
    <FontFamily x:Key="FontMono">Consolas</FontFamily>
    <FontFamily x:Key="FontUI">Segoe UI</FontFamily>
</ResourceDictionary>
```

- [ ] **Step 4: App.xaml 合并注册**

在 `App.xaml` 的 `<Application.Resources>` 内、所有 converter 之前，加 MergedDictionaries：

```xml
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Themes/Colors.xaml" />
            </ResourceDictionary.MergedDictionaries>
            <!-- 现有 converter 原样保留在此 ResourceDictionary 内 -->
            <conv:NullToVisibilityConverter x:Key="NullToVisibilityConverter" />
            ...
        </ResourceDictionary>
    </Application.Resources>
```

> 注意：MergedDictionaries 必须是 ResourceDictionary 的第一个子元素，否则运行时解析不到令牌。现有 converter 行全部包进外层 `<ResourceDictionary>`。

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~ColorTokensTests"`
Expected: PASS（31 个 InlineData 全过）

- [ ] **Step 6: 提交**

```bash
git add src/PeakCan.Host.App/Themes/Colors.xaml src/PeakCan.Host.App/App.xaml tests/PeakCan.Host.App.Tests/Themes/ColorTokensTests.cs
git commit -m "feat(ui): add Colors.xaml semantic token dictionary (P2-1)"
```

---

### Task 2: Fluent 图标码点常量 + HIL 转换器 + 码点验证测试（P2-2）

**Files:**
- Create: `src/PeakCan.Host.App/Composition/Icons/FluentIconGlyphs.cs`
- Modify: `src/PeakCan.Host.App/Composition/Converters/HilModeToIconConverter.cs`
- Test: `tests/PeakCan.Host.App.Tests/Composition/FluentIconGlyphsTests.cs`

**Interfaces:**
- Produces: `FluentIconGlyphs` 的 `public const string` 码点（XAML 用 `{x:Static icons:FluentIconGlyphs.X}` 引用，Task 3-6 用到）；HIL 转换器返回码点字符串（HilView ComboBox 用 `FontFamily="Segoe Fluent Icons"` 渲染）

- [ ] **Step 1: 写失败测试**

创建 `tests/PeakCan.Host.App.Tests/Composition/FluentIconGlyphsTests.cs`：

```csharp
using System.Collections.Generic;
using System.Windows.Media;
using FluentAssertions;
using PeakCan.Host.App.Composition.Icons;
using PeakCan.Host.App.Tests.Collections;
using Xunit;

namespace PeakCan.Host.App.Tests.Composition;

/// <summary>P2-2: 每个码点常量必须真实存在于已安装的 Segoe Fluent Icons
/// 字体（C:\Windows\Fonts\SegoeIcons.ttf）。防机型差异——码点不存在时
/// 测试失败而非运行时静默渲染成豆腐块。</summary>
[Collection(WpfAppTestCollection.Name)]
public class FluentIconGlyphsTests
{
    private static HashSet<int> LoadFontGlyphs()
    {
        var font = new GlyphTypeface(new System.Uri(
            "file:///C:/Windows/Fonts/SegoeIcons.ttf"));
        return new HashSet<int>(font.CharacterToGlyphMap.Keys);
    }

    public static IEnumerable<object[]> AllGlyphs()
    {
        yield return new object[] { FluentIconGlyphs.Settings };
        yield return new object[] { FluentIconGlyphs.Play };
        yield return new object[] { FluentIconGlyphs.Pause };
        yield return new object[] { FluentIconGlyphs.Stop };
        yield return new object[] { FluentIconGlyphs.Save };
        yield return new object[] { FluentIconGlyphs.OpenFolder };
        yield return new object[] { FluentIconGlyphs.Dismiss };
        yield return new object[] { FluentIconGlyphs.Search };
        yield return new object[] { FluentIconGlyphs.ChevronUp };
        yield return new object[] { FluentIconGlyphs.ChevronDown };
        yield return new object[] { FluentIconGlyphs.ChevronLeft };
        yield return new object[] { FluentIconGlyphs.ChevronRight };
        yield return new object[] { FluentIconGlyphs.Link };
        yield return new object[] { FluentIconGlyphs.Lightning };
        yield return new object[] { FluentIconGlyphs.Record };
        yield return new object[] { FluentIconGlyphs.Plug };
        yield return new object[] { FluentIconGlyphs.Power };
        yield return new object[] { FluentIconGlyphs.Bot };
        yield return new object[] { FluentIconGlyphs.Sparkle };
        yield return new object[] { FluentIconGlyphs.Replay };
        yield return new object[] { FluentIconGlyphs.Laptop };
        yield return new object[] { FluentIconGlyphs.Help };
    }

    [Theory]
    [MemberData(nameof(AllGlyphs))]
    public void Codepoint_Exists_In_Segoe_Fluent_Icons(string glyph)
    {
        glyph.Should().HaveLength(1, "每个常量是单字符码点");
        LoadFontGlyphs().Should().Contain(glyph[0],
            $"U+{(int)glyph[0]:X4} 必须存在于 SegoeIcons.ttf");
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~FluentIconGlyphsTests"`
Expected: FAIL（FluentIconGlyphs 类不存在）

- [ ] **Step 3: 写 FluentIconGlyphs.cs**（码点已在本机 SegoeIcons.ttf 逐一验证存在；Sparkle 用 E734 替代缺失的 E9CF，视觉待确认）

创建 `src/PeakCan.Host.App/Composition/Icons/FluentIconGlyphs.cs`：

```csharp
namespace PeakCan.Host.App.Composition.Icons;

/// <summary>
/// Segoe Fluent Icons（Windows 11 系统字体）单字符码点常量。
/// 所有码点经 <c>C:\Windows\Fonts\SegoeIcons.ttf</c> 验证存在（FluentIconGlyphsTests）。
/// XAML 用 <c>{x:Static icons:FluentIconGlyphs.X}</c> 引用，配合
/// <c>FontFamily="Segoe Fluent Icons"</c> 渲染。
/// </summary>
public static class FluentIconGlyphs
{
    public const string Settings     = "\uE713"; // ⚙ 设备设置
    public const string Record       = "\uE7C8"; // ● 录制
    public const string Play         = "\uE768"; // ▶ 运行
    public const string Pause        = "\uE769"; // ⏸ 暂停
    public const string Stop         = "\uE71A"; // ⏹ 停止
    public const string Save         = "\uE74E"; // 💾 保存
    public const string OpenFolder   = "\uE8B7"; // 📂 打开
    public const string Dismiss      = "\uE711"; // ✕ 关闭/清除
    public const string Search       = "\uE721"; // 🔍 搜索
    public const string ChevronUp    = "\uE74A"; // ▲ 上移
    public const string ChevronDown  = "\uE74B"; // ▼ 下移
    public const string ChevronLeft  = "\uE76B"; // ← 后退
    public const string ChevronRight = "\uE76C"; // → 前进
    public const string Link         = "\uE71B"; // 🔗 UDS 连接
    public const string Lightning    = "\uE945"; // ⚡ 闪电
    public const string Plug         = "\uE8A5"; // 连接 / HIL Hardware
    public const string Power        = "\uE94A"; // 断开
    public const string Bot          = "\uE9F5"; // 🤖 AI 聊天
    public const string Sparkle      = "\uE734"; // ✨ 格式化（FavoriteStar 替代，视觉待确认）
    public const string Replay       = "\uE81C"; // HIL TraceReplay（替代磁带）
    public const string Laptop       = "\uE7F8"; // HIL VirtualEcu
    public const string Help         = "\uE894"; // ❓ 未知/帮助
}
```

- [ ] **Step 4: 改 HilModeToIconConverter**——emoji 字符串换成码点

在 `Composition/Converters/HilModeToIconConverter.cs`，把 switch 的返回值改为 `FluentIconGlyphs` 常量（Hint：HilMode.TraceReplay→Replay、Hardware→Plug、VirtualEcu→Laptop、Matrix→Link、unknown/null→Help），文件头加 `using PeakCan.Host.App.Composition.Icons;`。

- [ ] **Step 5: 跑测试确认通过 + 视觉验证**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~FluentIconGlyphsTests"`
Expected: PASS

然后运行应用，打开 HIL 窗口，肉眼确认模式下拉的 5 个图标是期望形状（尤其 Sparkle 用 E734 是否可接受）。若某字形形状不符，改 `FluentIconGlyphs` 常量到相邻存在码点并重跑测试。

- [ ] **Step 6: 提交**

```bash
git add src/PeakCan.Host.App/Composition/Icons/FluentIconGlyphs.cs src/PeakCan.Host.App/Composition/Converters/HilModeToIconConverter.cs tests/PeakCan.Host.App.Tests/Composition/FluentIconGlyphsTests.cs
git commit -m "feat(ui): add Fluent icon glyph constants + HIL converter codepoints (P2-2)"
```

---

### Task 3: AppShell 镀铬换令牌 + 工具栏图标（P2-3）

**Files:**
- Modify: `src/PeakCan.Host.App/AppShell.xaml`

**Interfaces:**
- Consumes: `Colors.xaml` 令牌（Task 1）、`FluentIconGlyphs`（Task 2）
- Produces: 换令牌/图标的 AppShell 镀铬（后续 Task 4-7 按同样手法处理视图）

- [ ] **Step 1: 改 AppShell.xaml 的硬编码色 + emoji**

文件头加 `xmlns:icons="clr-namespace:PeakCan.Host.App.Composition.Icons"`。逐处替换：

| 位置 | 现值 | → |
|---|---|---|
| 工具栏"连接"按钮 | 默认 | 加 `Style`：Background=`{StaticResource Accent}` Foreground=`{StaticResource TextOnAccent}`，Hover→`AccentHover`、Pressed→`AccentPressed` |
| 工具栏"⚙ 设备设置" | `Content="⚙ 设备设置"` | `<StackPanel Orientation="Horizontal"><TextBlock Text="{x:Static icons:FluentIconGlyphs.Settings}" FontFamily="Segoe Fluent Icons" Margin="0,0,4,0"/><TextBlock Text="设备设置"/></StackPanel>` |
| 工具栏"● 录制" Toggle | `Content="● 录制"` | 同上用 `FluentIconGlyphs.Record`；红色圆点样式改用 `{StaticResource Error}` |
| 连接状态 `ConnectionState` | `Foreground="#1A7F37"` | `{StaticResource Ok}` |

- [ ] **Step 2: 构建 + 冒烟**

Run: `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -v q`
Expected: 0 errors；运行应用确认菜单/工具栏/连接态渲染正常、图标是单色字形

- [ ] **Step 3: 提交**

```bash
git add src/PeakCan.Host.App/AppShell.xaml
git commit -m "feat(ui): AppShell chrome to tokens + Fluent toolbar icons (P2-3)"
```

---

### Task 4: 视图换令牌 Batch A——Trace / Dbc / Send / Signal（P2-4）

**Files:**
- Modify: `src/PeakCan.Host.App/Views/TraceView.xaml`、`DbcView.xaml`、`SendView.xaml`、`SignalView.xaml`

**Interfaces:**
- Consumes: `Colors.xaml` 令牌（Task 1）
- Produces: 4 个视图无裸色

- [ ] **Step 1: TraceView.xaml**——帧状态行底色 + 斑马 + 灰文字

| 现值 | → |
|---|---|
| `IsError` DataTrigger Background `#FFCDD2` | `{StaticResource FrameBgError}` |
| `IsFd` DataTrigger Background `#E3F2FD` | `{StaticResource FrameBgFd}` |
| `IsHighlighted` DataTrigger Background `#FFFDE7` | `{StaticResource FrameBgHighlight}` |
| 斑马/表头 `#F8F8F8` | `{StaticResource RowAlternate}` 或 `SurfaceSubtle`（按语义） |
| `Foreground="#6e7781"`（计数说明文字） | `{StaticResource TextSecondary}` |

- [ ] **Step 2: DbcView.xaml**

| 现值 | → |
|---|---|
| `AlternatingRowBackground="#F8F8F8"` | `{StaticResource RowAlternate}` |
| 注释列 `Foreground="Gray"` | `{StaticResource TextSecondary}` |
| 搜索/状态栏 `Gray`（如有） | `{StaticResource TextSecondary}` |

- [ ] **Step 3: SendView.xaml**

| 现值 | → |
|---|---|
| 限流 chip `Background="#FFF8E1" BorderBrush="#D4A72C" Foreground="#7D4E00"` | `WarnBg` / `WarnBorder` / `WarnText` |
| `ErrorMessage` `Foreground="Red"` | `{StaticResource Error}` |
| 工具栏 `▶⏹📂💾` | 各包 TextBlock 用 `FluentIconGlyphs.Play/Stop/OpenFolder/Save`（FontFamily="Segoe Fluent Icons"） |

- [ ] **Step 4: SignalView.xaml**

| 现值 | → |
|---|---|
| 表头/斑马 `#F8F8F8` | `{StaticResource SurfaceSubtle}` / `RowAlternate` |
| GridSplitter/统计卡边 `#CCCCCC` | `{StaticResource BorderSubtle}` |

- [ ] **Step 5: 构建 + 相关测试**

Run: `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -v q` + `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~SendViewTests|FullyQualifiedName~DbcViewTests|FullyQualifiedName~SignalView"`（如有对应文本级测试）
Expected: 0 errors、相关测试 PASS

- [ ] **Step 6: 提交**

```bash
git add src/PeakCan.Host.App/Views/TraceView.xaml src/PeakCan.Host.App/Views/DbcView.xaml src/PeakCan.Host.App/Views/SendView.xaml src/PeakCan.Host.App/Views/SignalView.xaml
git commit -m "feat(ui): tokens for Trace/Dbc/Send/Signal views (P2-4a)"
```

---

### Task 5: 视图 Batch B——Script / Replay / Hil + 浅色控制台（P2-4 + P2-5）

**Files:**
- Modify: `src/PeakCan.Host.App/Views/ScriptView.xaml`、`ReplayView.xaml`、`HilView.xaml`

**Interfaces:**
- Consumes: `ConsoleBg/ConsoleFg/ConsoleAccent`（Task 1）、`FluentIconGlyphs`（Task 2）

- [ ] **Step 1: ScriptView.xaml——浅色控制台 + 编辑器保持深色 + 图标**

| 现值 | → |
|---|---|
| 输出面板 `Border Background="#1E1E1E"` | `{StaticResource ConsoleBg}` |
| 输出 `ListBox Foreground="#D4D4D4"` | `{StaticResource ConsoleFg}` |
| `FontFamily="Consolas"` | `{StaticResource FontMono}`（可选，保持显式亦可） |
| 编辑器 WebView2 `Border Background="#1E1E1E"` | **保持深色不动**（D4）；其内 fallback `Foreground="DarkRed"` → `{StaticResource Error}` |
| 工具栏 `▶⏹📂💾` | `FluentIconGlyphs.Play/Stop/OpenFolder/Save` |
| 工具栏/输出 `Foreground="Gray"` | `{StaticResource TextSecondary}` |

- [ ] **Step 2: ReplayView.xaml**

| 现值 | → |
|---|---|
| `Foreground="Red"` ×3（错误态） | `{StaticResource Error}` |
| `▶`（如播放按钮） | `FluentIconGlyphs.Play` |
| 任何 `Gray` | `{StaticResource TextSecondary}` |

- [ ] **Step 3: HilView.xaml**

| 现值 | → |
|---|---|
| `Foreground="DarkRed"` | `{StaticResource Error}` |
| `Gray` ×3（说明/占位） | `{StaticResource TextSecondary}` |
| 模式下拉图标 | 已由 Task 2 的转换器返回码点；确认 ItemTemplate 里 TextBlock 有 `FontFamily="Segoe Fluent Icons"`，否则加上 |

- [ ] **Step 4: 构建 + 冒烟**

Run: `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -v q`
Expected: 0 errors；运行应用看脚本视图输出面板为浅色、编辑器仍深色、HIL 图标为字形

- [ ] **Step 5: 提交**

```bash
git add src/PeakCan.Host.App/Views/ScriptView.xaml src/PeakCan.Host.App/Views/ReplayView.xaml src/PeakCan.Host.App/Views/HilView.xaml
git commit -m "feat(ui): Script/Replay/Hil tokens + light console + icons (P2-4b/P2-5)"
```

---

### Task 6: 窗口换令牌——Uds / MultiFrame / EcuScriptEditor / DbcTreePicker / ConnectionSettings（P2-4）

**Files:**
- Modify: `src/PeakCan.Host.App/Windows/UdsWindow.xaml`、`MultiFrameSendWindow.xaml`、`EcuScriptEditorWindow.xaml`、`Views/DbcTreePickerWindow.xaml`、`Windows/ConnectionSettingsWindow.xaml`

- [ ] **Step 1: UdsWindow.xaml——输出日志浅色 + 灰色 + 图标**

| 现值 | → |
|---|---|
| 底部输出日志 `Border/RichTextBox Background="#1E1E1E" Foreground="#D4D4D4"` | `ConsoleBg` / `ConsoleFg` |
| `Gray` ×4 | `{StaticResource TextSecondary}` 或 `Border`（按用途） |
| `Foreground="#F0F0F0"`（若有） | `{StaticResource SurfaceSubtle}` |
| `🔗`、`←` 等 emoji | `FluentIconGlyphs.Link`、`ChevronLeft` 等 |

- [ ] **Step 2: MultiFrameSendWindow.xaml**

| 现值 | → |
|---|---|
| RowDetails `Border Background="#1E1E1E" BorderBrush="#444"` | `{StaticResource SurfaceSubtle}` / `{StaticResource Border}` |
| 限流 chip `#FFF8E1/#D4A72C/#7D4E00` | `WarnBg` / `WarnBorder` / `WarnText` |
| `Gray`（状态文字） | `{StaticResource TextSecondary}` |
| `💾 Save current` 按钮 | TextBlock 用 `FluentIconGlyphs.Save` |

- [ ] **Step 3: EcuScriptEditorWindow.xaml**

| 现值 | → |
|---|---|
| `#CCCCCC` 边框 | `{StaticResource BorderSubtle}` |
| `Red` | `{StaticResource Error}` |
| `📂💾✨` | `FluentIconGlyphs.OpenFolder/Save/Sparkle` |

- [ ] **Step 4: DbcTreePickerWindow.xaml + ConnectionSettingsWindow.xaml**

`Gray`（提示文字/边框）→ `{StaticResource TextSecondary}` 或 `Border`（按用途）。

- [ ] **Step 5: 构建 + 冒烟**

Run: `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -v q`
Expected: 0 errors；运行应用逐个打开 5 个窗口，确认浅色/图标正常

- [ ] **Step 6: 提交**

```bash
git add src/PeakCan.Host.App/Windows/UdsWindow.xaml src/PeakCan.Host.App/Windows/MultiFrameSendWindow.xaml src/PeakCan.Host.App/Windows/EcuScriptEditorWindow.xaml src/PeakCan.Host.App/Views/DbcTreePickerWindow.xaml src/PeakCan.Host.App/Windows/ConnectionSettingsWindow.xaml
git commit -m "feat(ui): window tokens + UDS log light console + icons (P2-4c)"
```

---

### Task 7: TraceViewer / ChatPanel 令牌（数据色保留）（P2-4）

**Files:**
- Modify: `src/PeakCan.Host.App/Views/TraceViewerView.xaml`、`TraceViewerViewChatPanel.xaml`

**注意：这两个文件含§5 保留的数据色（图表锚点 Blue/Green/Red/White、ChatPanel #DCF8C6 消息 chip），只换 UI 镀铬，不动数据色。**

- [ ] **Step 1: TraceViewerView.xaml**

| 现值 | → |
|---|---|
| `#0066CC`（链接/信息） | `{StaticResource Accent}` 或 `Info` |
| `#D62728`（错误） | `{StaticResource Error}` |
| `#F4F4F4`（表头） | `{StaticResource SurfaceSubtle}` |
| `Gray` ×2 | `{StaticResource TextSecondary}` |
| **`Blue/Green/Red/White`（图表锚点/图例）** | **保留不动**（数据色） |
| `Transparent` | 保留 |

- [ ] **Step 2: TraceViewerViewChatPanel.xaml**

| 现值 | → |
|---|---|
| `#1565C0`（链接） | `{StaticResource Accent}` |
| `#FAFAFA` / `#EEEEEE`（表头/次级底） | `{StaticResource SurfaceSubtle}` |
| `Gray` ×2 | `{StaticResource TextSecondary}` |
| **`#DCF8C6`、`Green`、`Red`（消息类型 chip）** | **保留不动**（数据色） |
| `✕ 🔍 ⚙ 🤖 ⚡` | `FluentIconGlyphs.Dismiss/Search/Settings/Bot/Lightning` |

- [ ] **Step 3: 构建 + 冒烟**

Run: `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -v q`
Expected: 0 errors；打开追踪查看器确认图表锚点颜色未变、UI 镀铬换新

- [ ] **Step 4: 提交**

```bash
git add src/PeakCan.Host.App/Views/TraceViewerView.xaml src/PeakCan.Host.App/Views/TraceViewerViewChatPanel.xaml
git commit -m "feat(ui): TraceViewer/ChatPanel chrome tokens, keep data colors (P2-4d)"
```

---

### Task 8: 全量无裸色守卫测试（§9 验收强制）

**Files:**
- Test: `tests/PeakCan.Host.App.Tests/Themes/NoRawColorGuardTests.cs`

**Interfaces:**
- Consumes: Task 3-7 完成后的 15 个 XAML
- 守卫：任何回归重新引入裸 hex/命名色立即失败

- [ ] **Step 1: 写测试**（此刻 15 个 XAML 应已全部换完，测试应当直接通过）

创建 `tests/PeakCan.Host.App.Tests/Themes/NoRawColorGuardTests.cs`：

```csharp
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace PeakCan.Host.App.Tests.Themes;

/// <summary>
/// §9 验收强制：15 个 XAML 无裸 hex/命名色（§5 保留的数据色除外）。
/// 扫描前剥离 XML 注释，避免文档里的色值误报。
/// </summary>
public class NoRawColorGuardTests
{
    private static readonly string[] TokenizedFiles =
    {
        "AppShell.xaml",
        "Views/DbcTreePickerWindow.xaml", "Views/DbcView.xaml",
        "Views/HilView.xaml", "Views/ReplayView.xaml", "Views/ScriptView.xaml",
        "Views/SendView.xaml", "Views/SignalView.xaml", "Views/TraceView.xaml",
        "Views/TraceViewerView.xaml", "Views/TraceViewerViewChatPanel.xaml",
        "Windows/ConnectionSettingsWindow.xaml",
        "Windows/EcuScriptEditorWindow.xaml",
        "Windows/MultiFrameSendWindow.xaml", "Windows/UdsWindow.xaml",
    };

    // 每文件允许保留的数据色（§5）。
    private static readonly (string file, string[] hex, string[] named)[] Allow =
    {
        ("Views/TraceViewerView.xaml", new string[0],
            new[] { "Blue", "Green", "Red", "White" }),
        ("Views/TraceViewerViewChatPanel.xaml", new[] { "#DCF8C6" },
            new[] { "Green", "Red" }),
    };

    private static readonly Regex HexRe = new(@"#[0-9A-Fa-f]{6}\b");
    private static readonly Regex NamedRe =
        new(@"\b(Black|White|Gray|DarkGray|LightGray|DarkRed|Red|Green|Blue|DarkBlue|LightBlue|Silver|DimGray)\b");

    private static string StripComments(string xaml) =>
        Regex.Replace(xaml, @"<!--.*?-->", string.Empty,
            RegexOptions.Singleline);

    [Fact]
    public void No_Raw_Hex_Or_Named_Colors_In_Tokenized_Views()
    {
        var root = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "PeakCan.Host.App");

        foreach (var file in TokenizedFiles)
        {
            var text = StripComments(
                File.ReadAllText(Path.GetFullPath(Path.Combine(root, file))));
            var allowHex = Allow.FirstOrDefault(a => a.file == file).hex ?? new string[0];
            var allowNamed = Allow.FirstOrDefault(a => a.file == file).named ?? new string[0];

            var badHex = HexRe.Matches(text)
                .Select(m => m.Value).Where(h => !allowHex.Contains(h)).ToList();
            var badNamed = NamedRe.Matches(text)
                .Select(m => m.Value).Where(n => !allowNamed.Contains(n))
                .Where(n => n != "Transparent").ToList();

            badHex.Should().BeEmpty($"{file} 不得有裸 hex（用 Colors.xaml 令牌）");
            badNamed.Should().BeEmpty($"{file} 不得有裸命名色（用 Colors.xaml 令牌）");
        }
    }
}
```

- [ ] **Step 2: 跑测试确认通过**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~NoRawColorGuardTests"`
Expected: PASS。若某文件仍残留裸色，回 Task 3-7 修对应文件再跑（这是 §9 的强制收口）。

- [ ] **Step 3: 提交**

```bash
git add tests/PeakCan.Host.App.Tests/Themes/NoRawColorGuardTests.cs
git commit -m "test(ui): add no-raw-color guard across 15 XAML (P2-4 gate)"
```

---

### Task 9: LayoutStateStore 持久化（P2-6 infra）

**Files:**
- Create: `src/PeakCan.Host.App/Services/Ui/LayoutStateStore.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/Ui/LayoutStateStoreTests.cs`

**Interfaces:**
- Produces: `LayoutStateDto(double RightPanelWidth, int SelectedMainTabIndex, int SelectedRightTabIndex)` record；`LayoutStateStore(ILogger<LayoutStateStore>, string? overridePath)`；`Get()/Set(LayoutStateDto)`；`LoadAsync(CancellationToken)`；`DefaultPath()` = `%APPDATA%/PeakCan.Host/layout.json`

- [ ] **Step 1: 写失败测试**

创建 `tests/PeakCan.Host.App.Tests/Services/Ui/LayoutStateStoreTests.cs`（镜像 `WindowStateStoreTests` 结构）：

```csharp
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.Ui;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Ui;

public sealed class LayoutStateStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"layout-{System.Guid.NewGuid():N}.json");

    [Fact]
    public void Set_Then_Get_RoundTrips()
    {
        var store = new LayoutStateStore(NullLogger<LayoutStateStore>.Instance, TempPath());
        var dto = new LayoutStateDto(420.0, 2, 1);
        store.Set(dto);
        store.Get().Should().Be(dto);
    }

    [Fact]
    public async Task Persisted_File_Reloads_After_New_Instance()
    {
        var path = TempPath();
        new LayoutStateStore(NullLogger<LayoutStateStore>.Instance, path)
            .Set(new LayoutStateDto(350.0, 0, 2));

        var reloaded = new LayoutStateStore(NullLogger<LayoutStateStore>.Instance, path);
        await reloaded.LoadAsync(default);
        reloaded.Get().Should().Be(new LayoutStateDto(350.0, 0, 2));
    }

    [Fact]
    public void Missing_File_Loads_Empty()
    {
        var store = new LayoutStateStore(NullLogger<LayoutStateStore>.Instance, TempPath());
        store.Get().Should().BeNull();
    }

    [Fact]
    public void Corrupt_File_Loads_Empty_Without_Throwing()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ not json !!!");
        var store = new LayoutStateStore(NullLogger<LayoutStateStore>.Instance, path);
        store.LoadAsync(default).GetAwaiter().GetResult();
        store.Get().Should().BeNull();
    }

    [Fact]
    public void Oversized_File_Is_Treated_As_Empty()
    {
        var path = TempPath();
        File.WriteAllText(path, new string('x', (int)LayoutStateStore.MaxLoadFileBytes + 1));
        var store = new LayoutStateStore(NullLogger<LayoutStateStore>.Instance, path);
        store.LoadAsync(default).GetAwaiter().GetResult();
        store.Get().Should().BeNull();
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~LayoutStateStoreTests"`
Expected: FAIL（类型不存在）

- [ ] **Step 3: 写 LayoutStateStore.cs**（镜像 `WindowStateStore`：原子 tmp+rename、损坏/超大容错、schema 信封）

创建 `src/PeakCan.Host.App/Services/Ui/LayoutStateStore.cs`：

```csharp
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services.Ui;

public sealed record LayoutStateDto(
    [property: JsonPropertyName("rightPanelWidth")] double RightPanelWidth,
    [property: JsonPropertyName("selectedMainTab")] int SelectedMainTabIndex,
    [property: JsonPropertyName("selectedRightTab")] int SelectedRightTabIndex);

/// <summary>P2-6: AppShell 内布局持久化（右栏宽 / 主右 tab 选中项）到
/// <c>%APPDATA%/PeakCan.Host/layout.json</c>。镜像 <see cref="WindowStateStore"/>
/// 文件契约：schema 信封、原子 tmp+rename、损坏/超大容错。</summary>
public sealed partial class LayoutStateStore
{
    private const string CurrentSchema = "layout/v1";

    public const long MaxLoadFileBytes = 1 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly ILogger<LayoutStateStore> _logger;
    private LayoutStateDto? _state;

    public LayoutStateStore(ILogger<LayoutStateStore> logger)
        : this(logger, null) { }

    public LayoutStateStore(ILogger<LayoutStateStore> logger, string? overridePath)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _path = overridePath ?? DefaultPath();
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
        }
    }

    public LayoutStateDto? Get() => _state;

    public void Set(LayoutStateDto state)
    {
        _state = state;
        Persist();
    }

    public Task LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _state = null;
        if (!File.Exists(_path)) return Task.CompletedTask;
        var info = new FileInfo(_path);
        if (info.Length > MaxLoadFileBytes)
        {
            LogOversized(_logger, _path, info.Length, MaxLoadFileBytes);
            return Task.CompletedTask;
        }
        try
        {
            var json = File.ReadAllText(_path);
            _state = JsonSerializer.Deserialize<Envelope>(json, JsonOpts)?.Layout;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            LogCorrupt(_logger, _path, ex);
        }
        return Task.CompletedTask;
    }

    private void Persist()
    {
        var dto = new Envelope { Layout = _state };
        var json = JsonSerializer.Serialize(dto, JsonOpts);
        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            LogSaveFailed(_logger, ex, _path);
        }
    }

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PeakCan.Host", "layout.json");

    [LoggerMessage(Level = LogLevel.Error, Message = "Layout-state file corrupt or unreadable: {Path}")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Layout-state file exceeds size cap ({Actual} > {Cap} bytes), treating as empty: {Path}")]
    private static partial void LogOversized(ILogger logger, string path, long actual, long cap);

    [LoggerMessage(Level = LogLevel.Error, Message = "LayoutStateStore save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);

    public sealed class Envelope
    {
        [JsonPropertyName("version")] public string Version { get; set; } = CurrentSchema;
        [JsonPropertyName("layout")] public LayoutStateDto? Layout { get; set; }
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~LayoutStateStoreTests"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/PeakCan.Host.App/Services/Ui/LayoutStateStore.cs tests/PeakCan.Host.App.Tests/Services/Ui/LayoutStateStoreTests.cs
git commit -m "feat(ui): add LayoutStateStore for AppShell layout persistence (P2-6)"
```

---

### Task 10: AppShell 布局恢复/保存 + STA 测试（P2-6 wiring）

**Files:**
- Modify: `src/PeakCan.Host.App/AppShell.xaml`（右栏 ColumnDefinition 加 x:Name）
- Modify: `src/PeakCan.Host.App/AppShell.xaml.cs`（SourceInitialized 恢复 + Closing 保存）
- Test: `tests/PeakCan.Host.App.Tests/Windows/AppShellLayoutPersistenceTests.cs`

**Interfaces:**
- Consumes: `LayoutStateStore`（Task 9）；VM 已有 `SelectedMainTabIndex` / `SelectedRightTabIndex`（P1-5）
- Produces: 重启后 splitter 位置 / 右栏宽 / 主右 tab 选中项还原

- [ ] **Step 1: 写失败测试**（验证保存/恢复行为，STA + Application）

创建 `tests/PeakCan.Host.App.Tests/Windows/AppShellLayoutPersistenceTests.cs`：

```csharp
using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.Ui;
using PeakCan.Host.App.Tests.Collections;
using Xunit;

namespace PeakCan.Host.App.Tests.Windows;

/// <summary>P2-6: AppShell 关闭保存布局、重开恢复（右栏宽 + tab 选中）。
/// 用真实 AppShell 走完整 SourceInitialized/Closing 生命周期。</summary>
[Collection(WpfAppTestCollection.Name)]
public class AppShellLayoutPersistenceTests
{
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static LayoutStateStore MakeStore() => new(
        NullLogger<LayoutStateStore>.Instance,
        Path.Combine(Path.GetTempPath(), $"appshell-layout-{Guid.NewGuid():N}.json"));

    [Fact]
    public void Closing_Saves_And_Reopen_Restores_Layout()
    {
        Exception? err = null;
        var store = MakeStore();
        var t = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var shell = new AppShell { WindowStateStore = null, LayoutStateStore = store };
                app.MainWindow = shell;
                shell.Show();
                Pump();
                // 人为改动布局，模拟用户拖 splitter / 切 tab
                shell.TestSetLayout(420.0, 2, 1);
                Pump();
                shell.Close();
                app.Shutdown();
            }
            catch (Exception ex) { err = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(TimeSpan.FromSeconds(30));
        if (t.IsAlive) throw new TimeoutException("STA deadlock");
        if (err is not null) throw err;

        store.Get().Should().NotBeNull("Closing 必须保存布局");
        store.Get()!.RightPanelWidth.Should().BeApproximately(420.0, 0.01);
        store.Get()!.SelectedMainTabIndex.Should().Be(2);
        store.Get()!.SelectedRightTabIndex.Should().Be(1);
    }
}
```

> 说明：`shell.TestSetLayout(width, mainIdx, rightIdx)` 是 AppShell 内部测试挂钩，见 Step 3（`InternalsVisibleTo` 已覆盖 App.Tests）。测试先编译失败没关系，Step 3 补上挂钩。

- [ ] **Step 2: AppShell.xaml**——右栏 ColumnDefinition 命名

在主内容区 Grid 的右栏 `<ColumnDefinition Width="300" MinWidth="220" />` 改为 `<ColumnDefinition x:Name="RightPanelColumn" Width="300" MinWidth="220" />`。

- [ ] **Step 3: AppShell.xaml.cs**——注入 + 恢复/保存 + 测试挂钩

```csharp
/// <summary>P2-6: 布局持久化（右栏宽 + 主右 tab 选中项）。</summary>
public LayoutStateStore? LayoutStateStore { get; set; }

private void OnSourceInitialized(object? sender, EventArgs e)
{
    SourceInitialized -= OnSourceInitialized;
    if (WindowStateStore is not null)
        WindowHostService.ApplyStoredState(this, WindowKey.AppShell, WindowStateStore);
    RestoreLayout();
    if (DataContext is AppShellViewModel shell)
        shell.ShowTraceCommand.Execute(null);
}

private void OnClosing(object? sender, CancelEventArgs e)
{
    if (WindowStateStore is not null)
        WindowHostService.SaveState(this, WindowKey.AppShell, WindowStateStore);
    SaveLayout();
}

private void RestoreLayout()
{
    if (LayoutStateStore is null || DataContext is not AppShellViewModel shell) return;
    var s = LayoutStateStore.Get();
    if (s is null) return;
    if (s.RightPanelWidth > 0) RightPanelColumn.Width = new GridLength(s.RightPanelWidth);
    shell.SelectedMainTabIndex = s.SelectedMainTabIndex;
    shell.SelectedRightTabIndex = s.SelectedRightTabIndex;
}

private void SaveLayout()
{
    if (LayoutStateStore is null || DataContext is not AppShellViewModel shell) return;
    LayoutStateStore.Set(new LayoutStateDto(
        RightPanelColumn.Width.Value, shell.SelectedMainTabIndex, shell.SelectedRightTabIndex));
}

// 测试挂钩：非 UI 线程注入布局（真实操作由用户拖 splitter / 切 tab 完成）
internal void TestSetLayout(double rightPanelWidth, int mainTab, int rightTab)
{
    RightPanelColumn.Width = new GridLength(rightPanelWidth);
    if (DataContext is AppShellViewModel shell)
    {
        shell.SelectedMainTabIndex = mainTab;
        shell.SelectedRightTabIndex = rightTab;
    }
}
```

> `OnSourceInitialized` 现在调用 `RestoreLayout()`（在 ShowTrace 前）；`OnClosing` 调 `SaveLayout()`。注意 `RestoreLayout`/`SaveLayout` 依赖 `DataContext is AppShellViewModel`——`AppShell` 必须用真实 `AppShellViewModel` 作 DataContext 才生效（测试里用真实 VM，Task 10 Step 1 的测试用 `DataContext = null` 会跳过——因此测试需构造真实 VM，参考 `UdsWindowTests.NewVm` 的依赖清单）。

- [ ] **Step 4: 修正 Step 1 测试用真实 VM + AppHostBuilder 注册**

- AppShell 测试需 `shell.DataContext = <真实 AppShellViewModel>`（用 `UdsWindowTests.NewVm` 同款依赖构造，或从 DI 容器解析）。
- 生产接线：在 `AppHostBuilder`（`AppHostBuilder.cs`）的 AppShell 构造处，注入 `LayoutStateStore`（`AddSingleton<LayoutStateStore>` + `sp.GetRequiredService<LayoutStateStore>()` 赋给 `AppShell.LayoutStateStore`），并调用 `LayoutStateStore.LoadAsync`（启动时一次，镜像 `WindowStateStore`）。
- 用真实 VM 后，`TestSetLayout` 的 VM 分支生效，测试断言 `store.Get()` 非空且值正确。

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~AppShellLayoutPersistenceTests"`
Expected: PASS

- [ ] **Step 5: 全量回归**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo`
Expected: 全绿（0 failed）

- [ ] **Step 6: 提交**

```bash
git add src/PeakCan.Host.App/AppShell.xaml src/PeakCan.Host.App/AppShell.xaml.cs src/PeakCan.Host.App/Composition/AppHostBuilder.cs tests/PeakCan.Host.App.Tests/Windows/AppShellLayoutPersistenceTests.cs
git commit -m "feat(ui): persist/restore AppShell layout via LayoutStateStore (P2-6)"
```

---

## Self-Review

**Spec coverage：**
- §5 令牌全集 → Task 1（Colors.xaml + 值测试）✓
- §6 图标 + HIL 转换器 → Task 2 ✓；各视图 emoji 替换 → Task 3/4/5/6/7 ✓
- §4 D4 浅色控制台（输出面板 + UDS 日志 + MultiFrame RowDetails）→ Task 5/6 ✓；WebView2 深色保留 → Task 5 明确不动 ✓
- §7 映射表 → Task 3-7 按文件应用 ✓；数据色保留边界 → Task 7 ✓
- §8 P2-6 布局持久化 → Task 9（store）+ Task 10（wiring）✓
- §9 验收：无裸色守卫 → Task 8 ✓；令牌/图标/Layout 测试 → Task 1/2/9/10 ✓

**Placeholder scan：** 无 TBD/TODO；每个 code step 有真实代码或具体映射。HIL 图标"视觉待确认"是有意的验收步骤（spec §10 开放项），非占位。

**Type consistency：** `LayoutStateDto` 三字段名在 Task 9（定义）与 Task 10（使用）一致；`FluentIconGlyphs` 常量名在 Task 2（定义）与 Task 3-7（XAML 引用）一致；`AppShell.LayoutStateStore` 属性名与测试一致。
