# ASC 解析器 0xFD 数据字节丢失修复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 修复 `AscFormat.TryParseDataLine` 把数据字节 0xFD（token "FD"）误当 CAN-FD flag 吞掉的缺陷，同时保持自家 writer round-trip 不回归。

**Architecture:** 单一解析器数据源（`AscParser.DataLineParserFlow` 已 delegate 到 `AscFormat.TryParseDataLine`），修复落一处即覆盖桌面 Replay 与流式/移动导入。按位置消歧：数据未凑满行内声明 DLC 时 "FD" 是数据字节，凑满后才是 flag。

**Tech Stack:** .NET 10 / xUnit / FluentAssertions（Host.Core.Tests）；设备端 MAUI Android + adb（uiautomator）+ mock LLM（Python SSE TLS）。

**Spec:** [docs/superpowers/specs/2026-09-13-asc-fd-data-byte-parse-fix-design.md](../specs/2026-09-13-asc-fd-data-byte-parse-fix-design.md)

## Global Constraints

- `PeakCan.Host.Mobile.Core` 保持纯 net10.0（本期零改动）；桌面 UI（`src/PeakCan.Host.App`）零改动。
- 测试禁止 `Thread.Sleep` / 真实 `Task.Delay`。
- conventional commits，不带 attribution/Co-Authored-By。
- 设备验收只用 mock LLM（key `sk-mock-ok`），绝不用真实 API key。
- adb 二进制/中文路径安全：传输用 `adb exec-out`，路径加 `MSYS_NO_PATHCONV=1`；adb 全路径 `C:\Users\13777\AppData\Local\Android\Sdk\platform-tools\adb.exe`。
- 用户 SecOc 未提交工作树文件（19 个）原样不动。

---

### Task 1: 文档入库（spec + plan）

**Files:**
- Commit: `docs/superpowers/specs/2026-09-13-asc-fd-data-byte-parse-fix-design.md`、`docs/superpowers/plans/2026-09-13-asc-fd-data-byte-parse-fix.md`

- [x] **Step 1: 提交文档**

```bash
git add docs/superpowers/specs/2026-09-13-asc-fd-data-byte-parse-fix-design.md docs/superpowers/plans/2026-09-13-asc-fd-data-byte-parse-fix.md
git commit -m "docs: add asc 0xfd data-byte parse fix spec and plan"
```

### Task 2: TDD 红——新增 5 个 0xFD 用例

**Files:**
- Modify: `tests/PeakCan.Host.Core.Tests/Replay/AscParserTests.cs`（插入点：`Parse_CanoeFdDlc_LToken_SetsFdFlag` 方法之后，≈L470）

**Interfaces:**
- Consumes: `AscParser.ParseAsync(Stream)` → `IReadOnlyList<ReplayFrame>`（现有 API，零改动）；`MakeAscStream` helper（L14）。
- Produces: 5 个新测试方法名（Task 3 的绿验证按类过滤运行即可，无跨任务符号）。

- [x] **Step 1: 写失败测试**（在 `Parse_CanoeFdDlc_LToken_SetsFdFlag` 方法结束的 `}` 后插入）：

