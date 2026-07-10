# peakcan-host v3.11.3 PATCH — UDS UserControl → Window refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert the UDS diagnostic surface from an in-place `UserControl` tab into a separate, non-modal `Window` that opens from the AppShell View menu — mirroring the `TraceViewerView` / `MultiFrameSendWindow` precedent. This is a layout/UX refactor only: VM is unchanged, XAML body is unchanged, all bindings resolve identically once the root type changes from `UserControl` to `Window`.

**Architecture:** Replace `src/PeakCan.Host.App/Views/UdsView.xaml(.cs)` with `src/PeakCan.Host.App/Windows/UdsWindow.xaml(.cs)` (root type `Window`). Update `AppShellViewModel`:
- `_udsView` field (`UdsView?`) → `_udsWindow` field (`UdsWindow?`)
- `ShowUds` command body: `ViewSwitcher.Show(...)` → `ViewSwitcher.ShowWindow(...)` + Owner assignment + `Show()`/`Activate()` (mirror the v3.9.1 PATCH B1 pattern already in place for `ShowTraceViewer`).

Remove `UdsView` from `AppShell.xaml`'s `MainArea` candidates: nothing changes in `AppShell.xaml` because it does not name `UdsView` directly. The menu's `Command="{Binding ShowUdsCommand}"` binding is preserved. No `AppHostBuilder` changes — UDS VM is already a DI singleton.

**Tech Stack:** WPF .NET 10 + CommunityToolkit.Mvvm 8.x. Pattern reuses `ViewSwitcher.ShowWindow` (v3.11.1 PATCH M3) and the Trace Viewer Owner/Closed-reset precedent (v3.9.1 PATCH B1).

## Global Constraints

- **No production code regression** — `UdsViewModel` is unchanged. `UdsWindow` is a copy of `UdsView` with root type swapped + namespace moved.
- **No new DI registrations** — UDS VM is already a singleton in `AppHostBuilder.cs:615`.
- **Owner cascade-close** — `_udsWindow.Owner = Application.Current.MainWindow` (mirrors v3.9.1 PATCH B1 + OpenMultiFrame precedent at `AppShellViewModel.cs:641`).
- **Closed-reset cache** — wire via `ViewSwitcher.ShowWindow` (auto-resets on `Closed` event).
- **Tests must pass STA via `[Collection(WpfAppTestCollection.Name)]`** — `Window` ctor is STA-bound, same as `UserControl`.
- **No new public API surface** — `ShowUdsCommand` source-generated name unchanged. XAML `Command="{Binding ShowUdsCommand}"` menu binding unchanged.
- **No schema changes, no .tmtrace changes, no DBC changes.**
- **Test delta**: 1266 + 5 SKIP / 0 fail → 1267 + 5 SKIP / 0 fail (+1 active).
- **Plan: only edit the layout + AppShell wiring. Do NOT touch UdsViewModel or any of the Uds/ sub-folders.**

---

## File Structure

### Create
- `src/PeakCan.Host.App/Windows/UdsWindow.xaml` — root `Window`, body copied from `UdsView.xaml`.
- `src/PeakCan.Host.App/Windows/UdsWindow.xaml.cs` — root `Window`, code-behind copied from `UdsView.xaml.cs` (OutputLog attach/detach + `Session` dispose).
- `tests/PeakCan.Host.App.Tests/Windows/UdsWindowTests.cs` — +1 STA test (`ShowUdsCommand_Opens_UdsWindow_With_Cached_Instance`).

### Delete
- `src/PeakCan.Host.App/Views/UdsView.xaml`
- `src/PeakCan.Host.App/Views/UdsView.xaml.cs`

### Modify
- `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs`
  - Field `_udsView : UdsView?` → `_udsWindow : UdsWindow?`
  - `ShowUds` body → `ViewSwitcher.ShowWindow(...)` + Owner + Show/Activate (mirror `ShowTraceViewer` at lines 666–694).
- `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs` — no test body changes needed (no `ShowUdsCommand_Is_Not_Null_And_Can_Execute` exists today; the new test goes in the new `UdsWindowTests.cs`).

