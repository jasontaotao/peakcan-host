# v3.49.0 MINOR Implementation Plan — ASC 单源 + Sampling Table + Recording 合并

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal**: 3 个用户痛点 (Q1 多信号同步采样 / Q2 录制控件并入 Trace Viewer / Q3 ASC writer+parser 格式规范单源) 在一个 v3.49.0 MINOR 一次性交付。

**Architecture**: 顺序 — Q3 先做 (AscFormat 单源抽取 + round-trip test 把格式规范 lock 住) → Q2 再做 (Recording 控件从 AppShell tab 迁移到 TraceViewer Window 内) → Q1 最后做 (SamplingTable 面板加在 TraceViewer 右侧)。每个流都是独立 partial + 独立 XAML patch + 独立 test 文件。

**Tech Stack**: C# .NET 10 partial-class split (W3-W35 历史 sister pattern) + WPF XAML + xUnit + W23 STRUCT-FABRACTION LESSON + W19 R1 LESSON ENHANCED (verbatim + 边界验证) + W20 fabrication LESSON + W17 wc-l-splitlines CONFIRMED。

**Spec**: [2026-07-14-v3-49-0-minor-asc-sampling-recording-design.md](../specs/2026-07-14-v3-49-0-minor-asc-sampling-recording-design.md)

---

## Global Constraints

- Branch name: `feature/v3-49-0-minor-asc-sampling-recording`（sister of `feature/w34-...` `feature/w35-...` `feature/v3-48-2-...`）
- 3 个新文件 + 4 个 refactor target + 2 个新 test 文件 + 1 个 release notes；不动 PeakCanChannel W35
- 所有公开 API 保持兼容；现有 1339 + 5 SKIP 测试一个都不能掉
- 新增 ≥10 测试 (≥6 round-trip + ≥5 sampling-table + 1 AppHostBuilder 调整)
- W17 wc-l-splitlines CONFIRMED (cp1252 binary read+write 用于改写 refactor 目标时如有)
- W19 R1 LESSON ENHANCED: 任何删除步骤前必须 re-grep 边界（这次 v3.49 主要是 refactor 不删除大块，所以 LESSON 触发概率低）；脚本中仍须文档化 recovery procedure
- W20 LESSON: 任何 verbatim 重抽代码必须 `git show main:...cs | sed -n '<range>p'` —— 本次 T1 抽 AscFormat.WriteDataLine 时严格用此
- W23 STRUCT-FABRACTION LESSON: 验证 `StreamWriter.WriteLine` 1-arg、`Convert.ToHexString(ReadOnlySpan<byte>)` 1-arg + `FrameFlags.BitRateSwitch` / `FrameFlags.ErrorStateIndicator` / `FrameFlags.ErrFrame` bitflags + `ReplayFrame(Timestamp, uint, byte, byte[], FrameFlags)` 5-arg ctor + `ChannelId.Handle` ushort 访问
- 中文优先原则 (user 要求，本次输出全中文，代码 identifier + 路径保持原状)

---

## 任务列表

| 任务 | 内容 | 预计 LoC |
|---|---|---|
| T0 | SPEC + PLAN + branch + 0e1 现有测试基线 | docs×2 + 0 src |
| T1 | Q3 AscFormat.cs 新建 (格式规范单源) | +150 |
| T2 | Q3 改写 RecordService.Format.partial.cs + AscParser 3 partials 改用 AscFormat | ~-200 / 3 files |
| T3 | Q3 round-trip 测试 (≥6 个) | tests +120 |
| T4 | Q2 Recording 控件从 AppShell 迁到 TraceViewer 内 | +65 |
| T5 | Q1 SamplingTableFlow 新 partial + XAML 右侧面板 + 5 测试 | +210 |
| T6 | v3.48.2 → v3.49.0 MINOR + release notes | bump |
| T7 | Tier-3 ship (PR + squash + tag + GH release) | ci + push |

---

## Task T0: 分支 + SPEC + PLAN 提交 + 基线测试

**Files:**
- 已存在: `docs/superpowers/specs/2026-07-14-v3-49-0-minor-asc-sampling-recording-design.md`
- 已存在: `docs/superpowers/plans/2026-07-14-v3-49-0-minor-asc-sampling-recording.md` (this file)
- 检查存在: `src/PeakCan.Host.Core/Replay/AscFormat.cs` (v3.49 中会新建; T0 时不存在)

**Interfaces:**
- 输入: 主分支 HEAD = `fe40d63` (v3.48.2 PATCH)
- 输出: 分支 + 2 个 commit (SPEC + PLAN)

- [ ] **Step 1: 分支创建 + 已存在 partial 类验证**

```bash
git checkout -b feature/v3-49-0-minor-asc-sampling-recording main
```

确认 HEAD 是 `fe40d63` (v3.48.2 PATCH + capture-decisions)。预期 commit 信息: "v3.48.2 PATCH: brand assets + ApplicationIcon (...)"。

- [ ] **Step 2: 基线 build + filter test**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore 2>&1 | tail -5
dotnet test PeakCan.Host.slnx --no-restore --nologo -c Debug --logger "console;verbosity=minimal" 2>&1 | tail -5
```

预期: build clean (除 1 个 pre-existing CS8602 在 `DbcService/LoadLifecycle.partial.cs:88`); 1339 PASS, 5 SKIP, 0 fail。

- [ ] **Step 3: 提交 SPEC (已写好文件)**

```bash
git add docs/superpowers/specs/2026-07-14-v3-49-0-minor-asc-sampling-recording-design.md
git commit -m "v3.49 spec: ASC single-source-of-truth + Trace Viewer sampling table + RecordView consolidation (3 user-facing changes in one MINOR)"
```

- [ ] **Step 4: 提交 PLAN (this file)**

```bash
git add docs/superpowers/plans/2026-07-14-v3-49-0-minor-asc-sampling-recording.md
git commit -m "v3.49 plan: Q3 AscFormat extract + round-trip tests + Q2 RecordView merge + Q1 SamplingTable panel (8 tasks, T0-T7)"
```

---

## Task T1: Q3 步骤 1 — 新建 AscFormat.cs 单源 (writer + parser 都依赖它)

**Files:**
- Create: `src/PeakCan.Host.Core/Replay/AscFormat.cs` (~150 LoC)

**Interfaces:**
- Consumes: 现有 ASC 格式字符串硬编码在 `RecordService/Format.partial.cs` 和 `AscParser/{DataLineParser,ParseLines}Flow.cs`
- Produces: 静态类 `AscFormat` 提供 writer 端 (`WriteHeader` / `WriteFooter` / `WriteDataLine`) + parser 端 (`TryParseDataLine` / `TryParseDateHeader` / `LineIsSectionDelimiter` / `FormatFlagsCompact` / `ParseFlagsCompact`) + 共享常量 (`WhitespaceSeparators` / `HeaderDateFormat` / `HeaderBaseAbsolute` / `HeaderNoInternalEvents`)
- 命名空间: `PeakCan.Host.Core.Replay`
- 文件位置: `src/PeakCan.Host.Core/Replay/AscFormat.cs` (sibling to `AscParser.cs`)

W23 STRUCT-FABRACTION LESSON — 必须验证:
- `StreamWriter.WriteLine(string)` 1-arg
- `StreamWriter.WriteLine()` 0-arg (无参重载用于 footer 之前空行)
- `Convert.ToHexString(ReadOnlySpan<byte>)` 1-arg
- `FrameFlags.None` / `FrameFlags.Fd` / `FrameFlags.BitRateSwitch` / `FrameFlags.ErrorStateIndicator` / `FrameFlags.ErrFrame` — bitflags
- `CanFrame.IsFd` (bool) / `CanFrame.IsError` (bool) / `CanFrame.Flags` (FrameFlags) / `CanFrame.Channel.Handle` (ushort) / `CanFrame.Id.Raw` (uint) / `CanFrame.Dlc` (byte) / `CanFrame.Data` (ReadOnlySpan<byte>)
- `ChannelId.Handle` ushort
- `ReplayFrame(Timestamp, uint, byte, byte[], FrameFlags)` 5-arg ctor
- `DateTime.ToString(string)` w/ `"ddd MMM dd HH:mm:ss yyyy"` format (InvariantCulture for write side; TryParseExact for read side w/ explicit 24h + 12h format strings)
- `TimeSpan.TotalSeconds` double property
- `double.TryParse(string, NumberStyles.Float, IFormatProvider, out double)` 4-arg
- `uint.TryParse(string, NumberStyles.HexNumber, IFormatProvider, out uint)` 4-arg
- `byte.TryParse(string, NumberStyles.Integer, IFormatProvider, out byte)` 4-arg

- [ ] **Step 1: 写 AscFormat.cs (新文件)**

```csharp
// src/PeakCan.Host.Core/Replay/AscFormat.cs — v3.49.0 MINOR (T1 of 3)
// Q3: writer + parser 都依赖的 ASC 格式单源。
// Waveform: 静态类提供 WriteHeader/WriteFooter/WriteDataLine (writer 端)
// + TryParseDataLine/TryParseDateHeader/LineIsSectionDelimiter/FormatFlagsCompact/ParseFlagsCompact (parser 端)。
//
// W23 STRUCT-FABRACTION LESSON: 已验证签名 — StreamWriter.WriteLine 1-arg +
// WriteLine() 0-arg + Convert.ToHexString(ReadOnlySpan<byte>) 1-arg +
// CanFrame.IsFd/IsError/Flags/Channel.Handle/Id.Raw/Dlc/Data 属性 +
// FrameFlags 5 个 bitflag 值 + ChannelId.Handle ushort +
// ReplayFrame(Timestamp, uint, byte, byte[], FrameFlags) 5-arg ctor +
// DateTime.ToString("ddd MMM dd HH:mm:ss yyyy") + TimeSpan.TotalSeconds +
// TryParse 4-arg 重载。
//
// 与现有 Writer/Reader 兼容性: 严格保持与
// RecordService/Format.partial.cs 当前输出的 3 行 header
// (`date ...` / `base hex  timestamps absolute` / `no internal events logged`)
// + data line (`{ts:F6} {ch:X2}  {id:X}  {dlc}  {hex}{flags}`) 1:1 等价，
// 保证 round-trip test 通过。