```csharp
    /// <summary>
    /// 0xFD data-byte fix: per-byte dialects (CANoe 'd N', PCAN bare-DLC) write a
    /// 0xFD byte as the standalone token "FD", which the data loop swallowed as a
    /// CAN-FD flag. While collected data is short of the declared DLC, "FD" must
    /// parse as a data byte.
    /// </summary>
    [Fact]
    public async Task Parse_PerByteData_FirstByteFd_IsDataByteNotFlag()
    {
        const string asc = @"date Wed Jul 1 08:32:01 2026
base hex  timestamps absolute
internal events logged
155564.432800 1  100  d 8  FD BB CC DD EE FF 00 11
";
        using var stream = MakeAscStream(asc);
        var frames = await AscParser.ParseAsync(stream);

        frames.Should().HaveCount(1);
        frames[0].Dlc.Should().Be(8);
        frames[0].Data.Should().Equal(0xFD, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x11);
        frames[0].Flags.HasFlag(FrameFlags.Fd).Should().BeFalse("data-byte 0xFD must not set the CAN-FD flag");
    }

    [Fact]
    public async Task Parse_PerByteData_MidByteFd_IsDataByteNotFlag()
    {
        const string asc = @"date Wed Jul 1 08:32:01 2026
base hex  timestamps absolute
internal events logged
155564.432800 1  100  d 8  AA FD CC DD EE FF 00 11
";
        using var stream = MakeAscStream(asc);
        var frames = await AscParser.ParseAsync(stream);

        frames.Should().HaveCount(1);
        frames[0].Data.Should().Equal(0xAA, 0xFD, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x11);
        frames[0].Flags.HasFlag(FrameFlags.Fd).Should().BeFalse();
    }

    [Fact]
    public async Task Parse_PerByteData_LastByteFd_IsDataByteNotFlag()
    {
        const string asc = @"date Wed Jul 1 08:32:01 2026
base hex  timestamps absolute
internal events logged
155564.432800 1  100  d 8  AA BB CC DD EE FF 00 FD
";
        using var stream = MakeAscStream(asc);
        var frames = await AscParser.ParseAsync(stream);

        frames.Should().HaveCount(1);
        frames[0].Data.Should().Equal(0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0xFD);
        frames[0].Flags.HasFlag(FrameFlags.Fd).Should().BeFalse();
    }

    /// <summary>
    /// Guard for the >= branch: once the declared DLC is exhausted, a trailing
    /// "fd" token is the CAN-FD flag (classic 'd' marker — flag must come ONLY
    /// from the trailing token, hence not 'l').
    /// </summary>
    [Fact]
    public async Task Parse_PerByteData_FdTokenAfterFullData_IsFdFlag()
    {
        const string asc = @"date Wed Jul 1 08:32:01 2026
base hex  timestamps absolute
internal events logged
155564.432800 1  100  d 8  AA BB CC DD EE FF 00 11  fd
";
        using var stream = MakeAscStream(asc);
        var frames = await AscParser.ParseAsync(stream);

        frames.Should().HaveCount(1);
        frames[0].Dlc.Should().Be(8);
        frames[0].Data.Should().Equal(0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x11);
        frames[0].Flags.HasFlag(FrameFlags.Fd).Should().BeTrue("'fd' after data is exhausted is the CAN-FD flag");
    }

    /// <summary>
    /// PCAN bare-DLC shape (P7 acceptance fixture): no 'd/l' marker, DLC at
    /// tokens[3], first data byte 0xFD — the exact shape that lost its first
    /// byte on device.
    /// </summary>
    [Fact]
    public async Task Parse_PcanBareDlc_FirstByteFd_IsDataByteNotFlag()
    {
        const string asc = @"date Wed Jul 1 08:32:01 2026
base hex  timestamps absolute
internal events logged
0.000000 51  100  8  FD 00 02 03 04 05 06 07
";
        using var stream = MakeAscStream(asc);
        var frames = await AscParser.ParseAsync(stream);

        frames.Should().HaveCount(1);
        frames[0].Dlc.Should().Be(8);
        frames[0].Data.Should().Equal(0xFD, 0x00, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07);
        frames[0].Flags.HasFlag(FrameFlags.Fd).Should().BeFalse();
    }
```

- [x] **Step 2: 运行确认红**

Run: `dotnet test tests/PeakCan.Host.Core.Tests --filter "FullyQualifiedName~AscParserTests" --nologo -v minimal`
Expected: 4 个新用例 FAILED（首/中/尾/PCAN——0xFD 被吞）；Guard 用例 `FdTokenAfterFullData` 修复前即 PASS（它守护"修复不得矫枉过正"的 ≥ 分支）；既有用例全绿。

### Task 3: TDD 绿——位置消歧修复 + fix commit

**Files:**
- Modify: `src/PeakCan.Host.Core/Replay/AscFormat.cs:184-185`（数据循环 `case "fd"`）

- [x] **Step 1: 最小实现**——把

```csharp
                case "fd":
                    flags |= FrameFlags.Fd; continue;
```

改为：

```csharp
                case "fd":
                    // 位置消歧：数据未凑满行内声明 DLC 时，"FD" 是数据字节 0xFD
                    // （逐字节方言 PCAN/CANoe 的合法数据 token）；凑满后才是
                    // CAN-FD flag（自家 writer 尾部标记）。
                    if (data.Count < dlc) break;
                    flags |= FrameFlags.Fd; continue;
```

（switch 内 `break` 只跳出 switch，落到既有 hex 解析路径。）

- [x] **Step 2: 运行确认绿**

Run: `dotnet test tests/PeakCan.Host.Core.Tests --filter "FullyQualifiedName~AscParserTests" --nologo -v minimal`
Expected: 全部 PASS（含既有 round-trip / l-token / concatenated-hex 用例零回归）。

- [x] **Step 3: 单 fix commit（测试+修复同行）**

```bash
git add src/PeakCan.Host.Core/Replay/AscFormat.cs tests/PeakCan.Host.Core.Tests/Replay/AscParserTests.cs
git commit -m "fix(core): preserve 0xFD data bytes in ASC data-line parsing"
```

### Task 4: 全量回归

- [x] **Step 1: 三套测试**

