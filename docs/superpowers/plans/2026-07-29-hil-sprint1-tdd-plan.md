# HIL Sprint 1: TDD Implementation Plan

**Date**: 2026-07-29
**Spec**: [2026-07-29-hil-sprint1-design.md](2026-07-29-hil-sprint1-design.md)
**Status**: Ready for implementation
**Scope**: Pure domain model + orchestration skeleton, zero hardware dependency

---

## 1. TDD Increment Overview

14 increments, ordered by dependency. Each increment follows RED → GREEN → IMPROVE.

```
Inc 1   Enums + pure data records          ← zero dependency
Inc 2   Contracts (interfaces)              ← zero dependency
Inc 3   StepParameters hierarchy            ← CanId, CanFrame (Core existing)
Inc 4   ToleranceSpec + DiffConfig          ← TDD: IsWithin, Validate
Inc 5   StepParametersFactory               ← TDD: type conversion, 0x strip, InvariantCulture
Inc 6   TestCaseStep + factory              ← TDD: Kind consistency
Inc 7   Result models                       ← StepResult, TestCaseResult, TestSuiteResult
Inc 8   Serialization                       ← TDD: polymorphic round-trip, $kind inside parameters
Inc 9   AssertionPrimitives                 ← TDD: WaitForSignal, AssertSignal, AssertRange
Inc 10  DiffEngine (frame-level)            ← TDD: exact match, modified, Validate
Inc 11  TestCaseGenerator                   ← TDD: template expansion, label, type conversion
Inc 12  IStepExecutor skeletons             ← TDD: timeout CTS pattern
Inc 13  TestSuiteEngine                     ← TDD: orchestration, teardown, FailurePolicy, Comment
Inc 14  Architecture test                   ← assembly reference check
```

---

## 2. Pre-flight: Verify Core Types

Before Inc 1, verify two unknowns flagged in Spec Section 15:

```
Task 0a: Read CanId.cs — confirm constructor signature (uint raw vs uint + FrameFormat)
Task 0b: Read CanFrame.cs — confirm constructor signature
Task 0c: Read Result.cs + Unit.cs — confirm Result<Unit> exists in Core
Task 0d: Read DbcService.cs — confirm .Current property exists
Task 0e: Read DbcParser output — confirm Message type has .Signals + .Id
```

If any signature differs from Spec assumptions, update Spec Section 8 (StepParametersFactory) before proceeding.

---

## 3. Increment Details

### Inc 1: Enums + Pure Data Records

**Files**:
```
Core/HIL/StepStatus.cs
Core/HIL/StepParameters/StepParameters.cs          // abstract base + JSON attributes
Core/HIL/StepParameters/SendFrameStep.cs
Core/HIL/StepParameters/WaitForSignalStep.cs
Core/HIL/StepParameters/AssertSignalStep.cs
Core/HIL/StepParameters/AssertRangeStep.cs
Core/HIL/StepParameters/ExpectFrameStep.cs
Core/HIL/StepParameters/AssertResponseTimeStep.cs
Core/HIL/StepParameters/AssertDtcStep.cs
Core/HIL/StepParameters/AssertNrcStep.cs
Core/HIL/StepParameters/DelayStep.cs
Core/HIL/StepParameters/CommentStep.cs
Core/HIL/Assertions/AssertionResult.cs
Core/HIL/Progress/TestProgress.cs
Core/HIL/Diff/DiffGranularity.cs
Core/HIL/Diff/AlignStrategy.cs
Core/HIL/Diff/DiffEntry.cs
Core/HIL/Diff/DiffResult.cs
Core/HIL/TestSuiteConfig.cs                        // includes FailurePolicy enum
```

**Method**: Direct implementation (no behavior to test).

**Checklist**:
- [ ] `TestCaseStepKind` enum matches Spec 4.5 (11 values, SendSequence reserved)
- [ ] `StepParameters` has `[JsonDerivedType]` for all 10 subclasses + `[JsonPolymorphic]`
- [ ] `StepResult.Passed` is computed property (`Status == StepStatus.Passed`)
- [ ] `TestSuiteResult.AllPassed` excludes empty suite (`TotalCases > 0 && ...`)
- [ ] `DiffResult.IsMatch` checks `Added == 0 && Removed == 0 && Modified == 0`

