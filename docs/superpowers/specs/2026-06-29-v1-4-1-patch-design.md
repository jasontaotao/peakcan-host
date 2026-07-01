# v1.4.1 PATCH — SecurityAccessAsync race + AscParser logging + DbcSendViewModel DbcLoaded Design Spec

**Date:** 2026-06-29
**Branch:** `feature/v1-4-1-patch` (cut from main @ v1.4.0 `4bec174`)
**Target version:** v1.4.1 (PATCH)
**Spec owner:** main agent
**Reviewer:** code-reviewer subagent (pre-ship)
**Pre-flight:** Phase 2.5 actual code exploration (UdsClient, AscParser, DbcSendViewModel)

## 起源

v1.4.0 MINOR release notes §"Known follow-ups" 标出 3 项 v1.4.1 PATCH carry-over（v1.3.1 PATCH pre-ship review 起源）：

| Item | Source | Severity | 类型 |
|------|--------|----------|------|
| 1. Atomic concurrent mid-handshake race test for `SecurityAccessAsync` | v1.3.1 PATCH pre-ship review + v1.4.0 MINOR §Non-Goals | LOW | test-only |
| 2. AscParser log skipped malformed lines | v1.4.0 MINOR §Minor findings (Task 7 review) | LOW | src + test |
| 3. DbcSendViewModel subscribe `DbcService.DbcLoaded` event | v1.4.0 MINOR §Minor findings (Task 7 review) | LOW | src + test |

全部为 LOW severity。1 项 test-only，2 项小 src 改动。预估 ~150–250 行 src + ~120 行 test。

### Phase 2.5 actual code exploration findings

实际读 v1.4.0 shipped 代码（SHA `4bec174`）确认 brief 描述准确 + 锁定关键 design points：

| Assumption | Phase 2.5 actual |
|---|---|
| **Item 1**: 2-arg `SecurityAccessAsync` overload 存在 TOCTOU window | **确认**。`UdsClient.cs:381–408` 2-arg overload 在 T0 (line 394 `IsLocked` pre-check) 与 T5 (line 284 `IsLocked` re-check inside 3-arg SendKey leg) 之间没有 per-handshake 锁，只有 per-request `SemaphoreSlim _requestLock` (line 19)。XML doc lines 369–379 显式记录这是 intentional behavior。 |
| **Item 1**: 现有测试 gap | **确认**。`UdsClientTests.cs:348–607` 和 `UdsClientSecurityAccessOverloadTests.cs:28–156` 全部为单线程；唯一并发测试是 `IsoTpLayerTests.cs:290–422` (watchdog / timer race, NOT SecurityAccess)。 |
| **Item 1**: 测试 seam 完备 | 现有 `IsoTpLayer` sink + `FakeKeyDerivationAlgorithm` + `PublicOnMessageReceivedForTesting` (line 695) 足够。Zero src change 即可写新测试。 |
| **Item 2**: AscParser 已有 `ILogger<T>` ctor param（Reuse `ReplayService` pattern） | **Refuted**。`AscParser.cs:11` 是 `public static class`，无 ctor，无 logger，无 partial。`malformedCount` 是方法局部变量（line 50），不是 class field。 |
| **Item 2**: `Parse_MalformedLines_LogsAndSkips` 测试已断言 log | **Refuted**。`AscParserTests.cs:118` 只断言 `frames.Should().HaveCount(3)`，从未 inspect logger。Test 名字 misnomer。 |
| **Item 2**: Core.Tests 已有 NSubstitute / Logging.Abstractions 引用 | **Refuted**。`PeakCan.Host.Core.Tests.csproj:25–31` 只有 coverlet / FluentAssertions / Test.Sdk / xunit。需要加 PackageReference。 |
| **Item 3**: `DbcService.DbcLoaded` event 存在 | **确认**。`DbcService.cs:53`: `public event Action<DbcDocument>? DbcLoaded;` |
| **Item 3**: `DbcSendViewModel` 当前不订阅 event | **确认**。`DbcSendViewModel.cs:48–57` ctor 只读 `_dbcService.Current?.Messages` 一次。 |
| **Item 3**: 测试用 reflection raise event | **确认 precedent**。`DbcViewModelTests.cs:70–71` 用 `EventRaiseExtensions.RaiseMethod` 反射触发 `DbcLoaded`。可复用。 |
| **Item 3**: DbcLoaded 在 worker thread 触发 | **确认**。`DbcService.cs:17–22` class doc 写明 + `LoadAsync` 在 `Task.Run` 后 invoke (line 104)。订阅者必须 marshal 到 UI thread。 |