### No-op files
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` — UDS VM already registered as singleton; no change.
- `src/PeakCan.Host.App/AppShell.xaml` — menu binding `Command="{Binding ShowUdsCommand}"` is unchanged.
- `src/PeakCan.Host.App/App.xaml(.cs)` — no global converters/resources needed.
- `tests/PeakCan.Host.App.Tests/Composition/AppHostBuilderTests.cs` — `UdsViewModel` registration test unchanged.

---

### Task 1: Create `UdsWindow.xaml` (root `Window`)

**Files:**
- Create: `src/PeakCan.Host.App/Windows/UdsWindow.xaml`

**Consumes:** Nothing (XAML body copied verbatim from `UdsView.xaml`).
**Produces:** `UdsWindow` XAML root — a WPF `Window` (not `UserControl`) hosting the same `DockPanel`/TabControl/RichTextBox layout.

- [ ] **Step 1: Copy `UdsView.xaml` body to a new file**

Create `src/PeakCan.Host.App/Windows/UdsWindow.xaml` with EXACTLY this content:

```xml
<Window x:Class="PeakCan.Host.App.Windows.UdsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
        xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
        xmlns:vm="clr-namespace:PeakCan.Host.App.ViewModels.Uds"
        mc:Ignorable="d"
        d:DataContext="{d:DesignInstance Type=vm:UdsViewModel}"
        Title="UDS Diagnostics"
        Width="1100" Height="700"
        WindowStartupLocation="CenterOwner"
        ShowInTaskbar="True">

    <DockPanel>
        <!-- Top: Session header strip -->
        <Border DockPanel.Dock="Top" Padding="8" Background="#F0F0F0">
            <StackPanel Orientation="Horizontal">
                <Button Content="Default"     Command="{Binding Session.SetDefaultSessionCommand}"   Padding="6,2" Margin="0,0,4,0"/>
                <Button Content="Extended"    Command="{Binding Session.SetExtendedSessionCommand}"  Padding="6,2" Margin="0,0,4,0"/>
                <Button Content="Programming" Command="{Binding Session.SetProgrammingSessionCommand}" Padding="6,2" Margin="0,0,12,0"/>
                <Separator/>
                <Button Content="Load ODX…"
                        Command="{Binding LoadOdxCommand}"
                        Padding="6,2" Margin="0,0,12,0"/>
                <CheckBox Content="TesterPresent" IsChecked="{Binding Session.TesterPresentActive, Mode=OneWay}"
                          Command="{Binding Session.ToggleTesterPresentCommand}"
                          VerticalAlignment="Center" Margin="0,0,12,0"/>
                <Separator/>
                <Button Content="SecurityAccess (Level 1)"
                        Command="{Binding Session.SecurityAccessCommand}"
                        Padding="6,2" Margin="0,0,12,0"/>
                <TextBlock Text="{Binding Session.CurrentSession, StringFormat='Session: {0}'}"
                           VerticalAlignment="Center" Margin="0,0,12,0"/>
                <TextBlock Text="{Binding Session.SecurityLevel, StringFormat='Level: 0x{0:X2}', TargetNullValue='(not authenticated)'}"
                           VerticalAlignment="Center"/>
            </StackPanel>
        </Border>

        <!-- Bottom: Output Log -->
        <Border DockPanel.Dock="Bottom" Padding="8" Background="#1E1E1E">
            <DockPanel>
                <Button DockPanel.Dock="Top" Content="Clear" HorizontalAlignment="Right"
                        Command="{Binding ClearOutputCommand}" Margin="0,0,0,4"/>
                <RichTextBox x:Name="LogBox" IsReadOnly="True" IsDocumentEnabled="True"
                             Background="#1E1E1E" Foreground="#D4D4D4"
                             FontFamily="Consolas" FontSize="12"
                             VerticalScrollBarVisibility="Auto">
                    <FlowDocument PageWidth="2400">
                        <Paragraph x:Name="LogParagraph"/>
                    </FlowDocument>
                </RichTextBox>
            </DockPanel>
        </Border>

        <!-- Middle: TabControl (DIDs / Routines / DTCs) -->
        <TabControl>
            <TabItem Header="DIDs">
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="2*"/>
                        <ColumnDefinition Width="3*"/>
                    </Grid.ColumnDefinitions>
                    <DataGrid ItemsSource="{Binding Did.Dids}" SelectedItem="{Binding Did.SelectedDid}"
                              AutoGenerateColumns="False" IsReadOnly="True">
                        <DataGrid.Columns>
                            <DataGridTextColumn Header="ID"     Binding="{Binding Id, StringFormat=0x{0:X4}}"/>
                            <DataGridTextColumn Header="Name"   Binding="{Binding Name}"/>
                            <DataGridTextColumn Header="Length" Binding="{Binding LengthBytes}"/>
                            <DataGridTextColumn Header="R/W"    Binding="{Binding WritableDisplay}"/>
                            <DataGridTextColumn Header="Value"  Binding="{Binding ReadValue}"/>
                        </DataGrid.Columns>
                    </DataGrid>
                    <StackPanel Grid.Column="1" Margin="12">
                        <TextBlock Text="{Binding Did.SelectedDid.Name, StringFormat='Selected DID: {0}', TargetNullValue='(none selected)'}"
                                   FontWeight="Bold"/>
                        <TextBlock Text="{Binding Did.SelectedDid.LengthBytes, StringFormat='Length: {0} bytes', TargetNullValue=''}"
                                   Margin="0,4,0,0"/>
                        <TextBlock Text="Write value (hex)" Margin="0,8,0,0"/>
                        <TextBox  Text="{Binding Did.WriteValue, UpdateSourceTrigger=PropertyChanged}"
                                  IsEnabled="{Binding Did.SelectedDid.Writable}"/>
                        <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
                            <Button Content="Read"  Command="{Binding Did.ReadDidCommand}"  Padding="6,2" Margin="0,0,8,0"/>
                            <Button Content="Write" Command="{Binding Did.WriteDidCommand}" Padding="6,2"/>
                        </StackPanel>
                        <TextBlock Text="{Binding Did.LastResult, StringFormat='Last: {0}'}"
                                   Margin="0,8,0,0" FontFamily="Consolas" TextWrapping="Wrap"/>
                    </StackPanel>
                </Grid>
            </TabItem>

            <TabItem Header="Routines">
                <DockPanel>
                    <!-- v2.0.6 PATCH Bug-4: see UdsView.xaml history. Reorder: button row docks to Top. -->
                    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="8">
                        <Button Content="Start" Command="{Binding Routine.StartRoutineCommand}" Padding="8,3" MinWidth="72" Margin="0,0,6,0"/>
                        <Button Content="Stop"  Command="{Binding Routine.StopRoutineCommand}"  Padding="8,3" MinWidth="72" Margin="0,0,6,0"/>
                        <Button Content="Query" Command="{Binding Routine.QueryRoutineCommand}" Padding="8,3" MinWidth="72" Margin="0,0,12,0"/>
                        <TextBlock Text="{Binding Routine.SelectedRoutine.LastResult, StringFormat='Last: {0}'}"
                                   Margin="4,0,0,0" VerticalAlignment="Center" FontFamily="Consolas"/>
                    </StackPanel>
                    <DataGrid ItemsSource="{Binding Routine.Routines}" SelectedItem="{Binding Routine.SelectedRoutine}"
                              AutoGenerateColumns="False" IsReadOnly="True">
                        <DataGrid.Columns>
                            <DataGridTextColumn Header="ID"     Binding="{Binding Id, StringFormat=0x{0:X4}}"/>
                            <DataGridTextColumn Header="Name"   Binding="{Binding Name}"/>
                            <DataGridTextColumn Header="Status" Binding="{Binding Status}"/>
                            <DataGridTextColumn Header="Result" Binding="{Binding LastResult}"/>
                        </DataGrid.Columns>
                    </DataGrid>
                </DockPanel>
            </TabItem>

            <TabItem Header="DTCs">
                <DockPanel>
                    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="8">
                        <Button Content="Read DTCs"  Command="{Binding Dtc.ReadDtcsCommand}" Padding="6,2"/>
                        <Button Content="Clear DTCs" Command="{Binding Dtc.ClearDtcsCommand}" Padding="6,2" Margin="8,0,0,0"/>
                    </StackPanel>
                    <DataGrid ItemsSource="{Binding Dtc.Dtcs}" AutoGenerateColumns="False" IsReadOnly="True">
                        <DataGrid.Columns>
                            <DataGridTextColumn Header="Code"        Binding="{Binding CodeDisplay}"/>
                            <DataGridTextColumn Header="Status"      Binding="{Binding StatusDisplay}"/>
                            <DataGridTextColumn Header="Description" Binding="{Binding Description}" Width="*"/>
                        </DataGrid.Columns>
                    </DataGrid>
                </DockPanel>
            </TabItem>
        </TabControl>
    </DockPanel>