---

### Inc 2: Contracts (Interfaces)

**Files**:
```
Core/HIL/Contracts/DecodedFrame.cs
Core/HIL/Contracts/IAssertionContext.cs
Core/HIL/Contracts/ISignalObserver.cs
Core/HIL/Contracts/ISignalHistory.cs
Core/HIL/Contracts/IDbcLookup.cs
Core/HIL/Setup/ITestFixture.cs
Core/HIL/Setup/IFixtureResolver.cs
Core/HIL/Diff/IDiffEngine.cs
Core/HIL/StepExecutor/IStepExecutor.cs
```

**Method**: Direct implementation (interfaces, no behavior).

**Checklist**:
- [ ] `IAssertionContext` has 4 members: `SubscribeDecodedFrames`, `GetSignalValue`, `CurrentTimestamp`, `SendFrameAsync`
- [ ] `IDisposable contract` documented in XML comments (4 points: idempotent, no callback after dispose, non-blocking, drain with 5s timeout)
- [ ] `CurrentTimestamp` doc says "microseconds (matches CanFrame.Timestamp.TotalMicroseconds)"
- [ ] `GetSignalValue` doc says format "MessageName.SignalName"
- [ ] `IFixtureResolver` has single method `Resolve(string key)`

---

### Inc 3: StepParameters Hierarchy

**Files**:
```
Core/HIL/StepParameters/StepParameters.cs    // if not created in Inc 1
+ all 10 subclass files
```

**Method**: Direct implementation (records with positional parameters).

**Depends on**: Inc 1, Pre-flight 0a (CanId constructor).

**Checklist**:
- [ ] Each subclass passes correct `TestCaseStepKind` to base constructor
- [ ] `SendFrameStep` uses `CanId` (verified in Pre-flight)
- [ ] `ExpectFrameStep` has `DataMask` as `byte[]?` (nullable)
- [ ] `AssertDtcStep` has `DtcCode` as `ushort?` (nullable)

---

### Inc 4: ToleranceSpec + DiffConfig (TDD)

**Files**:
```
Core/HIL/Diff/ToleranceSpec.cs
Core/HIL/Diff/DiffConfig.cs
Tests/HIL/Diff/ToleranceSpecTests.cs
Tests/HIL/Diff/DiffConfigTests.cs
```

**Depends on**: Inc 1.

#### RED — Write tests first:

```
ToleranceSpecTests:
  ✓ IsWithin_Exact_True_WhenEqual
  ✓ IsWithin_Exact_False_WhenDifferent
  ✓ IsWithin_AbsoluteTolerance_True_WithinRange
  ✓ IsWithin_AbsoluteTolerance_False_OutsideRange
  ✓ IsWithin_RelativeTolerance_True_WithinPct
  ✓ IsWithin_RelativeTolerance_False_OutsidePct
  ✓ IsWithin_BothTolerances_True_WhenEitherSatisfied    // OR semantics
  ✓ IsWithin_NegativeTolerance_Throws                   // constructor validation

DiffConfigTests:
  ✓ Validate_NearestNeighbor_ZeroWindow_Throws
  ✓ Validate_NearestNeighbor_NegativeWindow_Throws
  ✓ Validate_NearestNeighbor_PositiveWindow_OK
  ✓ Validate_Timestamp_ZeroWindow_OK                     // window ignored for Timestamp
  ✓ Validate_NegativeAbsoluteTolerance_Throws
  ✓ Validate_NegativeRelativeTolerance_Throws
  ✓ Validate_AfterWithExpression_StillCatches            // with { Alignment = NearestNeighbor, NeighborWindowMs = 0 }
  ✓ DefaultConstructor_ProducesValidConfig
```

#### GREEN — Implement:

- `ToleranceSpec.IsWithin`: `diff <= AbsoluteTolerance || diff <= Math.Abs(expected) * RelativeTolerance`
- `ToleranceSpec` constructor: throw if negative tolerance
- `DiffConfig.Validate()`: check NearestNeighbor + window, check negative tolerance

#### IMPROVE:
- Extract validation messages to constants
- Consider `init` accessor validation as secondary defense

---

