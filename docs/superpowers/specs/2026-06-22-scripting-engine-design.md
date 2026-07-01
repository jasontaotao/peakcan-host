# PeakCan Host — Scripting Engine Design

## 1. Overview

Add a scripting tab to PeakCan Host that allows users to write and execute JavaScript scripts to automate CAN bus operations. Scripts can send frames, react to received frames, decode DBC signals, and perform custom analysis.

## 2. Goals

- **Automation**: Users can write scripts to automate repetitive CAN tasks (e.g., periodic diagnostic requests, signal logging).
- **Reactivity**: Scripts can register callbacks for incoming frames and react in real-time.
- **Safety**: Scripts run in a sandboxed environment with no access to filesystem, network, or system APIs.
- **Integration**: Full access to DBC decoding, signal values, and channel state.
- **Developer Experience**: Syntax highlighting, auto-completion, and error reporting in the editor.

## 3. Architecture

### 3.1 Script Engine: ClearScript (V8)

**Decision**: Use `Microsoft.ClearScript.V8` (NuGet: `Microsoft.ClearScript.V8.Native.win-x64`).

**Rationale**:
- Official Microsoft library for embedding V8 in .NET
- Full ECMAScript 2023+ support
- Excellent performance for CAN throughput (8k+ fps scripts)
- Clean .NET ↔ JS interop
- Windows-only is acceptable (PeakCan Host is Windows-only anyway)

**Alternatives considered**:
- **Jint**: Pure C# JS engine, but 10-50x slower than V8. At 8k fps, script execution latency matters.
- **Roslyn C# scripting**: Powerful but requires users to know C#; JS is more accessible for CAN engineers.
- **Python (IronPython)**: Good ecosystem but adds a runtime dependency; JS is self-contained via ClearScript.

### 3.2 Editor: WebView2 + CodeMirror 6

**Decision**: Embed a WebView2 control hosting CodeMirror 6.

**Rationale**:
- CodeMirror 6 provides excellent JS editing (syntax highlighting, autocomplete, linting)
- Consistent with claude-AutosarCfg's scripting approach (reuse knowledge)
- WebView2 is pre-installed on Windows 10 1809+ / Windows 11
- Native WPF editor (AvalonEdit) would require building JS language support from scratch

**Trade-off**: WebView2 adds ~150 MB to the published exe. Acceptable for a desktop tool.

### 3.3 Sandbox Design

The V8 engine runs with a **whitelist** of allowed APIs:

**Allowed**:
- `console.log()` / `console.warn()` / `console.error()` → routed to script output panel
- `JSON.parse()` / `JSON.stringify()`
- `Math`, `Number`, `String`, `Array`, `Object`, `Date`
- `setTimeout()` / `clearTimeout()` / `setInterval()` / `clearInterval()` (async scheduling)
- `can.*` namespace (see §4)
- `dbc.*` namespace (see §4)
- `log()` / `warn()` / `error()` — convenience wrappers

**Blocked** (not injected into V8 global):
- `require()`, `import()`, `fetch()`, `XMLHttpRequest`
- `process`, `global`, `globalThis` (restricted)
- `fs`, `path`, `os`, `child_process`
- `eval()` / `Function()` (optional: can be enabled per script)

## 4. Script API

### 4.1 `can` Namespace

```javascript
// Send a frame
can.send(id, data, options?)
// id: number (CAN ID)
// data: number[] | Uint8Array (raw bytes, max 8 or 64 for FD)
// options?: { fd?: boolean, extended?: boolean }
// Returns: Promise<{ success: boolean, error?: string }>

// Register frame callback
can.onFrame(callback)
// callback: (frame: ReceivedFrame) => void
// ReceivedFrame: { id: number, data: Uint8Array, timestamp: number, fd: boolean, extended: boolean }

// Register callback for specific CAN ID
can.onMessage(id, callback)
// id: number (exact match) or string (hex prefix, e.g., "1A" matches 0x1A0-0x1AF)

// Remove callback
can.offFrame(callback)
can.offMessage(id, callback)

// Query channel state
can.isConnected() // boolean
can.getChannelId() // string (e.g., "PCAN_USBBUS1")
```

### 4.2 `dbc` Namespace

```javascript
// Load a DBC file
await dbc.load(path)
// path: string (absolute path)
// Returns: Promise<{ success: boolean, messageCount: number, error?: string }>

// Decode a frame using loaded DBC
const decoded = dbc.decode(frame)
// frame: ReceivedFrame (from can.onFrame)
// Returns: { message: string, signals: { [name: string]: { value: number, raw: number, unit: string } } } | null

// Get signal value by name
dbc.getSignal(messageName, signalName)
// Returns: { value: number, raw: number, unit: string, timestamp: number } | null

// List loaded messages
dbc.getMessages()
// Returns: Array<{ id: number, name: string, dlc: number, sender: string }>
```

### 4.3 Utility Functions

```javascript
// Logging (output to script console panel)
log(message, ...args)    // info level
warn(message, ...args)   // warning level
error(message, ...args)  // error level

// Timing
await delay(ms)
// ms: number (milliseconds)
// Returns: Promise<void> (cancellable)

// Hex formatting
hex(value, padLength?) // number → "0x1A"
toHex(bytes)           // Uint8Array → "01 02 03 04"
```

### 4.4 Script Lifecycle

```javascript
// Optional: runs once when script starts
export function onInit() {
    log("Script initialized");
    await dbc.load("C:/path/to/network.dbc");
}

// Optional: runs once when script stops (cleanup)
export function onDispose() {
    log("Script stopped");
}
```