</Window>
```

Notes vs `UdsView.xaml`:
- Root: `<UserControl ...>` → `<Window ...>` with `Title="UDS Diagnostics"`, `Width="1100"`, `Height="700"`, `WindowStartupLocation="CenterOwner"`, `ShowInTaskbar="True"`.
- Added `xmlns:vm` + `d:DataContext="{d:DesignInstance Type=vm:UdsViewModel}"` for the XAML designer.
- Body bytes copied verbatim; only the root tag changed.

- [ ] **Step 2: Verify XAML compiles via `dotnet build`**

Run: `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj --nologo`
Expected: CS0246 "type 'UdsWindow' not found" — this is expected because the code-behind hasn't been created yet. We will fix this in Task 2.

Do not commit yet.

---

### Task 2: Create `UdsWindow.xaml.cs`

**Files:**
- Create: `src/PeakCan.Host.App/Windows/UdsWindow.xaml.cs`

**Consumes:** `UdsWindow.xaml` (Step 1).
**Produces:** `UdsWindow : Window` partial class with the `DataContextChanged` / `Unloaded` / `OnLogCollectionChanged` logic migrated from `UdsView.xaml.cs:1-105`.

- [ ] **Step 1: Write the code-behind**

Create `src/PeakCan.Host.App/Windows/UdsWindow.xaml.cs` with EXACTLY this content:

```csharp
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PeakCan.Host.App.ViewModels.Uds;

namespace PeakCan.Host.App.Windows;