### Inc 5: StepParametersFactory (TDD)

**Files**:
```
Core/HIL/StepParameters/StepParametersFactory.cs
Tests/HIL/StepParameters/StepParametersFactoryTests.cs
```

**Depends on**: Inc 3, Pre-flight 0a (CanId constructor).

#### RED — Write tests first:

```
StepParametersFactoryTests:
  ✓ Create_WaitForSignal_CorrectFields
  ✓ Create_SendFrame_With0xPrefix_ParsesCorrectly          // "0x7DF"
  ✓ Create_SendFrame_With0XPrefix_ParsesCorrectly          // "0X7DF"
  ✓ Create_SendFrame_WithoutPrefix_ParsesCorrectly          // "7DF"
  ✓ Create_SendFrame_HexData_ParsesCorrectly                // "0210030000000000"
  ✓ Create_Delay_CorrectMilliseconds
  ✓ Create_Comment_CorrectText
  ✓ Create_UnknownKind_ThrowsArgumentException
  ✓ Create_MissingKey_ThrowsKeyNotFoundException            // p["NonExistent"]
  ✓ Create_InvariantCulture_Parses3_14_OnAnyCulture         // Force comma culture, verify 3.14
  ✓ Create_SendFrame_InvalidHex_ThrowsFormatException
```

#### GREEN — Implement:

- `switch` expression on `TestCaseStepKind`
- `Convert.ToDouble(value, CultureInfo.InvariantCulture)` for all doubles
- `Convert.ToInt32(value, CultureInfo.InvariantCulture)` for all ints
- `((string)p["Id"]).TrimStart("0x", "0X")` for CAN ID
- `Convert.FromHexString((string)p["Data"])` for byte data
- `_ => throw new ArgumentException($"Unknown step kind: {kind}")` fallback

#### IMPROVE:
- Extract `TrimStart` to helper method `ParseCanId(string raw)`
- Add XML docs for each case

---

### Inc 6: TestCaseStep + Factory (TDD)

**Files**:
```
Core/HIL/TestCaseStep.cs
Tests/HIL/TestCaseStepTests.cs
```

**Depends on**: Inc 3.

#### RED:

```
TestCaseStepTests:
  ✓ Create_KindMatchesParametersKind                     // Kind == Parameters.Kind
  ✓ Create_PreservesLabel
  ✓ Create_NullLabel_OK
  ✓ Create_DifferentKind_Throws                           // not possible via factory, verify no mismatch path
```

#### GREEN:
- Private constructor
- `Create(StepParameters parameters, string? label = null)` factory derives `Kind` from `parameters.Kind`

#### IMPROVE:
- Add `[JsonConverter(typeof(TestCaseStepJsonConverter))]` attribute (converter implemented in Inc 8)

---

### Inc 7: Result Models

**Files**:
```
Core/HIL/StepResult.cs (if not in Inc 1)
Core/HIL/TestCaseResult.cs
Core/HIL/TestSuiteResult.cs
Core/HIL/TestCase.cs
Core/HIL/TestSuite.cs
```

**Method**: Direct implementation (records, computed properties).

**Depends on**: Inc 1, Inc 6.

**Checklist**:
- [ ] `StepResult.Passed` => `Status == StepStatus.Passed`
- [ ] `TestCaseResult` has `CommentSteps` field
- [ ] `TestCaseResult.TotalSteps` excludes Comment (set by Engine, documented)
- [ ] `TestSuiteResult.AllPassed` => `TotalCases > 0 && FailedCases == 0 && SkippedCases == 0`
- [ ] `TestSuiteResult.PassRate` => `TotalCases > 0 ? PassedCases / TotalCases : 0.0`
- [ ] `TestSuiteResult.SetupFailures` is `IReadOnlyList<string>`
- [ ] `TestCase` has `CaseFixtureKeys` as `IReadOnlyList<string>?`
- [ ] `TestSuite` has `GlobalCaseFixtureKeys` + `SuiteFixtureKeys`

---

### Inc 8: Serialization (TDD)

**Files**:
```
Core/HIL/Serialization/HILJsonOptions.cs
Core/HIL/Serialization/TestCaseStepJsonConverter.cs
Tests/HIL/Serialization/SerializationTests.cs
```