```bash
dotnet test tests/PeakCan.Host.Core.Tests --nologo -v minimal
dotnet test tests/PeakCan.Host.Infrastructure.Tests --nologo -v minimal
dotnet test tests/PeakCan.Host.Mobile.Core.Tests --nologo -v minimal
```

Expected: 全绿（基线 1123 / Infrastructure 全量 / 275）。记录实际数字供 finalize。

- [x] **Step 2: 约束自查**

`git diff main --stat` 确认变更仅 `AscFormat.cs`、`AscParserTests.cs` 与 docs；`src/PeakCan.Host.App`、`PeakCan.Host.Mobile*` 零改动。

### Task 5: 真机回归（0xFD fixture 复跑验收）

**Files:**
- Create: `D:\claude_proj2\.tmp\gen_p7demofd.py`、输出 `D:\claude_proj2\.tmp\p7demofd.asc`

- [x] **Step 1: 生成 fixture + 理论统计**

```python
#!/usr/bin/env python3
"""p7demofd.asc: 2000 frames @5ms (0..9.995s), id 0x100, EngineSpeed raw ramp 0..3997
(factor 0.25 -> 0..999.25 rpm). byte0-1 = raw LE（raw%256==253 时 byte0=0xFD），
byte7=0xFD canary。打印 search_signal_trace t=[0,10) 的理论统计。"""
raws = [round(i * 3997 / 1999) for i in range(2000)]
lines = ["date Wed Sep 13 10:00:00.000 2026",
         "base hex  timestamps absolute",
         "no internal events logged"]
t = 0.0
for r in raws:
    b = [r & 0xFF, (r >> 8) & 0xFF, 0x02, 0x03, 0x04, 0x05, 0x06, 0xFD]
    lines.append(f"{t:.6f} 51  100  8  " + " ".join(f"{x:02X}" for x in b))
    t += 0.005
open(r"D:\claude_proj2\.tmp\p7demofd.asc", "w").write("\n".join(lines) + "\n")
rpm = [r * 0.25 for r in raws]
print("frames", len(raws), "first", rpm[0], "last", round(rpm[-1], 4),
      "min", min(rpm), "max", round(max(rpm), 4), "mean", round(sum(rpm) / len(rpm), 4))
```

Run: `python /d/claude_proj2/.tmp/gen_p7demofd.py` → 记录理论值（期望 first=0.0、last/max=999.25、mean≈499.625）。核对 `.tmp/p7demo.dbc` 含 `BO_ 256 EngineData` + `SG_ EngineSpeed : 0|16@1+ (0.25,0)`（P7 遗留 fixture，直接复用）。

- [x] **Step 2: 推送 + 部署**

```bash
MSYS_NO_PATHCONV=1 /c/Users/13777/AppData/Local/Android/Sdk/platform-tools/adb.exe push D:/claude_proj2/.tmp/p7demofd.asc D:/claude_proj2/.tmp/p7demo.dbc /sdcard/Download/
dotnet build src/PeakCan.Host.Mobile -f net10.0-android -c Debug -t:Run   # 重编译（含修复后的 Host.Core）并安装启动
python D:/claude_proj2/.tmp/mock_llm.py 18443 --tls D:/claude_proj2/.tmp/srv2_cert.pem D:/claude_proj2/.tmp/srv2_key.pem   # 后台
```

- [x] **Step 3: 设备验收（uiautomator 流程，同 P7）**

导入 `p7demofd.asc` + `p7demo.dbc` → 进入回放/聊天 → 输入 `searchtrace` 发送（mock 触发 `search_signal_trace`，窗口 0–10s）→ 截图比对：
- `max` ≈ 理论值、`last` ≈ 理论值、`sample_count`=200、无 0xFD 吞字节迹象（帧 8 字节）；
- Browse 打开该 trace：行显示 DLC 8、数据完整（含 0xFD）。

- [x] **Step 4: 结果记录**——截图存 `.tmp/ascfd_chat1.png` 等；设备不在位或环境故障时，将实际执行到的步骤与阻塞原因记入本计划状态块（spec §4 允许降级为单测 + 理论对拍收尾）。

### Task 6: finalize

- [x] **Step 1: 勾选本计划全部 checkbox，重写顶部「状态」块**（新增到标题下方）：分支、commits（fix + finalize）、三套测试实际数字、真机验收结果（或降级说明）、与 spec 的偏差（预期：无；若有记录原因）。
- [x] **Step 2: finalize commit**

```bash
git add docs/superpowers/plans/2026-09-13-asc-fd-data-byte-parse-fix.md
git commit -m "docs: finalize asc fd data-byte fix plan"
```

- [x] **Step 3: 收尾汇报**——合并 main / push 由用户决定（同 P7 惯例，不主动执行）。