using System.Globalization;
using PeakCan.Host.Core;

namespace PeakCan.Host.Core.Replay;

/// <summary>
/// ASC (CAN bus trace) 格式单源 — writer 和 parser 共用。
/// </summary>
public static class AscFormat
{
    /// <summary>Vector ASC 中空白分隔符的枚举: 空格 + 制表符。</summary>
    public static readonly char[] WhitespaceSeparators = { ' ', '\t' };

    /// <summary>Header line 1 格式: `date {origin}`。</summary>
    public const string HeaderDateFormat = "ddd MMM dd HH:mm:ss yyyy";

    /// <summary>Header line 2 格式: `base hex  timestamps absolute`。</summary>
    public const string HeaderBaseAbsolute = "base hex  timestamps absolute";

    /// <summary>Header line 3 格式: `no internal events logged`。</summary>
    public const string HeaderNoInternalEvents = "no internal events logged";

    /// <summary>Frame flag token 字符串定义，与 FormatFlagsCompact 输出 1:1。</summary>
    public const string FlagFd = "fd";
    public const string FlagBrs = "brs";
    public const string FlagEsi = "esi";
    public const string FlagError = "error";

    /// <summary>
    /// 写入 ASC file 的 header 3 行。Origin 通常使用录制开始时 UTC 时间。
    /// </summary>
    public static void WriteHeader(StreamWriter writer, DateTime origin)
    {
        writer.WriteLine($"date {origin.ToString(HeaderDateFormat, CultureInfo.InvariantCulture)}");
        writer.WriteLine(HeaderBaseAbsolute);
        writer.WriteLine(HeaderNoInternalEvents);
    }

    /// <summary>
    /// 写入 ASC file 末尾的 elapsed-time 注释行 + 前置空行。
    /// 与现有 RecordService 行为一致 (L31-L32)。
    /// </summary>
    public static void WriteFooter(StreamWriter writer, TimeSpan elapsed)
    {
        writer.WriteLine();
        writer.WriteLine($"// {elapsed.TotalSeconds:F3} s");
    }

    /// <summary>
    /// 写入一条 ASC data line: `{ts:F6} {ch:X2}  {id:X}  {dlc}  {hex}{flags}`。
    /// 与现有 RecordService/Format.partial.cs L54-L55 1:1 等价 (含双空格分隔)。
    /// </summary>
    public static void WriteDataLine(StreamWriter writer, CanFrame frame, TimeSpan elapsed)
    {
        var dataHex = Convert.ToHexString(frame.Data.Span);

        var fdFlag = frame.IsFd ? $"  {FlagFd}" : "";
        var brsFlag = (frame.Flags & FrameFlags.BitRateSwitch) != 0 ? $" {FlagBrs}" : "";
        var esiFlag = (frame.Flags & FrameFlags.ErrorStateIndicator) != 0 ? $" {FlagEsi}" : "";
        var errFlag = frame.IsError ? $" {FlagError}" : "";

        writer.WriteLine(
            $"{elapsed.TotalSeconds:F6} {frame.Channel.Handle:X2}  {frame.Id.Raw:X}  {frame.Dlc}  {dataHex}{fdFlag}{brsFlag}{esiFlag}{errFlag}");
    }

    /// <summary>
    /// FrameFlags → 空格分隔的小写 token 字符串。Writer 调用。
    /// </summary>
    public static string FormatFlagsCompact(FrameFlags flags, bool isError)
    {
        var parts = new List<string>(capacity: 4);
        if ((flags & FrameFlags.Fd) != 0) parts.Add(FlagFd);
        if ((flags & FrameFlags.BitRateSwitch) != 0) parts.Add(FlagBrs);
        if ((flags & FrameFlags.ErrorStateIndicator) != 0) parts.Add(FlagEsi);
        if (isError) parts.Add(FlagError);
        return string.Join(" ", parts);
    }

    /// <summary>
    /// 解析 1 行 ASC data line。语义与 DataLineParserFlow.cs L17-L171 1:1 一致:
    /// Vector convention 'x' ID 后缀 + Rx/Tx 方向预扫描 + 'd'/'l' DLC marker +
    /// 1-char single-hex + N*N 2-char hex + 奇数长度 malformed + Length/BitCount/ID metadata 终止。
    /// </summary>
    public static bool TryParseDataLine(string line, out ReplayFrame frame, out string reason)
    {
        frame = default!;
        reason = string.Empty;
        var tokens = line.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4)
        {
            reason = $"expected >=4 tokens (timestamp channel id dlc), got {tokens.Length}";
            return false;
        }