**Depends on**: Inc 3, Inc 6.

#### RED:

```
SerializationTests:
  ✓ TestCaseStep_RoundTrip_PreservesKind
  ✓ TestCaseStep_RoundTrip_PreservesLabel
  ✓ TestCaseStep_RoundTrip_PreservesAllParameters       // WaitForSignalStep with all fields
  ✓ TestCaseStep_RoundTrip_NullLabel_Omitted             // WhenWritingNull
  ✓ StepParameters_Polymorphic_PreservesSubtype          // $kind discriminator
  ✓ StepParameters_DollarKind_InsideParameters           // not at TestCaseStep level
  ✓ SendFrameStep_RoundTrip_PreservesCanId
  ✓ CommentStep_RoundTrip_PreservesText
  ✓ TestCase_RoundTrip_PreservesAllFields
  ✓ TestSuite_RoundTrip_PreservesAllFields
  ✓ MultipleStepTypes_InSameArray_RoundTrip              // array of TestCaseStep
```

#### GREEN:
- `HILJsonOptions.Default`: CamelCase + WriteIndented + WhenWritingNull
- `TestCaseStepJsonConverter.Read`: deserialize `parameters` as `StepParameters` (polymorphic), derive `Kind` from `parameters.Kind`
- `TestCaseStepJsonConverter.Write`: serialize `parameters` as `typeof(StepParameters)` (triggers polymorphic `$kind`)

#### IMPROVE:
- Test edge cases: empty steps array, null optional fields
- Verify JSON output matches Spec 9.4 schema example

---

### Inc 9: AssertionPrimitives (TDD)

**Files**:
```
Core/HIL/Assertions/AssertionPrimitives.cs
Tests/HIL/Assertions/AssertionPrimitivesTests.cs
```

**Depends on**: Inc 2 (IAssertionContext), Inc 4 (ToleranceSpec for patterns).

**Test infrastructure**: Need a `FakeAssertionContext` that implements `IAssertionContext`:
- `SubscribeDecodedFrames`: stores callback, returns `FakeSubscription` (IDisposable that removes callback)
- `GetSignalValue`: returns value from internal `Dictionary<string, double>`
- `CurrentTimestamp`: returns `Stopwatch.GetTimestamp()` based value
- `SendFrameAsync`: records sent frames, returns `Result<Unit>.Success(default)`

#### RED:

```
AssertionPrimitivesTests:
  ✓ WaitForSignal_SignalMatches_PassesImmediately
  ✓ WaitForSignal_SignalMatchesWithinTolerance_Passes
  ✓ WaitForSignal_Timeout_FailsWithActualValue
  ✓ WaitForSignal_SignalNotFound_FailsWithNullActual
  ✓ WaitForSignal_CancellationTokenCancelled_ThrowsOperationCanceledException
  ✓ WaitForSignal_MultipleFrames_EventuallyMatches                  // frame 1 no match, frame 2 match
  ✓ AssertSignal_InTolerance_Passes
  ✓ AssertSignal_OutOfTolerance_FailsWithActualAndExpected
  ✓ AssertSignal_SignalNotFound_Fails
  ✓ AssertRange_InRange_Passes
  ✓ AssertRange_OutOfRange_Fails
  ✓ AssertRange_SignalNotFound_Fails
```

#### GREEN:
- `WaitForSignalAsync`: `TaskCompletionSource<bool>` + `SubscribeDecodedFrames` + `Task.Delay(Timeout.Infinite, ct)` + `Task.WhenAny`
- `using var sub` ensures cleanup
- `AssertSignal` / `AssertRange`: synchronous, read `GetSignalValue`, return `AssertionResult`

#### IMPROVE:
- Verify `using var sub` Dispose is called on all exit paths (pass, fail, cancel)
- Consider `tcs.TrySetCanceled()` on ct cancellation for cleaner exception

---

### Inc 10: DiffEngine — Frame-Level (TDD)

**Files**:
```
Core/HIL/Diff/DiffEngine.cs
Tests/HIL/Diff/DiffEngineTests.cs
```

**Depends on**: Inc 4 (DiffConfig, ToleranceSpec), Inc 7 (DiffResult, DiffEntry).

