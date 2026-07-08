# Release Notes v3.16.8 — Smoke test + Console log fallback (PATCH)

**Released:** 2026-07-08
**Parent:** v3.16.7.1 PATCH (`da08f34`)
**Tag:** v3.16.8

## Highlights

User reported "没 log 啊" and "还是没有！！！！" after v3.16.7 and v3.16.7.1. This PATCH adds two **smoke-test log lines** to the very earliest point in app startup and to the very first line of `TraceViewerService` construction, **plus a direct `Console.WriteLine` that bypasses Serilog entirely** so the user can confirm the app is alive and the Serilog pipeline is wired.

If the user can see `[SMOKE v3.16.8]` lines in any output channel, we know:
- The app is starting and reaching the DI host builder
- The DI host builder is constructing TraceViewerService
- Either Serilog is working (we see the lines in the log file too) OR the File sink is broken but the Console.WriteLine still gives us a channel

If the user sees the Console.WriteLine but NOT the Serilog Infor­mation line in the log file, **the Serilog File sink is broken** (path / permissions / config issue) and we can move on to fixing the sink instead of the playback chain.

| What | Where | Purpose |
|------|-------|---------|
| `System.Console.WriteLine("[SMOKE v3.16.8] AppHostBuilder.Build() ENTER ...")` | `Composition/AppHostBuilder.cs:Build()` (after Serilog config) | Bypasses Serilog; always visible when launching PeakCan.Host.exe from cmd / PowerShell. **First proof the app is alive.** |
| `Log.Logger.Information("[SMOKE v3.16.8] ... Serilog Logger ready")` | same line | Goes through Serilog to BOTH the configured File sink (appsettings.json path) AND would go to Console if the Console sink were configured. Proves Serilog at least received the call. |
| `Log.Information("[SMOKE v3.16.8] TraceViewerService ctor ENTER ...")` | `TraceViewerService` ctor (v3.16.7 PATCH already added logger forwarding; v3.16.8 adds the smoke test line) | Proves the DI container built the service AND the logger field is non-null AND Serilog's pipeline is wired to this point. |

## What the user should do after install

1. **Launch from cmd/PowerShell**: `cd "D:\path\to\bin\Debug\net10.0-windows" && .\PeakCan.Host.exe`
2. Open Trace Viewer (loads TraceViewerService → see smoke line in both console and log file)
3. Click ▶ Play
4. **Tell me what they see** in:
   - The console where they launched PeakCan.Host.exe
   - `%LOCALAPPDATA%/PeakCan.Host/logs/peak-{date}.log`
5. **Specifically report whether they see `[SMOKE v3.16.8]` lines and where.**

This breaks the silent-failure loop: even if Serilog is completely broken, we get Console output. Even if Console is hidden, we get the log file. Whichever channel is broken, the user can tell us.

## Why this PATCH ships NO behavior change

v3.16.7.1 was supposed to fix the log silence (forward TraceViewerService's logger to ReplayTimeline). The user reported it still doesn't work. The previous 4 PATCHes each shipped a "this is THE fix" with no instrumentation to verify. This PATCH ships **only instrumentation** — no playback behavior change, no DI change, no XAML change. **If the user can see the SMOKE lines, the previous PATCHes worked and the user's symptom is something else (looking at wrong log file, app not restarted, etc.). If the user cannot see the SMOKE lines, we have isolated the failure to Serilog config or DI host construction.**

## Lessons (1-of-1)

1. **`diagnostic-patch-without-self-verification-is-failure-prone`** — process lesson. v3.16.7 and v3.16.7.1 added logs and a logger-forwarding fix, but neither PATCH added a self-test that the logs themselves are reaching the user. When the user reported "no log", we had no way to distinguish "the playback chain is broken" from "the log machinery is broken". **Every diagnostic PATCH must include a smoke-test line that the user can confirm reached them** — that line must use a different output channel than the diagnostic itself (e.g. direct Console.WriteLine if the diagnostic uses Serilog) so a broken Serilog doesn't hide the smoke test.

2. **`console-sink-is-always-available-but-windows-gui-app-defaults-to-detached-stdout`** — WPF gotcha. The Serilog Console sink requires the app to be launched from a console (cmd / PowerShell) to see output. If launched via Start → Programs → icon, stdout is detached and Console output is silently discarded. **For diagnostic purposes on a Windows GUI app, the File sink is the only universally-visible output channel**. The smoke test in this PATCH uses Console.WriteLine as a best-effort for users who happen to launch from cmd; the File sink remains the canonical log location.

## Files in this PATCH

```
src/Directory.Build.props                        (bump 3.16.7.1 -> 3.16.8)
src/PeakCan.Host.Core/Replay/TraceViewerService.cs    (SMOKE log in ctor)
src/PeakCan.Host.App/Composition/AppHostBuilder.cs    (SMOKE Console.WriteLine + Log.Information after Serilog config)
docs/release-notes-v3.16.8.md                  (this file)
```