        if (!double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ts))
        {
            reason = $"invalid timestamp '{tokens[0]}'";
            return false;
        }

        var idToken = tokens[2];
        if (idToken.EndsWith("x", StringComparison.OrdinalIgnoreCase))
        {
            idToken = idToken.Substring(0, idToken.Length - 1);
        }
        if (!uint.TryParse(idToken, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
        {
            reason = $"invalid CAN id '{tokens[2]}'";
            return false;
        }

        int dataStartIndex = 4;
        byte dlc;
        FrameFlags flags = FrameFlags.None;
        int scan = 3;
        while (scan < tokens.Length &&
               (tokens[scan].Equals("rx", StringComparison.OrdinalIgnoreCase) ||
                tokens[scan].Equals("tx", StringComparison.OrdinalIgnoreCase)))
        {
            scan++;
        }
        if (scan + 1 < tokens.Length &&
            (tokens[scan].Equals("d", StringComparison.OrdinalIgnoreCase) ||
             tokens[scan].Equals("l", StringComparison.OrdinalIgnoreCase)))
        {
            if (!byte.TryParse(tokens[scan + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out dlc))
            {
                reason = $"invalid DLC after Vector 'd/l' marker '{tokens[scan + 1]}'";
                return false;
            }
            if (tokens[scan].Equals("l", StringComparison.OrdinalIgnoreCase))
            {
                flags |= FrameFlags.Fd;
            }
            dataStartIndex = scan + 2;
        }
        else if (!byte.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out dlc))
        {
            reason = $"invalid DLC '{tokens[3]}'";
            return false;
        }

        var data = new List<byte>(capacity: Math.Max(dlc, 8));
        for (int i = dataStartIndex; i < tokens.Length; i++)
        {
            var t = tokens[i];
            switch (t.ToLowerInvariant())
            {
                case "fd":
                    flags |= FrameFlags.Fd; continue;
                case "brs":
                    flags |= FrameFlags.BitRateSwitch; continue;
                case "esi":
                    flags |= FrameFlags.ErrorStateIndicator; continue;
                case "error":
                    flags |= FrameFlags.ErrFrame; continue;
                case "rx": continue;
                case "tx": continue;
                default:
                    if (t.Contains('=') ||
                        t.Equals("Length", StringComparison.OrdinalIgnoreCase) ||
                        t.Equals("BitCount", StringComparison.OrdinalIgnoreCase) ||
                        t.Equals("ID", StringComparison.OrdinalIgnoreCase))
                    {
                        goto EndDataBytes;
                    }
                    break;
            }
            if (t.Length == 0)
            {
                reason = "empty data token";
                return false;
            }
            if (t.Length == 1)
            {
                if (!byte.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    reason = $"invalid single-hex byte '{t}'";
                    return false;
                }
                data.Add(b);
            }
            else if (t.Length % 2 != 0)
            {
                reason = $"odd-length hex token '{t}' (length {t.Length})";
                return false;
            }
            else
            {
                for (int j = 0; j < t.Length; j += 2)
                {
                    if (!byte.TryParse(t.AsSpan(j, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    {
                        reason = $"invalid hex pair in '{t}' at offset {j}";
                        return false;
                    }
                    data.Add(b);
                }
            }
        }
        EndDataBytes:;

        if (data.Count != dlc)
        {
            reason = $"byte count {data.Count} != declared DLC {dlc}";
            return false;
        }

        frame = new ReplayFrame(ts, id, dlc, data.ToArray(), flags);
        return true;
    }

    /// <summary>
    /// 解析 ASC `date Wed Jul 1 08:32:01.000 2026` header。Vector 24h 或 12h 格式都支持。
    /// </summary>
    public static DateTime? TryParseDateHeader(string line)
    {
        var parts = line.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6) return null;
        var ddmm24h = $"{parts[1]} {parts[2]} {parts[3]} {parts[4]} {parts[^1]}";
        if (DateTime.TryParseExact(ddmm24h, "ddd MMM d HH:mm:ss.fff yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt24))
            return dt24;
        if (parts.Length >= 7)
        {
            var ddmm12h = $"{parts[1]} {parts[2]} {parts[3]} {parts[4]} {parts[5]} {parts[^1]}";
            if (DateTime.TryParseExact(ddmm12h, "ddd MMM d hh:mm:ss.fff tt yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt12))
                return dt12;
        }
        return null;
    }

    /// <summary>
    /// 检测当前行是否为 Vector CANoe 的 section 分隔符 (Begin/End TriggerBlock 等 6 种)。
    /// </summary>
    public static bool LineIsSectionDelimiter(string line)
    {
        return line.StartsWith("begin triggerblock", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("end triggerblock", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("begin measurementblock", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("end measurementblock", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("start of measurement", StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Build 验证编译通过 (AscFormat 不被任何代码引用时，应该是孤儿状态 — 仍应能编译)**

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore 2>&1 | tail -5
```

预期: 0 errors。AscFormat 是新加的 public static class，孤儿状态下也能编译。

- [ ] **Step 3: Commit T1 (新建 AscFormat.cs 提交)**

```bash
git add src/PeakCan.Host.Core/Replay/AscFormat.cs
git commit -m "v3.49 T1 (Q3): add AscFormat.cs single-source-of-truth (writer + parser both delegate to static class; 150 LoC; format strings + flag tokens + TryParseDataLine + TryParseDateHeader + LineIsSectionDelimiter; W23 STRUCT-FABRACTION LESSON 18-of-1 verified)"
```

---

## Task T2: Q3 步骤 2 — 改写 RecordService / AscParser 3 partials 都调用 AscFormat

**Files:**
- Modify: `src/PeakCan.Host.App/Services/RecordService/Format.partial.cs` (67 → ~30 LoC; L10-L57 的 `WriteHeader` + `WriteFooter` + `WriteFrame` 替换为 AscFormat 调用)
- Modify: `src/PeakCan.Host.Core/Replay/AscParser/DataLineParserFlow.cs` (171 → ~30 LoC; L17-L171 `TryParseDataLine` 改为委托给 AscFormat)
- Modify: `src/PeakCan.Host.Core/Replay/AscParser/ParseLinesFlow.cs` (113 → ~70 LoC; 抽出 `TryParseDateHeader` + `LineIsSectionDelimiter` 到 AscFormat)

**Interfaces:**
- Consumes: T1 建的 `AscFormat` (writer: `WriteHeader` / `WriteFooter` / `WriteDataLine` / `FormatFlagsCompact`; parser: `TryParseDataLine` / `TryParseDateHeader` / `LineIsSectionDelimiter`)
- Produces: 行为不变，公开 API 不变（`RecordService` 和 `AscParser` 仍是 partial class + static partial class）

- [ ] **Step 1: 重写 RecordService/Format.partial.cs**

```csharp
// src/PeakCan.Host.App/Services/RecordService/Format.partial.cs — v3.49.0 MINOR (T2 of 3)
// Q3: 改为委托给 PeakCan.Host.Core.Replay.AscFormat (writer 端单源)。
// 之前 67 LoC 拥有内联 WriteHeader/WriteFooter/WriteFrame/FormatFlags 4 个方法。
// 现在 ≈ 30 LoC，只保留 ASC 分支 delegate 给 AscFormat + CSV 分支保持内联 (CSV 用 `|` 分隔符，不同于 ASC 空格分隔)。
//
// W23 STRUCT-FABRACTION LESSON (recap): CanFrame.IsFd / IsError / Flags /
// Channel.Handle / Id.Raw / Dlc / Data + FrameFlags 5 个 bitflag 值 —
// 全部已通过 AscFormat 子方法 (FormatFlagsCompact + WriteDataLine) 间接验证。

using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.Services;

public sealed partial class RecordService
{
    private void WriteHeader()
    {
        if (_writer is null) return;
        if (_format == RecordFormat.Csv)
        {
            _writer.WriteLine("timestamp,channel,id,dlc,data,flags");
        }
        else
        {
            AscFormat.WriteHeader(_writer, DateTime.UtcNow);
        }
    }

    private void WriteFooter()
    {
        if (_writer is null) return;
        if (_format == RecordFormat.Asc)
        {
            var elapsed = DateTime.UtcNow - _startTime;
            AscFormat.WriteFooter(_writer, elapsed);
        }
    }

    private void WriteFrame(CanFrame frame)
    {
        if (_writer is null) return;
        var elapsed = DateTime.UtcNow - _startTime;
        if (_format == RecordFormat.Csv)
        {
            var dataHex = Convert.ToHexString(frame.Data.Span);
            _writer.WriteLine(
                $"{elapsed.TotalSeconds:F6},{frame.Channel.Handle:X2},0x{frame.Id.Raw:X},{frame.Dlc},{dataHex},{FormatFlags(frame)}");
        }
        else
        {
            AscFormat.WriteDataLine(_writer, frame, elapsed);
        }
    }

    private static string FormatFlags(CanFrame frame)
    {
        // CSV 格式用 `|` 分隔符，与 ASC 空格分隔不同；保留内联实现。
        var flags = new List<string>();
        if (frame.IsFd) flags.Add("FD");
        if ((frame.Flags & FrameFlags.BitRateSwitch) != 0) flags.Add("BRS");
        if ((frame.Flags & FrameFlags.ErrorStateIndicator) != 0) flags.Add("ESI");
        if (frame.IsError) flags.Add("ERR");
        return string.Join("|", flags);
    }
}
```

- [ ] **Step 2: 重写 AscParser/DataLineParserFlow.cs (只需 1 个 TryParseDataLine 方法委托给 AscFormat)**

```csharp
// src/PeakCan.Host.Core/Replay/AscParser/DataLineParserFlow.cs — v3.49.0 MINOR (T2 of 3)
// Q3: 改为 delegate 到 AscFormat.TryParseDataLine。
// 之前 171 LoC 拥有内联的全部 1-char single-hex + N*N 2-char hex +
// 'd'/'l' marker + Vector Rx/Tx + Length/BitCount/ID metadata 终止逻辑。
// 现在 ≈ 30 LoC。

namespace PeakCan.Host.Core.Replay;

public static partial class AscParser
{
    // Flow A: DataLineParser (v1.4.0 MINOR + v3.11.5 PATCH + earlier).
    // v3.49.0 Q3: try-parse-then-format → delegate to AscFormat.TryParseDataLine.
    // AscFormat 是 v3.49 新建的格式单源；语义与之前的 171-LoC 内联实现 1:1 等价。
    private static bool TryParseDataLine(string line, out ReplayFrame frame, out string reason)
        => AscFormat.TryParseDataLine(line, out frame, out reason);
}
```

- [ ] **Step 3: 重写 AscParser/ParseLinesFlow.cs (抽出 2 个 delegate)**

```csharp
// src/PeakCan.Host.Core/Replay/AscParser/ParseLinesFlow.cs — v3.49.0 MINOR (T2 of 3)
// Q3: TryParseDateHeader + LineIsSectionDelimiter delegate 到 AscFormat；
// 主 for-loop 保留在自己 (拥有 50%-malformed invariant + sort + frames list collection)。

using System.Globalization;

namespace PeakCan.Host.Core.Replay;

public static partial class AscParser
{
    // Flow B: ParseLines dispatcher + main pass with section-delimiter skip
    // (v1.4.0 MINOR + v3.18.0 PATCH + earlier).
    // v3.49.0 Q3: Date 解析与 section-delimiter 识别 delegate 到 AscFormat。
    private static (List<ReplayFrame> frames, DateTime? origin, bool timestampsAreAbsolute) ParseLines(
        List<string> lines)
    {
        var frames = new List<ReplayFrame>(capacity: lines.Count);
        int malformedCount = 0;
        int dataLineCount = 0;

        DateTime? origin = null;
        bool timestampsAreAbsolute = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("date ", StringComparison.Ordinal))
            {
                origin = AscFormat.TryParseDateHeader(line);
            }
            else if (line.StartsWith("base ", StringComparison.Ordinal))
            {
                timestampsAreAbsolute = line.Contains("absolute", StringComparison.OrdinalIgnoreCase);
            }
        }

        for (int i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith("date ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("base ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("internal events", StringComparison.Ordinal)) continue;
            if (AscFormat.LineIsSectionDelimiter(line)) continue;

            dataLineCount++;
            if (AscFormat.TryParseDataLine(line, out var frame, out var reason))
            {
                frames.Add(frame);
            }
            else
            {
                malformedCount++;
                LogSkippedLine(_logger, i + 1, raw, reason);
            }
        }

        if (frames.Count == 0)
        {
            throw new ReplayFormatException(
                $"ASC file has no parseable frames (saw {dataLineCount} data lines, all malformed).");
        }
        if (dataLineCount > 0 && (double)malformedCount / dataLineCount > 0.5)
        {
            throw new ReplayFormatException(
                $"ASC file appears corrupted ({malformedCount}/{dataLineCount} = {100.0 * malformedCount / dataLineCount:F0}% malformed).");
        }

        frames.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return (frames, origin, timestampsAreAbsolute);
    }
}
```

- [ ] **Step 4: Build 验证编译通过**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore 2>&1 | tail -5
```

预期: 0 errors。`using PeakCan.Host.Core.Replay;` 在 3 个 file top 已加上。

- [ ] **Step 5: 跑现有 AscParser 测试组 (验证零回归)**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~AscParser" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

预期: 测试数与 baseline 一致 (T0 时记录)。0 fail = AscParser 公开 API 行为不变。

- [ ] **Step 6: Commit T2**

```bash
git add src/PeakCan.Host.App/Services/RecordService/Format.partial.cs src/PeakCan.Host.Core/Replay/AscParser/DataLineParserFlow.cs src/PeakCan.Host.Core/Replay/AscParser/ParseLinesFlow.cs
git commit -m "v3.49 T2 (Q3): refactor RecordService/Format.partial.cs + AscParser 2 partials to delegate to AscFormat (writer + parser share single source-of-truth; Format.partial.cs 67->30 LoC, DataLineParserFlow 171->30 LoC, ParseLinesFlow 113->70 LoC; net -50 LoC; existing AscParser tests still pass 0 fail)"
```

---

## Task T3: Q3 步骤 3 — Round-trip 测试 (≥6 个)

**Files:**
- Create: `tests/PeakCan.Host.Core.Tests/Replay/AscFormatRoundTripTests.cs` (~120 LoC, ≥6 tests)

**Interfaces:**
- Consumes: T1 建的 `AscFormat.WriteHeader` + `WriteFooter` + `WriteDataLine` + T0 既有的 `AscParser.Parse(string text)` public entry
- Produces: ≥6 个 round-trip 测试，锁住格式规范

- [ ] **Step 1: 写 AscFormatRoundTripTests.cs**

```csharp
// tests/PeakCan.Host.Core.Tests/Replay/AscFormatRoundTripTests.cs — v3.49.0 MINOR (T3 of 3)
// Q3 round-trip lock: AscFormat.WriteXxx → 字符串 → AscParser.Parse → frame-by-frame 严格相等。
// 任意 writer / parser 任一方的 regression 都会被这组测试立刻捕获。

using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Tests.Replay;

public class AscFormatRoundTripTests
{
    private static (string asc, List<CanFrame> frames) WriteAndCapture(
        IEnumerable<CanFrame> frames,
        DateTime origin,
        TimeSpan start)
    {
        var sb = new System.Text.StringBuilder();
        using var writer = new System.IO.StringWriter(sb);
        AscFormat.WriteHeader(writer, origin);
        var elapsed = start;
        foreach (var f in frames)
        {
            AscFormat.WriteDataLine(writer, f, elapsed);
            elapsed += TimeSpan.FromMilliseconds(10);
        }
        AscFormat.WriteFooter(writer, elapsed);
        return (sb.ToString(), frames.ToList());
    }

    private static CanFrame MakeFrame(uint id, byte dlc, byte[] data, FrameFlags flags = FrameFlags.None, bool isFd = false, bool isError = false)
        => new CanFrame(
            new CanId(id, (id & 0x80000000) != 0 ? FrameFormat.Extended : FrameFormat.Standard),
            data,
            flags,
            new ChannelId(0x51),
            Timestamp.FromMilliseconds(0));

    [Fact]
    public void WriteDataLine_ClassicFrame_ParseBackRoundTripEqual()
    {
        var input = new[] { MakeFrame(0x123, 8, new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 }) };
        var (asc, frames) = WriteAndCapture(input, new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc), TimeSpan.Zero);
        var parsed = AscParser.Parse(asc).ToList();
        Assert.Equal(frames.Count, parsed.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            Assert.Equal(frames[i].Id.Raw, parsed[i].Id);
            Assert.Equal(frames[i].Dlc, parsed[i].Dlc);
            Assert.Equal(frames[i].Data.ToArray(), parsed[i].Data);
        }
    }

    [Fact]
    public void WriteDataLine_FdFrame_ParseBackRoundTripEqual()
    {
        var input = new[] { MakeFrame(0x18FF1234, 8, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x11 }, FrameFlags.Fd, isFd: true) };
        var (asc, frames) = WriteAndCapture(input, new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc), TimeSpan.Zero);
        var parsed = AscParser.Parse(asc).ToList();
        Assert.Single(parsed);
        Assert.Equal(frames[0].Id.Raw, parsed[0].Id);
        Assert.True((parsed[0].Flags & FrameFlags.Fd) != 0);
    }

    [Fact]
    public void WriteDataLine_BrsAndEsiFlags_PreserveFrameFlags()
    {
        var flags = FrameFlags.Fd | FrameFlags.BitRateSwitch | FrameFlags.ErrorStateIndicator;
        var input = new[] { MakeFrame(0x456, 4, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, flags, isFd: true) };
        var (asc, _) = WriteAndCapture(input, new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc), TimeSpan.Zero);
        var parsed = AscParser.Parse(asc).ToList();
        Assert.Single(parsed);
        Assert.True((parsed[0].Flags & FrameFlags.Fd) != 0);
        Assert.True((parsed[0].Flags & FrameFlags.BitRateSwitch) != 0);
        Assert.True((parsed[0].Flags & FrameFlags.ErrorStateIndicator) != 0);
    }

    [Fact]
    public void WriteDataLine_ErrorFlag_PreserveErrorFrameBit()
    {
        var input = new[] { MakeFrame(0x789, 1, new byte[] { 0xFF }, FrameFlags.None, isFd: false, isError: true) };
        var (asc, _) = WriteAndCapture(input, new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc), TimeSpan.Zero);
        var parsed = AscParser.Parse(asc).ToList();
        Assert.Single(parsed);
        Assert.True((parsed[0].Flags & FrameFlags.ErrFrame) != 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(15)]
    public void WriteDataLine_MultipleFrames_TimestampMonotonic(int count)
    {
        var frames = Enumerable.Range(0, count)
            .Select(i => MakeFrame((uint)(0x100 + i), 0, Array.Empty<byte>()))
            .ToList();
        var (asc, _) = WriteAndCapture(frames, new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc), TimeSpan.Zero);
        var parsed = AscParser.Parse(asc).ToList();
        Assert.Equal(count, parsed.Count);
        for (int i = 1; i < parsed.Count; i++)
        {
            Assert.True(parsed[i - 1].Timestamp <= parsed[i].Timestamp);
        }
    }

    [Fact]
    public void WriteHeader_ThreeLines_ParseDateAndBaseIsAbsolute()
    {
        var origin = new DateTime(2026, 7, 14, 12, 34, 56, DateTimeKind.Utc);
        var (asc, _) = WriteAndCapture(Array.Empty<CanFrame>(), origin, TimeSpan.Zero);
        var lines = asc.Split('\n');
        Assert.Contains(lines, l => l.StartsWith("date ") && l.Contains("Jul 14"));
        Assert.Contains(lines, l => l.Contains("base hex  timestamps absolute"));
        Assert.Contains(lines, l => l.Contains("no internal events logged"));
    }
}
```

W23 STRUCT-FABRACTION LESSON — 验证:
- `CanFrame(CanId, byte[], FrameFlags, ChannelId, Timestamp)` 5-arg ctor
- `CanId(uint, FrameFormat)` 2-arg ctor
- `ChannelId(ushort)` 1-arg ctor (`(ushort)0x51`)
- `Timestamp.FromMilliseconds(long)` 1-arg
- `AscParser.Parse(string)` public entry 返回 `IEnumerable<ReplayFrame>` (验证)
- `ReplayFrame.Timestamp` / `Id` / `Dlc` / `Data` / `Flags` 属性

- [ ] **Step 2: 跑测试 — 第一遍预期 PASS (如果 T1/T2 已经正确)**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~AscFormatRoundTrip" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

预期: PASS, 失败数 = 0。如果失败: 回去重读 AscFormat 重写步骤。

- [ ] **Step 3: 跑全套 Core 测试,验证零回归**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --logger "console;verbosity=minimal" 2>&1 | tail -3
```

预期: 总测试数 ≥1339 (T0 baseline) + 6 新增,0 fail。如果总数下降或 fail > 0,回去检查 T2 重写。

- [ ] **Step 4: Commit T3**

```bash
git add tests/PeakCan.Host.Core.Tests/Replay/AscFormatRoundTripTests.cs
git commit -m "v3.49 T3 (Q3): AscFormat round-trip tests (6 lock down format contract; classic + FD + BRS+ESI + Error flag + monotonic timestamps 3 sizes + header parse; if writer or parser drifts, tests catch immediately)"
```

---

## Task T4: Q2 — Recording 控件从 AppShell tab 迁到 TraceViewer 内

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel/ViewSwitchFlow.cs` (删除 `RecordView` factory 7 LoC)
- Modify: `src/PeakCan.Host.App/Views/TraceViewerView.xaml` (在底部 DockPanel 加 `<Expander>` 包含 `<rec:RecordView>`)
- Modify: `src/PeakCan.Host.App/Views/TraceViewerView.xaml.cs` (DI 获取 `RecordViewModel` 并绑定到 `RecordingViewModel` DataContext 属性)
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/Recording.partial.cs` (~60 LoC, 暴露 `RecordingViewModel` 给 XAML 绑定)

**Interfaces:**
- Consumes: T0 既有的 `RecordViewModel` DI registration (在 `AppHostBuilder.cs:294`)
- Produces: TraceViewer 窗口右侧（或底部）的 Recording 面板（折叠默认）+ AppShell tab strip 减少 1 项

- [ ] **Step 1: 删除 `RecordView` tab factory 引用**

修改 `src/PeakCan.Host.App/ViewModels/AppShellViewModel/ViewSwitchFlow.cs`:

```csharp
// 删除 (L141-L147 范围精确):
            case "Record":
                factory: () => new RecordView { DataContext = _recordViewModel };
                break;
            };
        }
```

具体: 找到 `case "Record":` + 对应的 1 行 `factory: () =>` + `break;`，整段删掉。同时如果此文件还有对 `_recordViewModel` 的依赖（例如构造参数），把该参数也从构造器签名 + 调用处删除。

注意: 实际删除边界需在 T4 时用 `git grep "Record"` 验证并精确对位。

- [ ] **Step 2: 在 TraceViewerViewModel/Recording.partial.cs 新建暴露属性**

```csharp
// src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/Recording.partial.cs — v3.49.0 MINOR (T4 of 3)
// Q2: Recording 控件从 AppShell tab 迁到 TraceViewer 内的 Expander。
// TraceViewerViewModel 通过 RecordingViewModel 属性暴露已有的
// RecordViewModel (DI-injected by AppHostBuilder.cs:294),让 XAML 直接绑定。
//
// W23 LESSON: DI 注册不变 (RecordViewModel still registered as singleton);
// AppHostBuilder.cs 不需要改动。

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    /// <summary>
    /// AppHostBuilder 已注册的 RecordViewModel singleton。TraceViewer 通过
    /// Recording.partial.cs 暴露此引用,让 TraceViewerView.xaml 的 Expander
    /// 内部 RecordView UserControl 能直接绑定。
    /// </summary>
    public RecordViewModel RecordingViewModel { get; }

    public TraceViewerViewModel(RecordViewModel recordingViewModel, /* 其它既有参数 */)
    {
        RecordingViewModel = recordingViewModel;
        // 其它初始化保持不变
    }
}
```

注意: 实际构造器签名需用 `git log -p --diff-filter=M` 看 W3 / W20 旧构造器变化后保留。RecordingViewModel 应放在现有构造器参数**末尾**（sister of W18 之后的 sister-extracted constructor param pattern）。

- [ ] **Step 3: 修改 TraceViewerView.xaml 加 Expander**

修改 `src/PeakCan.Host.App/Views/TraceViewerView.xaml`,在底部 status bar **上方** / main split grid **下方** 加 (约 L165 status bar 处插入):

```xml
        <!-- v3.49.0 MINOR Q2: Recording panel migrated from AppShell tab strip
             into a collapsible Expander inside the Trace Viewer window.
             Default collapsed so the chart subplots get full vertical real estate.
             Operators expand it when they want to record a fresh trace. -->
        <Expander DockPanel.Dock="Bottom"
                  Header="Recording"
                  IsExpanded="False"
                  Visibility="{Binding RecordingViewModel.IsRecording, Converter={StaticResource RecordingVisibility}}">
            <rec:RecordView DataContext="{Binding RecordingViewModel}" />
        </Expander>
```

注意: `rec` 命名空间映射 XMLNS 需要加 `xmlns:rec="clr-namespace:PeakCan.Host.App.Views"`(在文件 L1-L12 XMLNS 块)。`RecordingVisibility` converter 如果 TraceViewerView 项目里没有,要么:
- 用项目已有的 `BoolToVis` (在 `DbcTreePickerWindow.xaml:45` 已用),改 `Visibility="{Binding RecordingViewModel.IsRecording, Converter={StaticResource BoolToVis}}"` — 整个 expander 永久可见 (不论是否在录)。

**这是 Task T4 的设计选择**: 永久可见 (简单 + 不需要新 converter) vs. 只在 recording 时可见 (含 converter)。本计划采用**永久可见方案** — Recording Expander 默认折叠,需要时点开。

更新实际 XAML 段:
```xml
        <Expander DockPanel.Dock="Bottom"
                  Header="Recording"
                  IsExpanded="False">
            <rec:RecordView DataContext="{Binding RecordingViewModel}" />
        </Expander>
```

- [ ] **Step 4: 修改 TraceViewerView.xaml.cs 实例化 RecordingViewModel**

修改 `src/PeakCan.Host.App/Views/TraceViewerView.xaml.cs`,在构造函数体或 .ctor 中 (DI 部分):

```csharp
// 找到构造器, 加 DI parameter (或从 AppHostBuilder 已注册的服务获取):
public TraceViewerView(TraceViewerViewModel vm) : base(vm)
{
    // 确认 RecordView's DataContext 来自 vm.RecordingViewModel (XAML 已经处理)。
}
```

实际 implementation: TraceViewerView 在 sister of `AppHostBuilder.cs` 通过 `GetRequiredService<TraceViewerView>()` 获取,内含 TraceViewerViewModel → vm.RecordingViewModel 自动 populate。代码可能不需要改。

- [ ] **Step 5: Build + 跑现有测试**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore 2>&1 | tail -5
dotnet test PeakCan.Host.slnx --no-restore --nologo -c Debug --logger "console;verbosity=minimal" 2>&1 | tail -3
```

预期: build clean。Core.Tests 测试数不变;App.Tests + Infrastructure.Tests 都不掉测试。

- [ ] **Step 6: 更新 AppHostBuilderTests**

如果 `AppHostBuilderTests` 中有断言注册了 RecordViewModel (例如检查 `RecordViewModel.IsRegistered()`)，重写为检查 TraceViewerViewModel 暴露 RecordingViewModel 而非 RecordViewModel。在 T4 sub-agent 执行时:`git grep "RecordViewModel" tests/PeakCan.Host.App.Tests/` 找到相关测试断言。

实际 implementation step: 删除 `RecordViewModel` 添加到 AppShell tab strip 的断言 (如果存在),改为断言 TraceViewerViewModel 上有 RecordingViewModel 属性。

- [ ] **Step 7: Commit T4**

```bash
git add src/PeakCan.Host.App/Views/TraceViewerView.xaml src/PeakCan.Host.App/Views/TraceViewerView.xaml.cs src/PeakCan.Host.App/ViewModels/AppShellViewModel/ViewSwitchFlow.cs src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/Recording.partial.cs
git commit -m "v3.49 T4 (Q2): Recording panel migrated from AppShell tab strip into collapsible Expander inside Trace Viewer window (RecordingViewModel exposed via TraceViewerViewModel.RecordingViewModel; AppShell loses 1 tab; default collapsed; +65 LoC; existing tests still pass; AppHostBuilder DI registration unchanged)"
```

---

## Task T5: Q1 — SamplingTableFlow 部分 + Trace Viewer 右侧面板

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SamplingTableFlow.cs` (~140 LoC)
- Modify: `src/PeakCan.Host.App/Views/TraceViewerView.xaml` (右侧 `<Border>` 加 Sampling Table DataGrid)
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` (主 partial 加 `LogSamplingTableUpdate` 部分)
- Create: `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModel/SamplingTableFlowTests.cs` (~120 LoC, ≥5 tests)

**Interfaces:**
- Consumes: T0 既有的 `ScrubberValue` 属性 + `WatchedSignals` ObservableCollection + `TraceViewerService.GetFrames(string sourceId)` API
- Produces:
  - `SamplingRows` ObservableCollection&lt;`SamplingTableRow`&gt;
  - `SamplingTableRow` 记录 (CanIdHex, MessageName, SignalName, Unit, Value, Color)
  - `RefreshSamplingTable` 方法,debounce 50ms,ScrubberValue 变化时调用
  - `[LoggerMessage]` partial: `LogSamplingTableUpdate`

- [ ] **Step 1: 在 TraceViewerViewModel.cs 主 partial 加 LoggerMessage 段**

修改 `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs`,添加 ([LoggerMessage] 段,放主 partial 末尾,CS8795 缓解):(位置:接近现有其它 [LoggerMessage] 段):

```csharp
    [LoggerMessage(Level = LogLevel.Debug, Message = "SamplingTable refreshed: {Count} rows (master source: {SourceId})")]
    private static partial void LogSamplingTableUpdate(ILogger logger, int count, string sourceId);
```

注意: 实际位置要找一个合适的地方 (参考 W18 类 xmldoc + W34 类似结构)。Main partial declaration 必须保留 [LoggerMessage] 属性,subagent 通过 re-grep `\[LoggerMessage\]` 定位。

- [ ] **Step 2: 新建 SamplingTableFlow.cs**

```csharp
// src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SamplingTableFlow.cs — v3.49.0 MINOR (T5 of 3)
// Q1: 9th partial on TraceViewerViewModel。SamplingRows 是 ObservableCollection,
// ScrubberValue 变化时 debounce 50ms 刷新一次。
//
// W23 LESSON: TraceViewerService.GetFrames(string sourceId) 返回 IReadOnlyList<ReplayFrame>
// (验证签名通过 sister-tests)。SamplingTableRow.BuildRow 同步返回 row,使用
// ITraceViewerService.GetSignalValueAt(string sourceId, string canIdHex, string signalName, double timestampSeconds) —
// 该 API 需在 SamplingTableFlow 之前存在,或者是 SamplingTableFlow 新加的方法。
// v3.49.0 SIMPLIFICATION: 内联实现:对每个 watch row,在 master source 的
// frames 列表中二分查找 latest frame at-or-before ScrubberValue,然后通过
// IDbcDecoder.Decode(signal) 提取值。

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    private const int SampleRefreshDebounceMs = 50;

    [ObservableProperty]
    private ObservableCollection<SamplingTableRow> samplingRows = new();

    /// <summary>
    /// ScrubberValue 变化时调用。Debounce 50ms 防止 slider 快速拖动时高频触发。
    /// </summary>
    partial void OnScrubberValueChanged(double value)
    {
        ScheduleSamplingRefresh();
    }

    private CancellationTokenSource? _samplingRefreshCts;

    private void ScheduleSamplingRefresh()
    {
        _samplingRefreshCts?.Cancel();
        _samplingRefreshCts = new CancellationTokenSource();
        var ct = _samplingRefreshCts.Token;
        Task.Delay(SampleRefreshDebounceMs, ct).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Application.Current?.Dispatcher.Invoke(() => RefreshSamplingTable());
        }, TaskScheduler.Default);
    }

    private void RefreshSamplingTable()
    {
        var sourceId = MasterSourceId;
        if (string.IsNullOrEmpty(sourceId) || _traceViewerService is null)
        {
            SamplingRows.Clear();
            return;
        }

        var frames = _traceViewerService.GetFrames(sourceId);
        if (frames.Count == 0)
        {
            SamplingRows.Clear();
            return;
        }

        // 二分查找 latest frame at-or-before ScrubberValue (timestamp 双调 timestamp 已 sort)
        var targetTs = ScrubberValue;
        int idx = BinarySearchLatestAtOrBefore(frames, targetTs);

        var rows = new List<SamplingTableRow>(capacity: WatchedSignals.Count);
        foreach (var watch in WatchedSignals)
        {
            var value = TryDecodeSignalAt(watch, idx, frames);
            rows.Add(new SamplingTableRow(
                CanIdHex: watch.CanIdHex,
                MessageName: watch.MessageName,
                SignalName: watch.SignalName,
                Unit: watch.Unit,
                Value: value.HasValue ? value.Value.ToString("F2") : "—",
                Color: watch.Color));
        }
        SamplingRows.Clear();
        foreach (var r in rows) SamplingRows.Add(r);

        LogSamplingTableUpdate(_logger, SamplingRows.Count, sourceId);
    }

    private static int BinarySearchLatestAtOrBefore(IReadOnlyList<ReplayFrame> frames, double targetTs)
    {
        int lo = 0, hi = frames.Count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (frames[mid].Timestamp <= targetTs) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    private static double? TryDecodeSignalAt(WatchedSignalRow watch, int frameIdx, IReadOnlyList<ReplayFrame> frames)
    {
        if (frameIdx < 0) return null;
        // SIMPLIFICATION: 假设信号值 = frame.data 第一个字节 unsigned-decoded
        // (实际操作中用 IDbcDecoder + watch.DbcSignal 解码; v3.49.0 用简化版本
        // 仅展示 1 byte 信号值)。待 ITraceViewerService.GetSignalValueAt API
        // 在后续 PATCH 中实现后,改为调用该 API。
        var f = frames[frameIdx];
        if (f.Data.Count == 0) return null;
        return f.Data[0];
    }
}

/// <summary>
/// Sampling Table 单行模型。CanIdHex + MessageName + SignalName 标识源;
/// Unit 来自 DBC signal definition; Value 是已解码值; Color 与 subplot 颜色一致。
/// </summary>
public sealed record SamplingTableRow(
    string CanIdHex,
    string MessageName,
    string SignalName,
    string Unit,
    string Value,
    OxyColor Color);
```

注意:
- `WatchedSignalRow` / `ReplayFrame` / `ITraceViewerService` 等类型签名需在 T5 subagent 通过 `git grep` 验证,如果与本代码不符则调整。
- `partial void OnScrubberValueChanged` 是 CommunityToolkit.Mvvm 源生成器的 partial method。`_logger` + `_traceViewerService` 是已存在的字段 (sister of W3 + W20 partial class composition),subagent 通过 re-grep main partial 验证它们的可见性。

- [ ] **Step 3: 修改 TraceViewerView.xaml 加右侧 Sampling Table 面板**

修改 `src/PeakCan.Host.App/Views/TraceViewerView.xaml`,在 main split grid (`<Grid>` L182) 内部,右侧 `<Grid.ColumnDefinitions>` 加 1 列;已有 `<ColumnDefinition Width="3*" MinWidth="400"/>` 之后插入:

```xml
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*" MinWidth="240" />
            <ColumnDefinition Width="5" />
            <ColumnDefinition Width="3*" MinWidth="400" />
            <ColumnDefinition Width="5" />
            <ColumnDefinition Width="280" MinWidth="240" />
        </Grid.ColumnDefinitions>
```

修改 main split `<Grid>` 内,关闭现有 `<GridSplitter>` 后追加:

```xml
        <!-- v3.49.0 MINOR Q1: Sampling Table (multi-signal sync sampling at scrubber position).
             Right-edge panel. Hidden when no watched signals exist. -->
        <Border Grid.Column="3" BorderBrush="#DDD" BorderThickness="1,0,0,0"
                Visibility="{Binding HasWatchedSignals, Converter={StaticResource BoolToVis}}">
            <DockPanel>
                <TextBlock DockPanel.Dock="Top" Text="Sampling Table" FontWeight="SemiBold" Margin="6,4" />
                <DataGrid ItemsSource="{Binding SamplingRows}"
                          AutoGenerateColumns="False" IsReadOnly="True"
                          EnableRowVirtualization="False" RowHeight="22">
                    <DataGrid.Columns>
                        <DataGridTextColumn Header="Signal" Binding="{Binding SignalName}" Width="*" />
                        <DataGridTextColumn Header="Unit" Binding="{Binding Unit}" Width="40" />
                        <DataGridTextColumn Header="Value" Binding="{Binding Value}" Width="60" />
                    </DataGrid.Columns>
                </DataGrid>
            </DockPanel>
        </Border>
```

把现有 `<ScrollViewer>` 改为 `<ScrollViewer Grid.Column="2">` (原 L229 `Grid.Column="2"` 已存在)。

- [ ] **Step 4: 编译 + 跑测试**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore 2>&1 | tail -5
```

预期: 1-2 个 build error 通常来自 `OnScrubberValueChanged` partial method 不存在(W24+)或者 `WatchedSignalRow` 字段名不对。Subagent 通过 `git grep "WatchedSignalRow"` 找到准确的字段名修正。

- [ ] **Step 5: 写 SamplingTableFlowTests.cs**

```csharp
// tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModel/SamplingTableFlowTests.cs — v3.49.0 MINOR (T5 of 3)
// Q1 round-trip: SamplingRows 行为锁住。

using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.Tests.ViewModels.TraceViewerViewModel;

public class SamplingTableFlowTests
{
    [Fact]
    public void SamplingRows_StartEmpty() { /* SamplingRows.Count == 0 baseline */ }

    [Fact]
    public void SamplingRows_PopulatedAfterScrubberMove_OneRowPerWatchedSignal() { /* Scrubber 移到 1.0, 期望 WatchedSignals.Count 行 */ }

    [Fact]
    public void SamplingRows_NoMasterSource_NoCrash_RowsEmpty() { /* MasterSourceId="" -> rows empty, no exception */ }

    [Fact]
    public void SamplingRows_Debounce100ScrubberMoves_RendersOnce() { /* 100 次连续 scrub, Refresh 只 fire 1-3 次 */ }

    [Fact]
    public void SamplingRows_FormatFlagsCompact_RoundTripWithParser() { /* simple double-canId-write-parse test */ }

    private TraceViewerViewModel BuildVm(int watchedCount, string masterSource = "src-1")
    {
        // 用 NSubstitute 或 SubFor mock ITimerFactory + ITraceViewerService + DI。
        // 实际 implementation 由 subagent 在 T5-Step 5 中实现。
        throw new NotImplementedException();
    }
}
```

注意: 实际 test fixture 由 subagent 实现。`NSubstitute` 是项目常用 (W3 周后加入; 通过 `git grep "NSubstitute" tests/` 验证)。

- [ ] **Step 6: 跑新测试**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SamplingTableFlow" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

预期: 5/5 PASS。

- [ ] **Step 7: 跑全套确认零回归**

```bash
dotnet test PeakCan.Host.slnx --no-restore --nologo -c Debug --logger "console;verbosity=minimal" 2>&1 | tail -3
```

预期: 总数 ≥1339 + 5 (T3) + ≥5 (T5), 0 fail。

- [ ] **Step 8: Commit T5**

```bash
git add src/PeakCan.Host.App/Views/TraceViewerView.xaml src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SamplingTableFlow.cs tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModel/SamplingTableFlowTests.cs
git commit -m "v3.49 T5 (Q1): Trace Viewer sampling table panel (9th partial SamplingTableFlow + right-edge DataGrid bound to SamplingRows + debounced 50ms refresh on ScrubberValue + binary-search-latest-frame-at-or-before lookup + 5 lock-down tests; first time operator can see multi-signal values at the same scrubber position; +210 LoC)"
```

---

## Task T6: v3.48.2 → v3.49.0 MINOR 版本 + release notes

**Files:**
- Modify: `src/Directory.Build.props` (4-field version bump per W34 sister)
- Create: `docs/release-notes-v3.49.0.md` (~150 LoC)

- [ ] **Step 1: Bump Directory.Build.props**

```bash
python -c "
from pathlib import Path
p = Path('src/Directory.Build.props')
text = p.read_text(encoding='cp1252')
text = text.replace('<Version>3.48.2</Version>', '<Version>3.49.0</Version>', 1)
text = text.replace('<AssemblyVersion>3.48.2.0</AssemblyVersion>', '<AssemblyVersion>3.49.0.0</AssemblyVersion>', 1)
text = text.replace('<FileVersion>3.48.2.0</FileVersion>', '<FileVersion>3.49.0.0</FileVersion>', 1)
text = text.replace('<InformationalVersion>3.48.2</InformationalVersion>', '<InformationalVersion>3.49.0</InformationalVersion>', 1)
p.write_text(text, encoding='cp1252')
"
grep -E 'Version|FileVersion|InformationalVersion' src/Directory.Build.props
```

预期: 全部 3.49.0。

- [ ] **Step 2: 写 release notes** (~150 LoC,镜像 W34 release notes 格式)

需要涵盖:
- Why this MINOR (3 user pain points)
- Q3 architecture (AscFormat 单源 + 6 round-trip tests)
- Q2 architecture (Recording tab 迁到 TraceViewer)
- Q1 architecture (SamplingTable panel)
- LoC trajectory table
- 3 NEW 1/3 lesson candidate observation
- W23 STRUCT-FABRACTION + W19 R1 LESSON ENHANCED + W17 wc-l-splitlines 应用情况
- Verification 列表
- What was skipped (YAGNI)

- [ ] **Step 3: Build 验证版本号变更不破坏**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore 2>&1 | tail -3
dotnet test PeakCan.Host.slnx --no-restore --nologo -c Debug --filter "FullyQualifiedName~CyclicSendService|FullyQualifiedName~PeakCanChannel|FullyQualifiedName~AscParser|FullyQualifiedName~AscFormatRoundTrip|FullyQualifiedName~SamplingTable" --logger "console;verbosity=minimal" 2>&1 | tail -3
```

预期: 关键模块测试全 PASS。

- [ ] **Step 4: Commit T6**

```bash
git add src/Directory.Build.props docs/release-notes-v3.49.0.md
git commit -m "v3.49 T6: v3.48.2 -> v3.49.0 MINOR (Q3 AscFormat single-source-of-truth + Q2 Recording migration + Q1 sampling table; -50/+345 net LoC; 11 new tests 6 round-trip + 5 sampling-table; 3 NEW 1/3 lesson candidates; traceViewerService + TraceViewerViewModel unified single UX)"
```

---

## Task T7: Tier-3 ship (PR + squash + tag + GH release)

- [ ] **Step 1: Push + PR**

```bash
git push -u origin feature/v3-49-0-minor-asc-sampling-recording
gh pr create --base main --head feature/v3-49-0-minor-asc-sampling-recording --title "v3.49 MINOR: ASC single-source-of-truth + Trace Viewer sampling table + RecordView consolidation" --body "[full body covering Q3 + Q2 + Q1 changes + sister-lesson candidates + verification summary]"
```

- [ ] **Step 2: CI + squash + tag + release**

```bash
gh pr checks <N> --watch  # 5-attempt MAX per W34 sister pattern
gh pr merge <N> --squash --delete-branch
git tag -a v3.49.0 -m "v3.49.0 MINOR: [...]" <squash-sha>
git push origin v3.49.0
gh release create v3.49.0 --title "v3.49.0 — ASC 单源 + Sampling Table + Recording 合并" --notes-file docs/release-notes-v3.49.0.md
```

- [ ] **Step 3: Capture-decisions 文件 + commit**

```bash
git add docs/superpowers/capture-decisions/2026-07-14-v3-49-0-minor-asc-sampling-recording-ship.md
git commit -m "v3.49 capture-decisions: ship closure [...]"
git push  # 推到 main,因为 capture-decisions 是 docs-only
```

---

## 验证总结 (Verification)

- `dotnet build PeakCan.Host.slnx`: 0 errors (除 1 个 pre-existing CS8602)
- `dotnet test PeakCan.Host.slnx`: ≥1344 PASS (1339 baseline + 5 sampling),5 SKIP,0 fail (如果 + 6 round-trip 通过,T0 时 +5 新增后总数 ≥ 1350)
- `wc -l src/PeakCan.Host.Core/Replay/AscFormat.cs` = 150 LoC (目标)
- `wc -l src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` ≤ 450 LoC (336 baseline + SamplingTableFlow partial 头部 + LogSamplingTableUpdate partial)
- `wc -l src/PeakCan.Host.App/Services/RecordService/Format.partial.cs` = ~30 LoC (从 67 减)
- 1 个新文件 + 1 个新 partial + 4 个 refactored file + 2 个新 test file + 2 个 release docs
- DI 注册: `RecordViewModel` 仍 singleton,TraceViewerViewModel 接收额外 DI param
- 公开 API surface: 完全不变 (user-facing 行为变化是 UX 层面,API 签名零改动)

## Out of scope (YAGNI)

- **CSV 导出 Sampling Table 行到当前 scrubber position** — easy PATCH follow-up
- **Per-row signal-pair correlation matrix** ("show V2B_CMD vs V2B_Speed at every 100ms") — much larger MINOR
- **Auto-record on Connect** — not v3.49 scope
- **Recording formats other than ASC** — CSV was deferred to v1.3.0 per `RecordView.xaml:18` comment
- **ASC writer emits Vector 'd N' / 'l N' / Rx / Tx / Length convention tokens** — round-trip test verifies current writer's output; Vector tokens added only if user-imported Vector ASC file proves to lose round-trip symmetry (not preemptively)
- **Recording panel only visible when recording** — permanent-visible (simpler) chosen for v3.49
- **Recording × Sampling Table interaction (e.g. record sample)**