**Test data helpers**: Need `CanFrame` builder for tests:
```csharp
static CanFrame Frame(uint id, byte[] data, double timestamp = 0) => ...;
```

#### RED:

```
DiffEngineTests:
  ✓ FrameLevel_ExactMatch_IdenticalSequences_IsMatch
  ✓ FrameLevel_ExactMatch_EmptySequences_IsMatch
  ✓ FrameLevel_ExactMatch_OneModified_ModifiedEquals1
  ✓ FrameLevel_ExactMatch_ActualExtraFrame_AddedEquals1
  ✓ FrameLevel_ExactMatch_GoldenExtraFrame_RemovedEquals1
  ✓ FrameLevel_TimestampAlignment_MatchesByTimestamp
  ✓ FrameLevel_IndexAlignment_MatchesByOrder
  ✓ FrameLevel_NearestNeighbor_MatchesWithinWindow
  ✓ FrameLevel_NearestNeighbor_OutsideWindow_NoMatch
  ✓ Validate_NearestNeighborZeroWindow_ThrowsAtDiffEntry
  ✓ Validate_NegativeTolerance_ThrowsAtDiffEntry
  ✓ Validate_WithExpressionBypass_StillCatches                      // config with { ... }
  ✓ SignalLevel_WithoutDbcLookup_ThrowsInvalidOperationException
  ✓ DiffResult_MatchRate_Correct
  ✓ DiffResult_IsMatch_True_WhenAllMatched
```

#### GREEN:
- `DiffEngine()` parameterless constructor for frame-level
- `Diff(config.Validate())` at entry
- Frame-level: compare `CanFrame.Id` + `CanFrame.Data.Span` (exact or with mask)
- Timestamp alignment: match frames by `Timestamp` proximity
- Index alignment: match by position in list
- NearestNeighbor: match within `NeighborWindowMs` window

#### IMPROVE:
- Extract frame comparison to `FrameEquals(CanFrame a, CanFrame b, ToleranceSpec tolerance)`
- Consider `DiffEntry` with `Reason` string for mismatch details

---

### Inc 11: TestCaseGenerator (TDD)

**Files**:
```
Core/HIL/Parameterization/ParameterSet.cs
Core/HIL/Parameterization/TemplateStep.cs
Core/HIL/Parameterization/TestCaseTemplate.cs
Core/HIL/Parameterization/TestCaseGenerator.cs
Tests/HIL/Parameterization/TestCaseGeneratorTests.cs
```

**Depends on**: Inc 5 (StepParametersFactory), Inc 6 (TestCaseStep), Inc 7 (TestCase).

#### RED:

```
TestCaseGeneratorTests:
  ✓ Generate_SingleParameter_ReplacesInName
  ✓ Generate_MultipleParameters_ReplacesAllInName
  ✓ Generate_StepParameters_ConvertedToStrongType
  ✓ Generate_Label_PreservedFromTemplate
  ✓ Generate_IdCombinesBaseIdAndParameterId
  ✓ Generate_UnresolvedParameter_KeepsOriginalPlaceholder          // {{unknown}} stays
  ✓ Generate_NullParameterValue_KeepsOriginalPlaceholder
  ✓ Generate_TypeConversionFailure_ThrowsArgumentException
  ✓ Generate_UnknownKind_ThrowsArgumentException
  ✓ Generate_DescriptionTemplate_ReplacedCorrectly
```

#### GREEN:
- `Resolve(template, parameters)`: `Regex.Replace(template, @"\{\{(\w+)\}\}", ...)`
- `StepParametersFactory.Create(kind, resolvedParams)` for each step
- `TestCaseStep.Create(parameters, step.Label)` preserving label

#### IMPROVE:
- Test with special characters in parameter values
- Test with nested `{{param}}` in step parameters (not just name/description)

---

### Inc 12: IStepExecutor Skeletons (TDD)