/// <summary>
/// v3.11.3 PATCH: UDS diagnostic surface migrated from an in-place
/// <c>UserControl</c> tab to a separate non-modal <see cref="Window"/>.
/// Hosts the same 4-panel orchestrator (<see cref="UdsViewModel"/>) and
/// listens to the shared <c>OutputLog</c> <see cref="ObservableCollection{T}"/>
/// to append color-coded Runs into the RichTextBox. Behaviour is byte-
/// identical to the v1.1.0 UdsView.xaml.cs — only the root type and
/// namespace moved from <c>PeakCan.Host.App.Views</c> to
/// <c>PeakCan.Host.App.Windows</c>.
/// </summary>
public partial class UdsWindow : Window
{
    private static readonly SolidColorBrush WarnBrush  = Freeze(new(Color.FromRgb(0xDC, 0xDC, 0xAA)));
    private static readonly SolidColorBrush ErrorBrush = Freeze(new(Color.FromRgb(0xF4, 0x87, 0x71)));

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public UdsWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) =>
        {
            DetachLog();
            DisposeSessionVm();
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachLog();
        if (e.NewValue is UdsViewModel vm)
        {
            vm.OutputLog.CollectionChanged += OnLogCollectionChanged;
        }
    }

    private void OnLogCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // v2.0.7 PATCH Bug-1: previously this handler returned early
        // for any action other than Add. ObservableCollection.Clear()
        // raises Reset (not Add), so clicking the UDS "Clear" button
        // emptied OutputLog but left the RichTextBox's FlowDocument
        // populated. WPF's binding only refreshes the bound property,
        // not the visual document — we own the LogParagraph.Inlines
        // ourselves and must mirror the collection explicitly.
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Reset:
                LogParagraph.Inlines.Clear();
                return;
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is null) return;
                foreach (UdsLogLine line in e.NewItems)
                {
                    var run = new Run($"[{line.Timestamp}] {line.Message}")
                    {
                        Foreground = line.Level switch
                        {
                            "Warn"  => WarnBrush,
                            "Error" => ErrorBrush,
                            _       => null,
                        }
                    };
                    LogParagraph.Inlines.Add(run);
                }
                LogBox.ScrollToEnd();

                // Trim if over the 500-line cap.
                while (LogParagraph.Inlines.Count > 500)
                {
                    LogParagraph.Inlines.Remove(LogParagraph.Inlines.FirstInline);
                }
                return;
            // Remove / Replace / Move are not currently emitted by the
            // 4 panel VMs (only Add via AppendLog + Reset via Clear).
            // Fall through to a defensive no-op rather than silently
            // leaving stale runs on screen.
        }
    }

    private void DetachLog()
    {
        if (DataContext is UdsViewModel oldVm)
        {
            oldVm.OutputLog.CollectionChanged -= OnLogCollectionChanged;
        }
    }

    private void DisposeSessionVm()
    {
        if (DataContext is UdsViewModel udsVm && udsVm.Session is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
```

Notes vs `UdsView.xaml.cs`:
- Class `UdsView : UserControl` → `UdsWindow : Window`.
- Namespace `PeakCan.Host.App.Views` → `PeakCan.Host.App.Windows` (matches `MultiFrameSendWindow`).
- Body bytes copied verbatim — no behavior change.

- [ ] **Step 2: Verify the App project builds cleanly**

Run: `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj --nologo`
Expected: build SUCCEEDS for `UdsWindow.xaml(.cs)` (CS0246 from Task 1 cleared).
The compile will still fail at `AppShellViewModel.cs` because it references the deleted `UdsView`. That is expected and resolved in Task 4.

Do not commit yet.

---

### Task 3: Delete the old `UdsView.xaml(.cs)`

**Files:**
- Delete: `src/PeakCan.Host.App/Views/UdsView.xaml`
- Delete: `src/PeakCan.Host.App/Views/UdsView.xaml.cs`

**Consumes:** Old layout already replaced by `UdsWindow.xaml(.cs)`.
**Produces:** No `UdsView` references remain. `AppShellViewModel.cs` is the only remaining caller and is updated in Task 4.

- [ ] **Step 1: Delete the files via `git rm` (preferred over `rm` per v3.6.x lesson — `git rm` records the deletion in the index so the next commit picks it up atomically)**

```bash
cd D:/claude_proj2/peakcan-host
git rm src/PeakCan.Host.App/Views/UdsView.xaml src/PeakCan.Host.App/Views/UdsView.xaml.cs
```

- [ ] **Step 2: Verify `UdsView` has no other references in src/**

Run (use Grep tool):
```
Grep pattern="UdsView\b" path=D:\claude_proj2\peakcan-host\src
```

Expected: ZERO matches. (`UdsViewModel` matches are expected and OK — that's the VM, not the view. The `\b` word boundary excludes `UdsViewModel`.)

If matches appear, surface them in the report — do NOT silently leave a stale reference.

Do not commit yet.

---

### Task 4: Update `AppShellViewModel` to use `UdsWindow` via `ViewSwitcher.ShowWindow`

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs`
  - Line 14: `using PeakCan.Host.App.Views;` — add `using PeakCan.Host.App.Windows;` (already imported at line 15 for `MultiFrameSendWindow`).
  - Line 142: field `private UdsView? _udsView;` → `private UdsWindow? _udsWindow;`.
  - Lines 579-590: `ShowUds` body rewrite.

**Consumes:** `ViewSwitcher.ShowWindow<TWindow>(factory, ref cache)` (signature frozen at v3.11.1 PATCH M3 — see `ViewSwitcher.cs:107-135`).
**Produces:** `ShowUdsCommand` opens a cached `UdsWindow` with `Owner = AppShell`, closed-reset wired by helper.

- [ ] **Step 1: Replace the field declaration**

Edit line 142 of `AppShellViewModel.cs`:

```diff
-    private UdsView? _udsView;
+    private UdsWindow? _udsWindow;
```

- [ ] **Step 2: Rewrite `ShowUds` body (lines 579-590)**

Replace the entire method body with:

```csharp
    [RelayCommand]
    private void ShowUds()
    {
        // v3.11.3 PATCH: UDS migrated from an in-place UserControl tab to
        // a separate non-modal Window. Mirrors the v3.9.1 PATCH B1 + v3.11.1
        // PATCH M3 secondary-window precedent established by ShowTraceViewer:
        // factory + cache lifecycle owned by ViewSwitcher.ShowWindow
        // (auto Closed-reset); Owner + Show/Activate owned by the caller
        // (Application.Current.MainWindow only resolves inside App.OnStartup's
        // STA context).
        //
        // Behaviour parity with the pre-PATCH UserControl path:
        // - First Show creates the window from the factory.
        // - Second Show reuses the cached instance (window position + size +
        //   SelectedDid + Did/Routine/Dtc selections all preserved).
        // - Closing the window clears the cache so the next Show opens fresh.
        // - Closing AppShell cascade-closes the UDS window via the Owner
        //   assignment below (mirrors ShowTraceViewer at line 681).
        ViewSwitcher.ShowWindow(
            factory: () => new UdsWindow { DataContext = _udsViewModel },
            cache: ref _udsWindow);
        if (_udsWindow is null) return; // defensive — cache cannot be null after ShowWindow

        if (Application.Current?.MainWindow is { } owner && owner != _udsWindow)
            _udsWindow.Owner = owner;

        if (!_udsWindow.IsVisible)
        {
            _udsWindow.Show();
        }
        else
        {
            // Already shown — bring to the foreground instead of re-activating
            // (which on Windows flashes the taskbar icon for an already-visible
            // window and looks like a bug). Same precedent as ShowTraceViewer.
            _udsWindow.Activate();
        }
    }
```

Notes vs the old body:
- `ViewSwitcher.Show(...)` → `ViewSwitcher.ShowWindow(...)`.
- `ref _udsView` → `ref _udsWindow`.
- Factory `new UdsView { DataContext = _udsViewModel }` → `new UdsWindow { DataContext = _udsViewModel }`.
- Setter lambda `v => CurrentView = v` removed (windows don't live in `CurrentView`).
- Added: Owner assignment + Show/Activate (4 lines mirrored from `ShowTraceViewer` at 681-694).

- [ ] **Step 3: Verify the App project builds cleanly**

Run: `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj --nologo`
Expected: build SUCCEEDS. Zero CS errors. The full solution build below will confirm tests too.

Do not commit yet.

---

### Task 5: Add STA test for `ShowUds` cache contract

**Files:**
- Create: `tests/PeakCan.Host.App.Tests/Windows/UdsWindowTests.cs`

**Consumes:** `AppShellViewModel.NewVm()` (already wired with `UdsViewModel` ctor args — see `AppShellViewModelTests.cs:118-122`), `[Collection(WpfAppTestCollection.Name)]`, `RunSta(Action body)` (mirrored from `AppShellViewModelTests.cs:149-170`).
**Produces:** +1 STA test asserting that `ShowUdsCommand` opens a cached `UdsWindow` and reuses the same instance on a second call.

- [ ] **Step 1: Write the failing test**

Create `tests/PeakCan.Host.App.Tests/Windows/UdsWindowTests.cs` with EXACTLY this content:

```csharp
using System.Reflection;
using System.Windows;
using FluentAssertions;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.App.Views;
using PeakCan.Host.App.Windows;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.Channel;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeakCan.Host.App.Tests.Windows;

/// <summary>
/// v3.11.3 PATCH: pins the ShowUdsCommand contract — the UDS surface
/// opens as a separate cached <see cref="UdsWindow"/> (not the in-place
/// <c>UdsView</c> UserControl used by v1.1.0 – v3.11.2). Mirrors the
/// ShowTraceViewer STA test pattern in <c>AppShellViewModelTests</c>.
/// STA-bound (Window ctor requires STA) — joined to
/// <see cref="WpfAppTestCollection"/> so it doesn't race on the WPF
/// Application singleton.
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public class UdsWindowTests
{
    /// <summary>
    /// Hand-rolled <see cref="DbcService"/> stub. The shell only navigates
    /// into the UDS window; it never loads a DBC. Stub keeps the test
    /// hermetic (no real DBC file required).
    /// </summary>
    private sealed class FakeDbcService : DbcService
    {
        public FakeDbcService() : base(NullLogger<DbcService>.Instance) { }
        public override System.Threading.Tasks.Task LoadAsync(string path, System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>
    /// Build a real <see cref="AppShellViewModel"/> with the same
    /// dependency surface the production DI uses. Mirrors
    /// <c>AppShellViewModelTests.NewVm</c> but is kept private here so
    /// the test file is self-contained (no internal visibility on the
    /// existing helper).
    /// </summary>
    private static AppShellViewModel NewVm()
    {
        var isoTp = new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { });
        var udsClient = new UdsClient(isoTp);
        var recentTemp = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"recent-uds-{System.Guid.NewGuid():N}.json");
        return new AppShellViewModel(
            new ChannelRouter(),
            NullLogger<AppShellViewModel>.Instance,
            new TraceViewModel(),
            new SendService(NullLogger<SendService>.Instance),
            new FakeChannelProbe(),
            new FakeChannelFactory(),
            new DbcViewModel(new FakeDbcService(),
                             new SignalViewModel(),
                             NullLogger<DbcViewModel>.Instance),
            new SendViewModel(new SendService(NullLogger<SendService>.Instance), NullLogger<SendViewModel>.Instance, new SendViewModelTests.FakeCyclicSendService(), null),
            new SignalViewModel(),
            new StatsViewModel(),
            new ScriptViewModel(NullLogger<ScriptViewModel>.Instance,
                                new ScriptEngine(NullLogger<ScriptEngine>.Instance, null, null, null)),
            new UdsViewModel(
                new SessionPanelViewModel(udsClient, NullLogger<SessionPanelViewModel>.Instance),
                new DidPanelViewModel(udsClient, new DidDatabase(NullLogger<DidDatabase>.Instance)),
                new RoutinePanelViewModel(udsClient, new RoutineDatabase(NullLogger<RoutineDatabase>.Instance)),
                new DtcPanelViewModel(udsClient)),
            new RecordViewModel(new RecordService(NullLogger<RecordService>.Instance), NullLogger<RecordViewModel>.Instance),
            new ReplayViewModel(
                NSubstitute.Substitute.For<IReplayService>(),
                NSubstitute.Substitute.For<IFileDialogService>(),
                NSubstitute.Substitute.For<IAscContentHasher>(),
                NSubstitute.Substitute.For<IAscLocator>(),
                new TraceSessionLibrary(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"uds-tmtrace-{System.Guid.NewGuid():N}.tmtrace"), NullLogger<TraceSessionLibrary>.Instance),
                new RecentSessionsService(NullLogger<RecentSessionsService>.Instance, recentTemp)),
            new MultiFrameSendViewModel(new SequenceSendService(new SendService(NullLogger<SendService>.Instance))),
            new TraceViewerViewModel(NSubstitute.Substitute.For<ITraceSessionRegistry>(), new FakeDbcService(), NullLogger<TraceViewerViewModel>.Instance,
                new TraceSessionLibrary(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"uds-traceview-{System.Guid.NewGuid():N}.tmtrace"), NullLogger<TraceSessionLibrary>.Instance)),
            new RecentSessionsService(NullLogger<RecentSessionsService>.Instance, recentTemp),
            NSubstitute.Substitute.For<IFileDialogService>(),
            NSubstitute.Substitute.For<PeakCan.Host.App.Services.Trace.IMessageBoxPrompt>());
    }

    /// <summary>
    /// Hand-rolled <see cref="Core.IChannelFactory"/> stub. The production
    /// <see cref="AppShellViewModelTests"/> class declares the same shape
    /// as <c>private sealed class</c> — not visible here. Mirrored locally
    /// (duplication is the smaller cost vs the visibility surface change
    /// of promoting it to <c>internal</c> just for this PATCH).
    /// </summary>
    private sealed class FakeChannelFactory : Core.IChannelFactory
    {
        public ICanChannel Create(ChannelId id) => new FakeCanChannel(id);
    }

    private sealed class FakeCanChannel : ICanChannel
    {
        public ChannelId Id { get; }
        public bool IsConnected { get; private set; } = true;
#pragma warning disable CS0067
        public event Action<CanFrame>? FrameReceived;
#pragma warning restore CS0067
        public FakeCanChannel(ChannelId id) { Id = id; }
        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
            => Task.FromResult(Result<Unit>.Ok(default));
        public async Task DisconnectAsync(CancellationToken ct = default)
        {
            await Task.Yield();
            IsConnected = false;
        }
        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
            => ValueTask.FromResult(Result<Unit>.Ok(default));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Hand-rolled <see cref="Core.IChannelProbe"/> stub. Same shape as
    /// the <c>FakeChannelProbe</c> nested in <c>AppShellViewModelTests</c>
    /// (line 82); duplicated locally because the existing one is
    /// <c>private</c>.
    /// </summary>
    private sealed class FakeChannelProbe : Core.IChannelProbe
    {
        public Core.ProbeResult Probe(ushort handle)
            => new(true, $"fake probe ok 0x{handle:X2}");
    }

    private static void RunSta(Action body)
    {
        if (System.Threading.Thread.CurrentThread.GetApartmentState() == System.Threading.ApartmentState.STA)
        {
            body();
            return;
        }
        Exception? caught = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { caught = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        if (thread.IsAlive)
        {
            throw new TimeoutException("STA thread did not complete within 30 s — likely a WPF dispatcher deadlock");
        }
        if (caught is not null) throw caught;
    }

    [Fact]
    public void ShowUdsCommand_Opens_Cached_UdsWindow()
    {
        // v3.11.3 PATCH: ShowUdsCommand opens a UdsWindow (Window) rather
        // than swapping CurrentView to a UdsView (UserControl). Cache reuse
        // mirrors ShowTraceViewer: a second click returns the same instance
        // so window position + SelectedDid + tab selections survive menu
        // round-trips.
        RunSta(() =>
        {
            var vm = NewVm();
            vm.ShowUdsCommand.Execute(null);

            var first = (UdsWindow?)typeof(AppShellViewModel)
                .GetField("_udsWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(vm);
            first.Should().NotBeNull(
                "first ShowUdsCommand must populate the _udsWindow cache via ViewSwitcher.ShowWindow");

            vm.ShowUdsCommand.Execute(null);

            var second = (UdsWindow?)typeof(AppShellViewModel)
                .GetField("_udsWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(vm);
            second.Should().BeSameAs(first,
                "second ShowUdsCommand must reuse the cached UdsWindow — matches ViewSwitcher.ShowWindow contract");

            // v3.11.3 PATCH: UdsWindow is no longer the in-place CurrentView
            // — it lives in its own Window. CurrentView must remain null
            // (the AppShell does not host a Uds tab any more).
            vm.CurrentView.Should().BeNull(
                "CurrentView is the in-place tab surface; UDS is now a Window, not a tab");
        });
    }
}
```

- [ ] **Step 2: Build the test project to confirm it compiles**

Run: `dotnet build tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo`
Expected: 0 errors. Warnings allowed.

- [ ] **Step 3: Run the new test in isolation**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~UdsWindowTests" --nologo --no-build`
Expected: 1 passed (the new test), 0 failed. The other STA-collected tests in the same collection are gated behind xunit's collection serialization — they should also pass but if they don't, surface in the report.

- [ ] **Step 4: Commit**

```bash
cd D:/claude_proj2/peakcan-host
git add src/PeakCan.Host.App/Windows/UdsWindow.xaml \
        src/PeakCan.Host.App/Windows/UdsWindow.xaml.cs \
        src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs
git rm  src/PeakCan.Host.App/Views/UdsView.xaml \
        src/PeakCan.Host.App/Views/UdsView.xaml.cs
git add tests/PeakCan.Host.App.Tests/Windows/UdsWindowTests.cs

git commit -m "refactor(uds): migrate UdsView UserControl to UdsWindow Window (v3.11.3 PATCH)"
```

---

### Task 6: Run the full test suite + Tier 3 ship prep

**Files:**
- No source changes; this task verifies Task 5's commit didn't regress and prepares the Tier 3 ship.

- [ ] **Step 1: Run the full solution test suite**

Run: `dotnet test PeakCan.Host.slnx --nologo --no-build`
Expected: **1267 + 5 SKIP / 0 fail** (+1 active test from `UdsWindowTests`).

If any test fails or skips an unexpected count, surface in the report and STOP — do not proceed to the Tier 3 ship until the regression is resolved.

- [ ] **Step 2: Manual smoke (3 verification cases)**

Run the WPF app and verify:
1. **Menu route**: `View ▸ UDS` opens a new non-modal window titled "UDS Diagnostics" centered over AppShell. Clicking `View ▸ UDS` again does NOT spawn a second window — the cached window reactivates.
2. **Owner cascade-close**: With the UDS window open, close AppShell. The UDS window must close simultaneously (mirrors v3.9.1 PATCH B1 Trace Viewer pattern).
3. **Closed-reset**: Close the UDS window via the X button. Click `View ▸ UDS` again — a fresh window appears (cache was nulled by `ViewSwitcher.ShowWindow`).

If any smoke case fails, surface in the report.

- [ ] **Step 3: Write release notes**

Create `docs/release-notes-v3.11.3.md`:

```markdown
# Release Notes v3.11.3 — UDS Window refactor (PATCH)

**Released:** 2026-07-07
**Parent:** v3.11.2 PATCH (`d9e77a82`)
**Tag:** v3.11.3
**Branch:** `feature/v3-11-3-patch`

## Highlights

This PATCH completes the user-requested UX refactor: the UDS diagnostic surface
is migrated from an in-place `UserControl` tab to a separate, non-modal
`Window` that opens from the AppShell View menu — mirroring the
`TraceViewerView` / `MultiFrameSendWindow` precedent.

| Commit | Refactor | Tests |
|--------|----------|-------|
| `<this-commit>` | `UdsView` (`UserControl`) → `UdsWindow` (`Window`) under `Windows/` namespace | +1 |

**Test delta:** 1266 + 5 SKIP / 0 fail → **1267 + 5 SKIP / 0 fail** (+1 active test)
**Code stats:** +220 / -218 (net +2 LoC: Window attributes added, UserControl attributes removed; body bytes identical)

## Refactor

### UDS — `UdsView` → `UdsWindow`

`src/PeakCan.Host.App/Views/UdsView.xaml(.cs)` is moved to
`src/PeakCan.Host.App/Windows/UdsWindow.xaml(.cs)`. The root type changes
from `<UserControl>` to `<Window>` with `Title="UDS Diagnostics"`,
`Width="1100"`, `Height="700"`, `WindowStartupLocation="CenterOwner"`,
`ShowInTaskbar="True"`.

**XAML body is byte-identical** — same `DockPanel`, same TabControl
(DIDs / Routines / DTCs), same RichTextBox log handler. Only the root
type + namespace moved. XAML bindings resolve identically because the
`DataContext` contract (`UdsViewModel`) is preserved.

**`UdsViewModel` is unchanged** — no new DI registration needed (already
a singleton at `AppHostBuilder.cs:615`).

### `AppShellViewModel.ShowUds` — switch to `ViewSwitcher.ShowWindow`

The `ShowUds` body switches from `ViewSwitcher.Show(...)` (in-place tab
swapper) to `ViewSwitcher.ShowWindow(...)` + Owner assignment + Show/Activate.
This mirrors the v3.9.1 PATCH B1 + v3.11.1 PATCH M3 secondary-window
precedent already in place for `ShowTraceViewer`:

```csharp
ViewSwitcher.ShowWindow(
    factory: () => new UdsWindow { DataContext = _udsViewModel },
    cache: ref _udsWindow);
if (_udsWindow is null) return; // defensive

if (Application.Current?.MainWindow is { } owner && owner != _udsWindow)
    _udsWindow.Owner = owner;

if (!_udsWindow.IsVisible) _udsWindow.Show();
else _udsWindow.Activate();
```

**Cache semantics preserved**:
- First Show creates the window from the factory.
- Second Show reuses the cached instance (window position + size +
  SelectedDid + tab selections all survive menu round-trips).
- Closing the window clears the cache so the next Show opens fresh.
- Closing AppShell cascade-closes the UDS window via the Owner
  assignment (mirrors Trace Viewer cascade-close from v3.9.1 PATCH B1).

**Field rename**: `_udsView : UdsView?` → `_udsWindow : UdsWindow?`.

**Menu binding preserved**: `AppShell.xaml` line `<MenuItem Header="UDS"
Command="{Binding ShowUdsCommand}" />` is unchanged — the source-
generated command name survives the refactor.

## Tests

| Test | Asserts |
|------|---------|
| `UdsWindowTests.ShowUdsCommand_Opens_Cached_UdsWindow` (NEW, +1) | First click populates `_udsWindow` cache; second click reuses the same instance; `CurrentView` remains null (UDS is no longer in-place) |

## Deferred

| Item | Reason |
|------|--------|
| C2 ReplayViewModel god class split | Deferred to v3.12.0 MINOR |
| 38-finding backlog (H3/H6/M1-M13) | Deferred to v3.12.0 MINOR cleanup PATCH |

## Upgrade notes

No breaking changes:
- `ShowUdsCommand` source-generated name preserved (XAML binding unchanged).
- `UdsViewModel` constructor signature unchanged.
- DI registration in `AppHostBuilder` unchanged.
- File moved (not renamed): `Views/UdsView.xaml(.cs)` → `Windows/UdsWindow.xaml(.cs)`.
  No external consumers reference `UdsView` (only the `AppShellViewModel`
  field reference, updated in this PATCH).

## NEXT

- v3.11.4 PATCH — visual UI smoke testing if user-reported regressions emerge
- v3.12.0 MINOR — C2 ReplayViewModel god class split (1153 LoC → 3 VMs)
```

- [ ] **Step 4: Create the Tier 3 ship script**

Create `scripts/tier3_v3113.py` by copying `scripts/tier3_v3112.py` and updating:
- Line 17: `PARENT_SHA = "d9e77a82cfa2686e4e1a5957945c11b3e8950212"`  (v3.11.2 on origin/main — confirm via `git ls-remote https://github.com/jasontaotao/peakcan-host.git refs/tags/v3.11.2` if uncertain)
- Lines 21-28: replace `ADDED_OR_MODIFIED` with:

```python
ADDED_OR_MODIFIED = [
    # M4: UdsView -> UdsWindow
    "src/PeakCan.Host.App/Windows/UdsWindow.xaml",
    "src/PeakCan.Host.App/Windows/UdsWindow.xaml.cs",
    "src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs",
    "tests/PeakCan.Host.App.Tests/Windows/UdsWindowTests.cs",
    # Release notes
    "docs/release-notes-v3.11.3.md",
]
```

- Lines 73, 84, 87, 90, 94, 99: replace all `v3.11.2` with `v3.11.3`.

- [ ] **Step 5: Run the Tier 3 ship**

Run: `python scripts/tier3_v3113.py`
Expected output:
```
  parent       d9e77a82...
  parent tree  <40-hex-sha>
  blob   <40-hex-sha>  src/PeakCan.Host.App/Windows/UdsWindow.xaml  (... bytes)
  blob   <40-hex-sha>  src/PeakCan.Host.App/Windows/UdsWindow.xaml.cs  (... bytes)
  blob   <40-hex-sha>  src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs  (... bytes)
  blob   <40-hex-sha>  tests/PeakCan.Host.App.Tests/Windows/UdsWindowTests.cs  (... bytes)
  blob   <40-hex-sha>  docs/release-notes-v3.11.3.md  (... bytes)

  tree  <40-hex-sha>
  commit <40-hex-sha>
  refs/heads/main -> <40-hex-sha> (force)
  tag    <40-hex-sha>  v3.11.3
  refs/tags/v3.11.3 -> <40-hex-sha>
  release https://github.com/jasontaotao/peakcan-host/releases/tag/v3.11.3

=== TIER 3 SHIP COMPLETE ===
  parent  : d9e77a82...
  new     : <40-hex-sha>
  tag     : v3.11.3  (<40-hex-sha>)
  release : https://github.com/jasontaotao/peakcan-host/releases/tag/v3.11.3
```

- [ ] **Step 6: Commit the ship script + release notes to local branch**

```bash
cd D:/claude_proj2/peakcan-host
git add docs/release-notes-v3.11.3.md scripts/tier3_v3113.py
git commit -m "docs(ship): v3.11.3 PATCH release notes + tier3 ship script"
```

- [ ] **Step 7: PKM capture**

Dispatch `vault-pkm:pkm-capture` in the background with:
- First capture this session: false (previous devlog already exists for v3.11.2)
- Previous capture timestamp: 2026-07-07T07:10:55Z (v3.11.2 ship)
- Vault path: `01-Projects/peakcan-host/development/v3-11-3-patch-uds-window-2026-07-07.md`

---

## Self-Review (post-write, before handoff)

Run this checklist yourself:

1. **Spec coverage**:
   - User request: "UDS view should be a separate window". ✓ Task 4 + Task 5 verify the window opens.
   - User request: mirror Trace Viewer pattern. ✓ Task 4 code mirrors `ShowTraceViewer` line-for-line.
   - Public API surface unchanged. ✓ Task 4 keeps `ShowUdsCommand` name; Task 5 doc says no DI changes.
   - +1 test for the new contract. ✓ Task 5.
   - Tier 3 ship. ✓ Task 6.

2. **Placeholder scan**:
   - "TBD" / "TODO" / "implement later" — none.
   - "Add appropriate error handling" — none (behavior is byte-identical to UdsView).
   - "Similar to Task N" — none (each step shows the actual code).

3. **Type consistency**:
   - Field `_udsWindow : UdsWindow?` declared in Task 4 step 1, used in Task 4 step 2 factory + ViewSwitcher.ShowWindow call, asserted in Task 5 step 1 reflection read. Consistent.
   - `ViewSwitcher.ShowWindow<TWindow>(factory, ref cache)` — signature matches the v3.11.1 PATCH M3 implementation at `ViewSwitcher.cs:107`. Consistent.

## Out of scope (deferred)

- **WPF Application singleton / STA race fix** — pre-existing; not introduced by this PATCH.
- **`UdsWindow.WindowStartupLocation` tuning** — defaulted to `CenterOwner`; matches `MultiFrameSendWindow`. Cosmetic polish deferred.
- **ODX import UX polish** — pre-existing scope; not user-reported.
- **Auto-save / snapshot integration with UdsWindow** — pre-existing scope; UDS doesn't snapshot today. Defer.
- **12-step form layout, sub-windows for DIDs/Routines** — YAGNI; current tabbed layout covers the user need.

## Verification

```bash
# Targeted:
dotnet test --filter "FullyQualifiedName~UdsWindowTests|FullyQualifiedName~AppShellViewModelTests|FullyQualifiedName~ViewSwitcher" --nologo
# Expect: 1 new UdsWindow test green, all AppShellViewModel + ViewSwitcher tests green

# Full suite:
dotnet test PeakCan.Host.slnx --nologo
# Expect: 1266 → 1267 + 5 SKIP / 0 fail (+1 active)

# Manual smoke:
# 1. View > UDS opens a non-modal UdsWindow titled "UDS Diagnostics"
# 2. View > UDS second click reuses the cached window (no second window)
# 3. Closing AppShell cascade-closes the UdsWindow
# 4. Closing UdsWindow via X then View > UDS opens a fresh window
```

## Ship summary

- **Tag**: v3.11.3 (PATCH)
- **Parent**: v3.11.2 PATCH on origin/main (`d9e77a82cfa2686e4e1a5957945c11b3e8950212`)
- **Files**: 2 created (UdsWindow.xaml + .cs), 2 deleted (UdsView.xaml + .cs), 2 modified (AppShellViewModel.cs + UdsWindowTests.cs), +1 ship script, +1 release notes
- **Tests**: +1 active (UdsWindow cache reuse contract). Total delta: 1266 → 1267 + 5 SKIP / 0 fail.
- **Commits**: 2 commits (1 source + 1 ship docs) on `feature/v3-11-3-patch`, then 1 Tier 3 ship commit on `origin/main`.