## Scope

3 PATCH items = 1 test-only + 2 small src/test。全部 LOW severity。MINOR 纪律保持（additive only）。

| # | Item | 组件 | 工作 | 来源 | Severity |
|---|------|------|------|------|----------|
| 1 | **SecurityAccessAsync race test** | Core.Tests: `UdsClientConcurrentSecurityAccessTests.cs` (new file) | Concurrent `SecurityAccessAsync` calls on same level — assert non-interleaved wire emission + lockout semantic | v1.3.1 PATCH review carry-over | LOW |
| 2 | **AscParser log skipped lines** | Core: `AscParser.cs` (static → static partial + optional ILogger param + [LoggerMessage] helper) + Core.Tests: csproj + 2 tests | Log Warning with 1-based line number + raw line + reason; convert foreach → for counter | v1.4.0 Task 7 review | LOW |
| 3 | **DbcSendViewModel DbcLoaded subscription** | App: `DbcSendViewModel.cs` ctor + `OnLoaded` handler wrapped in `RunOnUi()` | Re-populate `DbcMessages` on late DBC load; reset `SelectedDbcMessage` to null | v1.4.0 Task 7 review | LOW |

## Non-Goals

- **v1.5.0 MINOR**: V8 sandbox hardening + CanApi rate limit + DBC size/token limit + path normalization + OEM `IKeyDerivationAlgorithm` concrete + Channel picker + Replay loop/CAN ID filter + Periodic DBC send — **deferred** (v1.4.0 spec §Non-Goals 第 53 行原文）。
- **Replay scope**: loop playback, CAN ID filter, time-range filter — **deferred**。
- **Periodic DBC send** — **deferred**。
- **Multiplexed signal groups UI** (auto-show only valid mux signals) — **out of scope**。
- **Value table encoding** (text → int via `DbcDocument.ValueTables`) — **out of scope**。
- **Additional SecurityAccessAsync hardening** beyond Item 1 race test (e.g. per-handshake mutex) — **out of scope**。Item 1 是 test-only，证明 race is observable but the existing TOCTOU window is intentional per v1.3.0 spec. 任何新增锁会改变 public observable behavior，需走 MINOR 流程。
- **AscParser IL sink / counter expose**（让 caller 查询 malformed count）— **out of scope**。当前 spec 已 throw `ReplayFormatException` 含 `{malformedCount}/{dataLineCount}` message（line 80）。Log 只为 production debuggability，counter 已是 `ReplayFormatException.Message` 的一部分。
- **DbcSendViewModel IDisposable** — **out of scope**（采用 `DbcViewModel` precedent，不 dispose；两者都是 app-lifetime DI singleton，一起 die at process exit）。
- **DbcService API change** — 不变；`DbcLoaded` event 已存在。
- 公开 API 移除 / 依赖升级 / schema migration — MINOR 纪律。
- 新增 NuGet 包（NSubstitute 加到 Core.Tests.csproj；Logging.Abstractions 已在 Directory.Packages.props 10.0.9）。
- 修改 `UdsSecurity` / `DbcService` / `UdsClient` / `SignalDecoder` / `DbcEncodeService` 接口签名。

## 设计决策

### Decision 1: Item 1 — race test 用 `Task.WhenAll` + 2 concurrent calls（同 level）

**选项 A** (推荐): 2 个 task `Task.WhenAll(SecurityAccessAsync(level), SecurityAccessAsync(level))` — 第一个到达 `SendKey` 阶段时第二个还在 `RequestSeed` 阶段（slow first frame response）；触发 lockout 边界 (AttemptCount=3)。

**选项 B**: 1 caller + 1 thread 在 RequestSeed 期间手动调 `RecordFailedAttempt` 模拟外因 lockout。优点：更确定性。缺点：测的是 lockout-throw 路径而非真实 race。

**决策**: A。理由：测的是真实 TOCTOU 行为（test seam 已有 `IsoTpLayer` sink capture `sent`，可断言 wire emission non-interleaved）；B 是 unit-style 测，已隐含在 Item 1 现有 `TwoArg_Overload_PreChecks_Lockout_Before_RequestSeed` (UdsClientSecurityAccessOverloadTests.cs:104–124) 中。

### Decision 2: Item 1 — race test flakiness mitigation

**选项 A** (推荐): Deadline pattern `Task.WhenAny(tasks, Task.Delay(5000))` + 5s timeout fail。fail message 含 dump。

**选项 B**: 无 deadline；CI 可能 hang。

**决策**: A。理由：memory v1.2.12 lesson 4 — `CyclicSendServiceRaceTests` 有 known transient flakes。Deadline 让 flake 信号化（fail 而非 hang）。

### Decision 3: Item 2 — AscParser logger parameter signature

**选项 A** (推荐): `public static async Task<IReadOnlyList<ReplayFrame>> ParseAsync(Stream stream, ILogger<AscParser>? logger = null, CancellationToken ct = default)` + `private static readonly ILogger _logger = NullLogger<AscParser>.Instance;` 在 static field + assign at entry: `_logger = logger ?? NullLogger<AscParser>.Instance;`

**选项 B**: 全局 `AsyncLocal<ILogger>` per-call context。

**选项 C**: 把 AscParser 改成 instance class + DI 注入 ILogger。

**决策**: A。理由：
- B 是 speculative generality；AsyncLocal 仅当真的多线程同实例不同 logger 才需要。
- C 是大改：现有 4 个 caller (ReplayService.LoadAsync, tests) 全部要换；违反 PATCH 纪律。
- A 是 minimal change：1 个 ctor 字段 + 1 个 optional 参数 + 1 行 fallback。

### Decision 4: Item 2 — line number = 1-based stream line 还是 1-based data line

**选项 A** (推荐): 1-based stream line (`for (int i = 0; i < lines.Count; i++)` 用 `i + 1`)。用户可在 editor `goto-line N` 直接定位。

**选项 B**: 1-based data line（仅算 non-header/non-comment/non-empty 行）。更紧凑但用户难以反推文件位置。

**决策**: A。理由：production debuggability 是 carry-over 的核心目的；operator 应该能 `texteditor ASC.asc +N` 直接跳到坏行。

### Decision 5: Item 2 — 测试断言粒度

**选项 A** (推荐): 2 tests：
- `Parse_MalformedLines_LogsEachWithLineNumberAndReason` — 3 valid + 2 malformed → 断言 `logger.Received(2).Log(Warning, …, 包含 line number + raw line + reason)`
- `Parse_HighMalformedRatio_ThrowsAfterLoggingAll` — >50% malformed → throw 前已 log 全部 malformed lines

**选项 B**: 1 test only + happy path don't-log 守卫。

**决策**: A。理由：覆盖 happy path log + throw path log 两个语义独立路径。

### Decision 6: Item 3 — `IDisposable` 还是 match `DbcViewModel` precedent

**选项 A** (推荐): Match `DbcViewModel` precedent (DbcViewModel.cs:97-98 subscribe + 不 unsubscribe)。两者都是 app-lifetime DI singleton (`AppHostBuilder.cs:158,190`)。

**选项 B**: 加 `IDisposable`，在 `Dispose()` 中 unsubscribe。Defensive，与 `SendViewModel` + `ReplayViewModel` 对齐。

**决策**: A。理由：
- memory v1.2.11 lesson "VMs that own timers/observers should be IDisposable" 适用于 own **timer/observable** (high-frequency event) 的 VM。`DbcService.DbcLoaded` 是 low-frequency event (一次 per 文件加载)。
- `DbcViewModel` precedent 已存在：subscribe + 不 unsubscribe + 同 app-lifetime singleton 论证（"GC + finalizer pass handles cleanup"）。
- 一致性 > 抽象防御性。Pattern 锁定为"DbcService 单例 → subscribe 不 unsubscribe"。

### Decision 7: Item 3 — DbcLoaded handler body

**选项 A** (推荐): Wrap 整个 body in `DispatcherExtensions.RunOnUi()` (mirror `DbcViewModel.OnLoaded` lines 112–147)。Body:
1. `SelectedDbcMessage = null` (触发 `OnSelectedDbcMessageChanged(null)` 隐式 clear `SignalRows`)
2. `DbcMessages.Clear()`
3. repopulate `DbcMessages` from `doc.Messages`
4. `ErrorMessage = null` (parity with `SendAsync` ctor pattern)

**选项 B**: 全 body 同步跑，不 marshal。期望 ObservableCollection.Add 不抛。

**决策**: A。理由：`DbcService.LoadAsync` 在 `Task.Run` worker thread 触发 `DbcLoaded` (line 104)，跨线程改 `ObservableCollection` 会 throw `NotSupportedException`。已有 `DbcViewModel` 的 RunOnUi 包装是 reference pattern。

## 架构

### Item 1 — Race test architecture (no src change)

```
Test
  ↓
  new UdsClient(iso, FakeKeyDerivationAlgorithm, new UdsSecurity(Default))
  ↓
  var t1 = client.SecurityAccessAsync(level=0x01, ct)  // start handshake
  var t2 = client.SecurityAccessAsync(level=0x01, ct)  // concurrent, same level
  ↓
  await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(5))
  ↓
  Assertion 1: t1 和 t2 中**恰好一个** throws UdsSecurityLockedException OR 两者都 succeed
              (取决于 race timing — non-deterministic, but observable)
  Assertion 2: sent collection has non-interleaved wire emission
              (期望 [RequestSeed-frame1, RequestSeed-frame2] then [SendKey-frame2, SendKey-frame1])
              OR [RequestSeed-frame1, SendKey-frame1, RequestSeed-frame2, SendKey-frame2-throws]
  Assertion 3: client.Security.IsLocked(level) is consistent with AttemptCount
```

### Item 2 — AscParser logging architecture

```
AscParser.ParseAsync(stream, logger: ILogger<AscParser>?, ct)
  ↓
  _logger = logger ?? NullLogger<AscParser>.Instance   // assign at entry
  ↓
  // existing read-all-lines path (lines 26–34)
  ↓
  ParseLines(lines)
    ↓
    for (int i = 0; i < lines.Count; i++)
      var raw = lines[i];
      var line = raw.Trim();
      // existing skip paths: empty / // / date / base / internal events
      ↓
      dataLineCount++
      ↓
      if (TryParseDataLine(line, out var frame, out var reason))
        frames.Add(frame);
      else
        LogSkippedLine(_logger, lineNumber: i + 1, rawLine: lines[i], reason: reason)
        malformedCount++;
    ↓
  // existing exception path: throw ReplayFormatException on empty / >50%

[LoggerMessage(Level = LogLevel.Warning,
               Message = "Skipped malformed ASC line {LineNumber}: {RawLine} ({Reason})")]
private static partial void LogSkippedLine(ILogger logger, int lineNumber, string rawLine, string reason);

[LoggerMessage] requires partial class → static class → static partial class
```

### Item 3 — DbcSendViewModel subscription architecture

```
AppHostBuilder 构造时:
  builder.Services.AddSingleton<DbcService>();
  builder.Services.AddSingleton<DbcSendViewModel>();   // existing

Runtime:
  User opens SendView BEFORE loading DBC
    ↓
    DbcSendViewModel ctor
      _dbcService = DbcService (singleton, Current=null at this point)
      DbcMessages = [] (empty)
    ↓
  User loads DBC via DbcViewModel.LoadFileAsync
    ↓
    DbcService.LoadAsync(path)
      parse OK → Current = doc → DbcLoaded.Invoke(doc)  (on Task.Run worker thread)
    ↓
    DbcSendViewModel.OnLoaded(doc)   ← new handler, subscribed in ctor
      ((Action)(() => {
        SelectedDbcMessage = null;  // triggers OnSelectedDbcMessageChanged(null) → SignalRows.Clear()
        DbcMessages.Clear();
        foreach (var msg in doc.Messages) DbcMessages.Add(msg);
        ErrorMessage = null;
      })).RunOnUi();  // marshal to UI thread (DispatcherExtensions.cs:49–99)

No IDisposable; both VMs die at process exit (DbcViewModel precedent).
```

## 组件

### Core 修改

| File | Change |
|------|--------|
| `src/PeakCan.Host.Core/Replay/AscParser.cs` | `static` → `static partial`; add `private static ILogger _logger = NullLogger<AscParser>.Instance;`; add `[LoggerMessage]` `LogSkippedLine`; change `ParseAsync` signature to `ParseAsync(Stream, ILogger<AscParser>? = null, CancellationToken)`; assign `_logger = logger ?? NullLogger<AscParser>.Instance` at entry; convert `foreach` (line 53) to `for (int i = 0; i < lines.Count; i++)`; call `LogSkippedLine(_logger, i + 1, lines[i], reason)` before `malformedCount++` at line 69; extend `TryParseDataLine` signature to `TryParseDataLine(string line, out ReplayFrame frame, out string reason)` (6 return-false sites each set `reason = "..."`). |

### Core.Tests 修改

| File | Change |
|------|--------|
| `tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj` | Add `<PackageReference Include="NSubstitute" />` + `<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />` (versions from Directory.Packages.props:10,19 — 5.3.0 / 10.0.9). |
| `tests/PeakCan.Host.Core.Tests/Uds/UdsClientConcurrentSecurityAccessTests.cs` (new) | `~80 lines`. 1-2 race tests using `IsoTpLayer` sink + `FakeKeyDerivationAlgorithm` + `PublicOnMessageReceivedForTesting`. Deadline pattern (5s timeout). |
| `tests/PeakCan.Host.Core.Tests/Replay/AscParserTests.cs` | +2 tests: `Parse_MalformedLines_LogsEachWithLineNumberAndReason` + `Parse_HighMalformedRatio_ThrowsAfterLoggingAll`. |

### App 修改

| File | Change |
|------|--------|
| `src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs` | ctor subscribe: `_dbcService.DbcLoaded += OnLoaded;`; add `private void OnLoaded(DbcDocument doc)` wrapping body in `DispatcherExtensions.RunOnUi()`; body: `SelectedDbcMessage = null; DbcMessages.Clear(); foreach (var msg in doc.Messages) DbcMessages.Add(msg); ErrorMessage = null;`. |

### App.Tests 修改

| File | Change |
|------|--------|
| `tests/PeakCan.Host.App.Tests/ViewModels/DbcSendViewModelTests.cs` | +1 test: `DbcSendViewModel_OnDbcLoaded_RepopulatesDbcMessagesAndClearsSignalRows` using `SetCurrentForTests` + `EventRaiseExtensions.RaiseMethod` (per DbcViewModelTests.cs:70-71 precedent). |

### DI 注册（修改 `AppHostBuilder`）

无变化。`DbcService` + `DbcSendViewModel` 已是 singleton，subscription 在 ctor 自动 wire。

### Documentation 修改

| File | Change |
|------|--------|
| `docs/release-notes-v1.4.1.md` | new |
| `Directory.Build.props` | version bump 1.4.0 → 1.4.1 |

## 数据流

### Item 1 happy path (race resolves cleanly)

1. Test 构造 `UdsClient` with `IsoTpLayer` (sink captures `sent`) + `FakeKeyDerivationAlgorithm` + `UdsSecurity(Default)`
2. Test launches `Task.WhenAll(t1, t2)` where t1 = t2 = `SecurityAccessAsync(level=0x01)`
3. Both tasks enter 2-arg overload; both pre-check `IsLocked(0x01)` (line 394, currently false)
4. Both t1 and t2 reach `RequestSeed` leg (line 398); `_requestLock` serializes wire emission so first RequestSeed sends first, second waits
5. Test injects response frames via `PublicOnMessageReceivedForTesting` in deterministic order (ResponseSeed for both)
6. Both tasks reach `SendKey` leg (line 407); `_requestLock` serializes; first SendKey succeeds → `Security.SetAuthenticated(0x01)` → `RecordFailedAttempt` clear → t1 succeeds
7. Second SendKey also passes lockout pre-check; t2 succeeds (or hits edge case where t1's SetAuthenticated triggers something)
8. Test asserts: `sent` has 4 frames (2 RequestSeed + 2 SendKey) in non-interleaved order (request seeds first, then send keys, no mid-handshake interleaving)
9. Test asserts: t1.Result and t2.Result both equal expected seed bytes

### Item 1 race edge case (lockout flips mid-handshake)

1. Same setup as above but `UdsSecurity.LockoutConfig` set to AttemptCount=2 (boundary)
2. Test pre-sets `AttemptCount=1` via `RecordFailedAttempt` to put level at edge
3. t1 enters RequestSeed leg → gets seed
4. t2 enters RequestSeed leg → waits on `_requestLock`
5. Test injects bad response for t1's RequestSeed (NRC 0x35) → t1 path catches (per v1.3.1 PATCH Item 1 logic) → t1 calls `RecordFailedAttempt` → AttemptCount=2 → not yet locked
6. t2's RequestSeed finally executes; t2 awaits response
7. Test injects bad response for t2's RequestSeed (NRC 0x35) → t2 `RecordFailedAttempt` → AttemptCount=3 → locked!
8. t2's SendKey leg pre-check (line 284) → `IsLocked` true → throws `UdsSecurityLockedException`
9. Test asserts: t1 throws specific exception OR t2 throws `UdsSecurityLockedException` (one of the two), and lockout state consistent

### Item 2 happy path (log skipped lines)

1. Test 构造 ASC stream with 3 valid + 2 malformed lines (well under 50% threshold)
2. `logger = Substitute.For<ILogger<AscParser>>(); logger.IsEnabled(Warning).Returns(true);` (per TraceServiceTests.cs:249 precedent)
3. `var frames = await AscParser.ParseAsync(stream, logger)`
4. Assert `frames.Should().HaveCount(3)`
5. Assert `logger.Received(2).Log(Warning, …, contains line number "5" and "6", contains raw line content, contains reason)`
6. **Verify IsEnabled-gate**: if test forgot `IsEnabled(Warning).Returns(true)`, `[LoggerMessage]` source-gen may skip the call → test fails → developer reminded

### Item 2 throw path (log then throw)

1. Test 构造 ASC stream with 1 valid + 4 malformed (>50% threshold = 80% malformed)
2. `logger = Substitute.For<ILogger<AscParser>>(); logger.IsEnabled(Warning).Returns(true);`
3. `var act = async () => await AscParser.ParseAsync(stream, logger);`
4. Assert `act.Should().ThrowAsync<ReplayFormatException>().WithMessage(contain "4/5 = 80%")`
5. Assert `logger.Received(4).Log(Warning, …)` — all 4 malformed lines logged BEFORE throw

### Item 3 happy path (DBC loaded after VM constructed)

1. Test: `var svc = new DbcService(NullLogger<DbcService>.Instance);`
2. `var vm = new DbcSendViewModel(encoder, sendService, svc);` — ctor sees `Current=null`, `DbcMessages=[]`
3. `svc.SetCurrentForTests(testDoc);` — installs DBC
4. **Get `DbcLoaded` event via reflection** (per DbcViewModelTests.cs:70-71 pattern):
   `svc.GetType().GetEvent(nameof(DbcService.DbcLoaded))!.RaiseMethod(svc, testDoc);`
5. `vm.DbcMessages.Should().HaveCount(testDoc.Messages.Count)`
6. `vm.SelectedDbcMessage.Should().BeNull()`
7. `vm.SignalRows.Should().BeEmpty()` (cleared by OnSelectedDbcMessageChanged(null))

## 错误处理

| Error | Source | Handling |
|-------|--------|----------|
| Race test deadlock | Concurrent SecurityAccessAsync | 5s `Task.WhenAny` deadline → fail with sent-collection dump |
| Race test flake | Timing-sensitive interleaving | Re-run 3x in CI; if 1 of 3 fails → mark test as transient-flaky (memory v1.2.12 lesson 4) |
| AscParser logger not configured | Production caller doesn't pass logger | `NullLogger<AscParser>.Instance` fallback; no crash |
| AscParser `ILogger.IsEnabled(Warning)` returns false | Caller logger config | `[LoggerMessage]` source-gen skips call → no log → test asserts fail (developer reminder) |
| AscParser csproj missing NSubstitute / Logging.Abstractions | Build error | csproj edit required at impl time; spec §components lists this explicitly |
| DbcService.DbcLoaded raised without subscribers | Empty handler chain | `_dbcService.DbcLoaded?.Invoke(doc)` null-conditional safe |
| DbcSendViewModel handler runs on worker thread without RunOnUi | Cross-thread ObservableCollection.Add | `NotSupportedException`; RunOnUi wrap is required (per DbcViewModel.cs:125–146 precedent) |
| DbcService LoadAsync throws | Parse error | `DbcService.LoadFailed` event fires (separate from `DbcLoaded`); DbcSendViewModel not affected |

**No silent failures**. All exceptions are surfaced; no swallowed errors.

## 测试策略

### Item 1 tests (~2 tests in Core.Tests)

| Test | Scenario |
|------|----------|
| `SecurityAccessAsync_TwoConcurrent_SameLevel_ProducesNonInterleavedWireEmission` | Both succeed; assert `sent` has [Seed1, Seed2, Key1, Key2] non-interleaved |
| `SecurityAccessAsync_ConcurrentMidHandshakeLockout_ThrowsConsistentlyWithState` | Lockout flips during race; assert exactly one throws OR both succeed consistently |

### Item 2 tests (~2 tests in Core.Tests)

| Test | Scenario |
|------|----------|
| `Parse_MalformedLines_LogsEachWithLineNumberAndReason` | 3 valid + 2 malformed; assert 2 log calls with line numbers + raw lines + reasons |
| `Parse_HighMalformedRatio_ThrowsAfterLoggingAll` | 1 valid + 4 malformed (>50%); assert 4 logs before throw |

### Item 3 tests (~1 test in App.Tests)

| Test | Scenario |
|------|----------|
| `DbcSendViewModel_OnDbcLoaded_RepopulatesDbcMessagesAndClearsSignalRows` | VM ctor with `Current=null`; raise DbcLoaded via reflection; assert DbcMessages populated + SelectedDbcMessage null + SignalRows empty |

### Test count expectations

| Suite | v1.4.0 baseline | **v1.4.1 final** | Δ |
|-------|-----------------|------------------|---|
| Core.Tests | 296 | **~300** | +4 (Item 1 ×2 + Item 2 ×2) |
| Infra.Tests | 84 + 2 SKIP | **84 + 2 SKIP** | unchanged |
| App.Tests | 356 + 4 SKIP | **~357 + 4 SKIP** | +1 (Item 3 ×1) |
| **Total** | 736 pass + 6 SKIP + 0 fail | **~741 pass + 6 SKIP + 0 fail** | **+5 net** |

### TDD discipline guard (per peakcan history)

Plan Task 1 应明示:
- **Item 1 race test**: Brief作者必须用真实 `IsoTpLayer` sink 而非 mock。Mocking 隐藏 race 实际形态（per memory v1.4.0 lesson: "end-to-end producer/consumer tests catch gaps that unit tests miss"）。
- **Item 1 race test**: Verify RED test 真的 FAIL against unfixed code (race test 是新加的，RED 应该 fail by 不存在)。After impl (= test file add), verify GREEN. After stabilization (= 1 re-run if transient), verify GREEN.
- **Item 2 log test**: 必须用 NSubstitute `Substitute.For<ILogger<AscParser>>` + `IsEnabled(Warning).Returns(true)` (per TraceServiceTests.cs:247–263 + ChannelRouterTests.cs:172–173 precedent)。漏 IsEnabled-stub 会让 `[LoggerMessage]` source-gen skip log → test fail by under-asserting。
- **Item 3 DbcLoaded test**: 必须用 `EventRaiseExtensions.RaiseMethod` reflection helper (per DbcViewModelTests.cs:70–71 precedent)。直接调 `svc.DbcLoaded?.Invoke(doc)` 行不通 (event 是 public field 但直接 `Invoke` 跳过 multicast delegate 合并)。

## 关键文件

### 新增
- `tests/PeakCan.Host.Core.Tests/Uds/UdsClientConcurrentSecurityAccessTests.cs`

### 修改
- `src/PeakCan.Host.Core/Replay/AscParser.cs` — static → static partial + optional ILogger + [LoggerMessage] helper
- `src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs` — ctor subscribe + OnLoaded handler
- `tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj` — add NSubstitute + Logging.Abstractions
- `tests/PeakCan.Host.Core.Tests/Replay/AscParserTests.cs` — +2 tests
- `tests/PeakCan.Host.App.Tests/ViewModels/DbcSendViewModelTests.cs` — +1 test
- `docs/release-notes-v1.4.1.md` — new
- `Directory.Build.props` — version bump 1.4.0 → 1.4.1

## 风险与缓解

| Risk | Probability | Impact | Mitigation |
|------|-------------|--------|------------|
| **Item 1 race test flake** | HIGH | LOW | 5s deadline pattern; assert non-deterministic outcome ∈ {both succeed, exactly-one throws}; re-run 3x in CI; mark transient-flaky acceptable (memory v1.2.12 lesson 4) |
| **Item 1 race test discover NEW TOCTOU bug** | MEDIUM | MEDIUM | If race test reveals `AttemptCount` race (not just throw ordering), this is v1.4.2 PATCH scope; do NOT fix in v1.4.1 to avoid scope creep |
| **Item 2 AscParser static partial compat** | LOW | LOW | `static partial class` is valid C# syntax (since C# 3.0); [LoggerMessage] source-gen works on static partial |
| **Item 2 AscParser csproj change required** | LOW | LOW | csproj edit is mechanical 2-line change; impl must read this spec §Test design before starting |
| **Item 2 TryParseDataLine signature break** | MEDIUM | MEDIUM | Adding `out string reason` parameter is **breaking change** to internal helper. Verify no other callers besides `ParseLines` (one grep suffices: should be 1 caller) |
| **Item 2 line number off-by-one** | LOW | LOW | `for (int i = 0; ...)` + `lineNumber: i + 1`; test verifies with deterministic input |
| **Item 2 ILogger default-fallback not picked up** | LOW | LOW | `NullLogger<AscParser>.Instance` is the conventional no-op; verify by reading existing `ReplayService` usage |
| **Item 3 DbcLoaded fired BEFORE VM ctor** (race) | LOW | LOW | DI singleton guarantees DbcSendViewModel ctor runs at startup before any LoadAsync call; if not, defensive: in OnLoaded handler, check `_dbcService.Current != doc` before applying (avoid double-populate) |
| **Item 3 ObservableCollection cross-thread** | LOW | MEDIUM | RunOnUi wrap mandatory; precedent in DbcViewModel.cs:125–146; if missed, `NotSupportedException` on first DBC load |
| **Item 3 DbcService LoadFailed event not handled** | MEDIUM | LOW | Out of scope per Decision 6; user sees failure in DbcViewModel.Status, no need to replicate in DbcSendViewModel |
| **Network proxy during ship** | LOW | MEDIUM | Per memory `git-push-network-workaround`: `git -c http.proxy="" -c https.proxy="" push` + `gh api` for tag/release fallback |
| **Phase 2.5 missed API surface assumption** | LOW | LOW | v1.4.0 + v1.3.1 4-of-4 confirmed brief drift is structural; this PATCH's items have been Phase 2.5 verified — low drift risk |

## Ship process

1. `git checkout -b feature/v1-4-1-patch` (from main @ v1.4.0 `4bec174`)
2. Per-Item TDD (3 implementation tasks: Item 1 race test, Item 2 AscParser logging, Item 3 DbcSendViewModel subscription) — each Task with reviewer subagent
3. Pre-ship code-review (whole-branch)
4. Bump `Directory.Build.props` 1.4.0 → 1.4.1
5. Write `docs/release-notes-v1.4.1.md`
6. Push → PR → squash → tag v1.4.1 → release
7. Update memory `peakcan-host-v1-4-1-shipped.md`

## 后续 (deferred to later releases)

- **v1.5.0 MINOR**: V8 sandbox hardening + CanApi rate limit + DBC size/token limit + path normalization + OEM `IKeyDerivationAlgorithm` concrete + Channel picker + Replay loop/CAN ID filter + Periodic DBC send (v1.4.0 spec §Non-Goals 第 53 行原文；8 项 deferred scope).
- **v1.4.2 PATCH (if needed)**: Item 1 race test 发现新 TOCTOU bug 时升级为 PATCH。