**Files**:
```
Core/HIL/StepExecutor/IStepExecutor.cs                    // interface (from Inc 2)
Core/HIL/StepExecutor/WaitForSignalStepExecutor.cs
Core/HIL/StepExecutor/AssertSignalStepExecutor.cs
Core/HIL/StepExecutor/AssertRangeStepExecutor.cs
Core/HIL/StepExecutor/ExpectFrameStepExecutor.cs
Core/HIL/StepExecutor/AssertResponseTimeStepExecutor.cs
Core/HIL/StepExecutor/AssertDtcStepExecutor.cs
Core/HIL/StepExecutor/AssertNrcStepExecutor.cs
Core/HIL/StepExecutor/SendFrameStepExecutor.cs
Core/HIL/StepExecutor/DelayStepExecutor.cs
Core/HIL/StepExecutor/SendSequenceStepExecutor.cs         // throws NotSupportedException
Tests/HIL/StepExecutor/StepExecutorTests.cs
```

**Depends on**: Inc 9 (AssertionPrimitives), Inc 2 (IAssertionContext).

#### RED:

```
StepExecutorTests:
  ✓ WaitForSignalExecutor_ReturnsPassed_WhenSignalMatches
  ✓ WaitForSignalExecutor_CreatesLinkedCTS_ForTimeout          // verify timeout CTS created
  ✓ WaitForSignalExecutor_ReturnsFailed_OnTimeout
  ✓ AssertSignalExecutor_ReturnsPassed_WhenInTolerance
  ✓ AssertSignalExecutor_ReturnsFailed_WhenOutOfTolerance
  ✓ AssertRangeExecutor_ReturnsPassed_WhenInRange
  ✓ AssertRangeExecutor_ReturnsFailed_WhenOutOfRange
  ✓ DelayExecutor_ReturnsPassed_AfterDelay
  ✓ SendFrameExecutor_CallsSendFrameAsync
  ✓ SendFrameExecutor_ReturnsPassed_WhenSendSucceeds
  ✓ SendFrameExecutor_ReturnsFailed_WhenSendFails
  ✓ SendSequenceExecutor_Throws_NotSupportedException
  ✓ Executor_ReturnsStepResult_WithDefaultStepIndexZero          // Engine overrides later
  ✓ Executor_ReturnsStepResult_WithDefaultElapsedMsZero          // Engine overrides later
```

#### GREEN:
- Each executor: cast `step.Parameters` to specific type, create `AssertionPrimitives` (or call `ctx` directly), execute
- Timeout pattern: `using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct); timeoutCts.CancelAfter(p.TimeoutMs);`
- `SendSequenceStepExecutor`: `throw new NotSupportedException("SendSequence not supported in Sprint 1")`
- Return `StepResult` with `StepIndex = 0, ElapsedMs = 0` (Engine overrides)

#### IMPROVE:
- Verify `timeoutCts` is disposed on all paths (pass, fail, cancel)
- Extract common `ToStepResult(TestCaseStep, AssertionResult)` helper

---

### Inc 13: TestSuiteEngine (TDD)

**Files**:
```
Core/HIL/TestSuiteEngine.cs
Tests/HIL/TestSuiteEngineTests.cs
```

**Depends on**: All previous increments.

**Test infrastructure**: Need `FakeFixtureResolver` (Spec 5.7) + `FakeStepExecutor` + `FakeAssertionContext`.

#### RED — Orchestration tests:

```
TestSuiteEngineTests:
  // Empty suite
  ✓ EmptySuite_Returns_TotalCasesZero_AllPassedFalse

  // Single case, single step
  ✓ SingleCase_SinglePassedStep_ReturnsPassed
  ✓ SingleCase_SingleFailedStep_ReturnsFailed

  // Comment step semantics (critical bug fix verification)
  ✓ CommentStep_Only_ReturnsPassed
  ✓ CommentStep_PlusPassedStep_ReturnsPassed
  ✓ CommentStep_PlusFailedStep_ReturnsFailed
  ✓ CommentStep_DoesNotMask_FailureReason

  // FailurePolicy: StopCaseOnFailure
  ✓ StopCaseOnFailure_StepFails_RemainingStepsSkipped
  ✓ StopCaseOnFailure_StepFails_TeardownStillCalled
  ✓ StopCaseOnFailure_SkippedSteps_HaveSkippedStatus

  // FailurePolicy: ContinueAll
  ✓ ContinueAll_StepFails_AllStepsExecuted
  ✓ ContinueAll_StepFails_CaseMarkedFailed

  // FailurePolicy: StopSuiteOnFailure
  ✓ StopSuiteOnFailure_CaseFails_RemainingCasesSkipped
  ✓ StopSuiteOnFailure_CaseFails_SuiteTeardownStillCalled

  // Setup/Teardown
  ✓ CaseSetupFailure_StepsSkipped_TeardownStillCalled
  ✓ CaseSetupFailure_FailureReasonContainsSetupError
  ✓ SuiteSetupFailure_ContinueAfterSetupFalse_AllCasesSkipped
  ✓ SuiteSetupFailure_ContinueAfterSetupTrue_CasesStillExecute
  ✓ SetupAndTeardownBothFail_FailureReasonContainsBoth

  // Teardown ordering
  ✓ CaseTeardown_ReverseOrder_OfSetup
  ✓ SuiteTeardown_ReverseOrder_OfSetup
  ✓ Teardown_IndependentCancellationToken_NotAffectedByCancellation

  // Cancellation
  ✓ CancellationToken_Throws_OperationCanceledException
  ✓ CancellationToken_TeardownStillCalled
  ✓ CancellationToken_SuiteTeardownStillCalled

  // Progress
  ✓ Progress_Reports_PerCaseCompletion
  ✓ Progress_Reports_CurrentCaseName

  // Defensive catch
  ✓ ExecutorThrowsUnhandledException_StepMarkedFailed
  ✓ ExecutorThrowsOperationCanceledException_Propagated

  // Result aggregation
  ✓ StepIndex_OverriddenByEngine_NotZeroFromExecutor
  ✓ ElapsedMs_OverriddenByEngine_NotZeroFromExecutor
  ✓ TotalSteps_ExcludesCommentSteps
  ✓ PassedPlusFailedPlusSkipped_EqualsTotalSteps
  ✓ SkippedCases_EqualsTotalMinusExecutedCases

  // Global + case fixture merge
  ✓ GlobalCaseFixtures_ExecutedBefore_CaseFixtures
  ✓ CaseFixtures_ExecutedBefore_Steps
```

#### GREEN:
- `ExecuteAsync`: linked CTS + suite fixture resolution + try-finally teardown
- `ExecuteCaseAsync`: fixture merge (global + case) + try-finally teardown + step loop
- Step loop: Comment `continue`, executor try-catch, `result with { StepIndex, ElapsedMs }`
- `passed = failureReason is null && stepResults.All(r => r.Status != StepStatus.Failed)`
- `TestCaseResult` aggregation with `CommentSteps` count

#### IMPROVE:
- Extract `ExecuteStepLoop` to reduce `ExecuteCaseAsync` size
- Extract `RunTeardown` helper for the try-catch-reverse pattern
- Verify `Stopwatch` usage doesn't leak timers

---

### Inc 14: Architecture Test

**Files**:
```
Tests/Architecture/HILLayeringTests.cs
```

**Depends on**: Inc 1 (TestCase type exists).

#### Implementation:

```csharp
[Fact]
public void HIL_assembly_does_not_reference_Infrastructure_or_App()
{
    var assembly = typeof(TestCase).Assembly;
    var refs = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
    Assert.DoesNotContain("PeakCan.Host.Infrastructure", refs);
    Assert.DoesNotContain("PeakCan.Host.App", refs);
}
```

**Checklist**:
- [ ] Test passes (Core csproj has no ProjectReference to Infrastructure/App)
- [ ] Test is in `PeakCan.Host.Core.Tests` project

---

## 4. File-to-Increment Matrix