## 5. UI Design

### 5.1 Script Tab Layout

```
┌─────────────────────────────────────────────────────────────┐
│ [▶ Run] [⏹ Stop] [📂 Open] [💾 Save] [📋 Examples ▾]      │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ┌─────────────────────────────────────────────────────┐   │
│  │                                                     │   │
│  │              CodeMirror 6 Editor                    │   │
│  │                                                     │   │
│  │   // Example: Log all 0x1A0 frames                  │   │
│  │   can.onFrame((frame) => {                          │   │
│  │       if (frame.id === 0x1A0) {                     │   │
│  │           log(`Received: ${toHex(frame.data)}`);    │   │
│  │       }                                             │   │
│  │   });                                               │   │
│  │                                                     │   │
│  └─────────────────────────────────────────────────────┘   │
│                                                             │
├─────────────────────────────────────────────────────────────┤
│ Output                                              [Clear] │
├─────────────────────────────────────────────────────────────┤
│ [12:34:56] Script initialized                               │
│ [12:34:57] Received: 01 02 03 04 05 06 07 08                │
│ [12:34:57] Received: 0A 0B 0C 0D                            │
│ [12:34:58] ⚠ Warning: Signal 'Temperature' out of range     │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

### 5.2 Toolbar Actions

| Button | Action |
|--------|--------|
| ▶ Run | Execute current script (validates → compiles → runs in sandbox) |
| ⏹ Stop | Cancel running script (calls `onDispose()`, disposes V8 engine) |
| 📂 Open | Load `.js` file from disk |
| 💾 Save | Save current script to disk |
| 📋 Examples | Dropdown with pre-built example scripts |

### 5.3 Output Panel

- Timestamp-prefixed log lines
- Color-coded by level: info (default), warn (yellow), error (red)
- Auto-scroll with pause-on-scroll-up (same as Trace view)
- Clear button

## 6. Pre-built Example Scripts

Ship 5-6 example scripts in `scripts/examples/`:

1. **Frame Logger** — Log all received frames with timestamp and hex data
2. **DBC Signal Monitor** — Load DBC, print decoded signal values for a specific message
3. **Periodic Send** — Send a frame every 100ms (e.g., heartbeat)
4. **Request-Response** — Send a diagnostic request, wait for response, log result
5. **Signal Statistics** — Track min/max/avg for a signal over time
6. **Bus Load Generator** — Send N frames/sec to stress-test the bus

## 7. Implementation Phases

### Phase A: Core Engine (Backend)
1. Add ClearScript NuGet packages
2. Implement `ScriptEngine` service (V8 lifecycle, sandbox setup)
3. Implement `can.*` API bridge
4. Implement `dbc.*` API bridge
5. Implement utility functions (`log`, `delay`, `hex`, `toHex`)
6. Unit tests for all API functions

### Phase B: IPC + ViewModel
1. Add `ScriptViewModel` (editor state, run/stop commands, output buffer)
2. Wire `ScriptEngine` into DI
3. Implement output buffering (flush at 30 Hz to UI, same pattern as signal chart)
4. Unit tests for ViewModel

### Phase C: UI Integration
1. Add WebView2 NuGet package
2. Create `ScriptView.xaml` with WebView2 + toolbar + output panel
3. Bundle CodeMirror 6 (inline HTML/JS, no external CDN)
4. Wire Run/Stop/Open/Save commands
5. Add "Script" tab to AppShell

### Phase D: Polish + Examples
1. Write 5-6 example scripts
2. Add autocomplete hints for `can.*` and `dbc.*` APIs
3. Error handling (syntax errors, runtime errors, timeouts)
4. Script timeout guard (kill scripts running >60s by default)

## 8. Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `Microsoft.ClearScript.V8` | 7.4.x | V8 JS engine |
| `Microsoft.ClearScript.V8.Native.win-x64` | 7.4.x | V8 native binary (Windows x64) |
| `Microsoft.Web.WebView2` | 1.0.x | Edge WebView2 for CodeMirror |

## 9. File Changes Estimate

| Area | New Files | Modified Files |
|------|-----------|----------------|
| Core (ScriptEngine, APIs) | 8-10 | 2 (DI wiring) |
| ViewModel | 2-3 | 1 (AppShellViewModel) |
| Views | 2-3 | 1 (AppShell.xaml) |
| Tests | 15-20 | 0 |
| Docs + Examples | 6-8 | 1 (README) |
| **Total** | ~35 | ~5 |

## 10. Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| WebView2 not installed on user machine | Script tab won't load | Detect and show install prompt; fallback to plain TextBox editor |
| ClearScript V8 native binary size (~30 MB) | Increases exe size | Acceptable; already at 66 MB |
| Script infinite loop hangs UI | App freeze | 60s timeout + `CancellationToken` on every await |
| Malicious script escapes sandbox | Security | Whitelist-only API; no `eval()`/`Function()` by default |
| V8 engine memory leak on script restart | Memory growth | Dispose + recreate engine on each Run; no engine pooling |

## 11. Open Questions

1. **Should scripts persist across app restarts?** — Propose: save last script to `%LocalAppData%\PeakCan.Host\last-script.js`.
2. **Should we support TypeScript?** — Not in v1.0; JS is sufficient. Can add later via esbuild/wasm.
3. **Max script execution time?** — Propose: 60s default, configurable in settings.
