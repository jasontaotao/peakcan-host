# PeakCan Host — Scripting Engine Implementation Plan

> **Spec**: [2026-06-22-scripting-engine-design.md](../specs/2026-06-22-scripting-engine-design.md)
> **Baseline**: `f9413b4` (v0.10.1)
> **Target**: v1.0.0

## Task Breakdown

### Phase A: Core Engine (Backend)

#### A1: Add ClearScript NuGet packages
- Add `Microsoft.ClearScript.V8` and `Microsoft.ClearScript.V8.Native.win-x64` to `PeakCan.Host.App.csproj`
- Verify build succeeds
- **Estimated**: 30 min

#### A2: Implement `ScriptEngine` service
- Create `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs`
- V8 engine lifecycle: `Create()`, `Execute()`, `Dispose()`
- Sandbox: inject whitelisted globals, block dangerous APIs
- Script timeout guard (CancellationToken + V8 interrupt)
- **Estimated**: 3 hours

#### A3: Implement `CanApi` bridge
- Create `src/PeakCan.Host.App/Services/Scripting/CanApi.cs`
- Expose `can.send()`, `can.onFrame()`, `can.onMessage()`, `can.offFrame()`, `can.offMessage()`, `can.isConnected()`, `can.getChannelId()`
- Thread-safe callback registration (callbacks fire on V8 thread)
- **Estimated**: 2 hours

#### A4: Implement `DbcApi` bridge
- Create `src/PeakCan.Host.App/Services/Scripting/DbcApi.cs`
- Expose `dbc.load()`, `dbc.decode()`, `dbc.getSignal()`, `dbc.getMessages()`
- Async `dbc.load()` uses `DbcService.LoadAsync` under the hood
- **Estimated**: 2 hours

#### A5: Implement utility functions
- Create `src/PeakCan.Host.App/Services/Scripting/ScriptUtilities.cs`
- `log()`, `warn()`, `error()` → route to output buffer
- `delay()` → cancellable Task.Delay wrapper
- `hex()`, `toHex()` → formatting helpers
- **Estimated**: 1 hour

#### A6: Unit tests for Core Engine
- Create `tests/PeakCan.Host.App.Tests/Services/Scripting/ScriptEngineTests.cs`
- Test sandbox restrictions (blocked APIs throw)
- Test `can.send()` calls `SendService.SendAsync`
- Test `can.onFrame()` callback receives frames
- Test `dbc.load()` / `dbc.decode()` integration
- Test script timeout (60s guard)
- Test `log()` / `delay()` / `hex()`
- **Estimated**: 4 hours

### Phase B: ViewModel + IPC

#### B1: Implement `ScriptViewModel`
- Create `src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs`
- Properties: `ScriptText`, `IsRunning`, `OutputLines` (ObservableCollection)
- Commands: `RunCommand`, `StopCommand`, `OpenCommand`, `SaveCommand`, `ClearOutputCommand`
- Output buffering: collect log lines, flush to UI at 30 Hz (same pattern as SignalChartViewModel)
- **Estimated**: 3 hours

#### B2: Wire ScriptEngine into DI
- Modify `AppHostBuilder.cs`: register `ScriptEngine`, `CanApi`, `DbcApi`, `ScriptViewModel`
- Register `ScriptEngine` as hosted service (disposes on app exit)
- **Estimated**: 30 min

#### B3: Unit tests for ViewModel
- Create `tests/PeakCan.Host.App.Tests/ViewModels/ScriptViewModelTests.cs`
- Test Run/Stop command CanExecute
- Test output buffering (log lines appear within 33ms)
- Test script cancellation
- Test Open/Save file dialog integration
- **Estimated**: 3 hours

### Phase C: UI Integration

#### C1: Add WebView2 NuGet package
- Add `Microsoft.Web.WebView2` to `PeakCan.Host.App.csproj`
- Verify build succeeds
- **Estimated**: 30 min

#### C2: Create CodeMirror 6 bundle
- Create `src/PeakCan.Host.App/Resources/ScriptEditor/` directory
- Write `index.html` with CodeMirror 6 (inline, no CDN)
- Include JS language support, syntax highlighting
- Include autocomplete for `can.*` and `dbc.*` APIs
- **Estimated**: 3 hours

#### C3: Create `ScriptView.xaml`
- Create `src/PeakCan.Host.App/Views/ScriptView.xaml` + `.xaml.cs`
- Layout: toolbar (Run/Stop/Open/Save/Examples) + WebView2 editor + output DataGrid
- WebView2 loads CodeMirror 6 from embedded resource
- Output panel with auto-scroll + pause-on-scroll-up
- **Estimated**: 3 hours

#### C4: Wire ScriptView into AppShell
- Modify `AppShell.xaml`: add "Script" tab
- Modify `AppShellViewModel`: add `ScriptViewModel` property
- Modify `AppHostBuilder.cs`: register `ScriptViewModel`
- **Estimated**: 1 hour

### Phase D: Polish + Examples

#### D1: Write example scripts
- Create `scripts/examples/` directory (embedded resources)
- Frame Logger, DBC Signal Monitor, Periodic Send, Request-Response, Signal Statistics, Bus Load Generator
- **Estimated**: 2 hours

#### D2: Script timeout guard
- Default 60s timeout, configurable via constant
- V8 `Interrupt()` API to kill infinite loops
- UI shows timeout warning
- **Estimated**: 1 hour

#### D3: Error handling
- Syntax errors: highlight in editor, show in output
- Runtime errors: catch + display with stack trace
- Engine disposal on script stop
- **Estimated**: 1 hour

#### D4: Update README + docs
- Add "Scripting" section to README
- Document `can.*` and `dbc.*` APIs
- Add script examples to docs
- Update version to v1.0.0
- **Estimated**: 1 hour

## Execution Order

```
A1 → A2 → [A3, A4, A5 parallel] → A6
      ↓
      B1 → [B2, B3 parallel]
      ↓
      C1 → C2 → C3 → C4
      ↓
      [D1, D2, D3 parallel] → D4
```

## Test Strategy

- **Unit tests**: 80%+ coverage for ScriptEngine, CanApi, DbcApi, ScriptViewModel
- **Integration tests**: Script → SendService → mock channel round-trip
- **Manual tests**: Run each example script against real or fake hardware
- **Edge cases**: infinite loop timeout, concurrent script runs, engine disposal during callback

## Estimated Total

| Phase | Tasks | Estimated |
|-------|-------|-----------|
| A: Core Engine | 6 | ~13 hours |
| B: ViewModel | 3 | ~6.5 hours |
| C: UI | 4 | ~7.5 hours |
| D: Polish | 4 | ~5 hours |
| **Total** | **17** | **~32 hours** |