| File | Increment | TDD? |
|---|---|---|
| `StepStatus.cs` | 1 | No (enum) |
| `StepParameters.cs` | 1/3 | No (record + attributes) |
| `SendFrameStep.cs` ... `CommentStep.cs` | 3 | No (records) |
| `AssertionResult.cs` | 1 | No (record) |
| `TestProgress.cs` | 1 | No (record) |
| `DiffGranularity.cs`, `AlignStrategy.cs` | 1 | No (enums) |
| `DiffEntry.cs`, `DiffResult.cs` | 1 | No (records) |
| `TestSuiteConfig.cs` | 1 | No (record + enum) |
| `Contracts/*.cs` (6 files) | 2 | No (interfaces) |
| `Setup/*.cs` (2 files) | 2 | No (interfaces) |
| `IDiffEngine.cs`, `IStepExecutor.cs` | 2 | No (interfaces) |
| `ToleranceSpec.cs` | 4 | **Yes** |
| `DiffConfig.cs` | 4 | **Yes** |
| `StepParametersFactory.cs` | 5 | **Yes** |
| `TestCaseStep.cs` | 6 | **Yes** |
| `TestCase.cs`, `TestSuite.cs` | 7 | No (records) |
| `StepResult.cs`, `TestCaseResult.cs`, `TestSuiteResult.cs` | 7 | No (records) |
| `HILJsonOptions.cs` | 8 | No (static config) |
| `TestCaseStepJsonConverter.cs` | 8 | **Yes** |
| `AssertionPrimitives.cs` | 9 | **Yes** |
| `DiffEngine.cs` | 10 | **Yes** |
| `Parameterization/*.cs` (4 files) | 11 | **Yes** (Generator) |
| `StepExecutor/*.cs` (10 files) | 12 | **Yes** |
| `TestSuiteEngine.cs` | 13 | **Yes** |
| `TypeResolver.cs` | 13 | No (utility, used by Sprint 2) |
| `HILLayeringTests.cs` | 14 | No (architecture test) |

**TDD increments**: 4, 5, 6, 8, 9, 10, 11, 12, 13 (9 of 14)
**Direct implementation**: 1, 2, 3, 7, 14 (5 of 14)

---

## 5. Test Infrastructure (Shared Fakes)

Created in Inc 9, reused by Inc 12 + Inc 13:

```
Tests/HIL/Fakes/FakeAssertionContext.cs
  - SubscribeDecodedFrames: callback list, IDisposable removes
  - GetSignalValue: Dictionary<string, double> with setter
  - CurrentTimestamp: configurable
  - SendFrameAsync: records frames, configurable result
  - PushFrame(DecodedFrame): simulates frame arrival for testing

Tests/HIL/Fakes/FakeFixtureResolver.cs
  - Register(key, fixture)
  - Resolve(key)

Tests/HIL/Fakes/FakeSubscription.cs
  - Implements IDisposable
  - Removes callback from FakeAssertionContext on Dispose

Tests/HIL/Fakes/FakeStepExecutor.cs
  - Kind: configurable
  - ExecuteAsync: returns configurable StepResult
  - ExecuteCallCount: track invocations
```

---

## 6. Risk Register

| Risk | Impact | Mitigation |
|---|---|---|
| `CanId` constructor doesn't accept single `uint` | Inc 3/5 compile failure | Pre-flight 0a; if mismatch, update factory + StepParameters |
| `CanFrame` constructor differs | Inc 7/10 compile failure | Pre-flight 0b |
| `Result<Unit>` doesn't exist in Core | Inc 2 compile failure | Pre-flight 0c; if missing, define in Core or use `Result<bool>` |
| `DbcService.Current` doesn't exist | Sprint 2 blocker (not Sprint 1) | Pre-flight 0d; Sprint 1 doesn't implement `DbcLookupAdapter` |
| NSubstitute can't mock `IAssertionContext` | Inc 9/12/13 test failure | Use `FakeAssertionContext` hand-rolled fake (not NSubstitute) |
| `System.Text.Json` polymorphic attributes require .NET 10 | Inc 8 compile failure | Verify .NET 10 SDK; attributes exist since .NET 7 |
| `TrimStart("0x", "0X")` fails on CAN ID=0 | Inc 5 edge case | Add special case: if result empty, return "0" |

---

## 7. Definition of Done

- [ ] All 14 increments completed
- [ ] All RED tests pass (GREEN)
- [ ] No skipped tests
- [ ] `dotnet test PeakCan.Host.slnx -c Debug` passes (existing + new tests)
- [ ] `dotnet build PeakCan.Host.slnx -c Release` passes
- [ ] Architecture test passes (Core/HIL no Infrastructure/App reference)
- [ ] No `console.log` / `Debug.WriteLine` left in production code
- [ ] Code coverage for `Core/HIL/` >= 80%
- [ ] Spec file updated if any signatures changed during implementation
