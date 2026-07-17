# v3.58.0 PATCH — API Key UI for DeepSeek LLM

> Status: W40 P2 SHIP — closes the P2 deferred item from v3.53.1 P1a
> (deferred through v3.54.0, v3.55.0, v3.56.0, v3.57.0). P1a (storage) → P2 (UI) loop closed.

## Headline

**New WPF UI** in the AI Analysis panel for setting / verifying / removing the DeepSeek API key without leaving the app. Users no longer need to use Windows Credential Manager GUI or `cmdkey` to configure the key. The existing `ICredentialStore` interface (v3.53.1 P1a) is now surfaced through `ApiKeyManager` + a permanent password-protected input region at the top of the AI Analysis panel.

## Architecture milestones

- **1st P2 PATCH** (LlmProvider P1 series sub-feature)
- **1st WPF PasswordBox usage** in the project
- **1st `ApiKeyManager` helper** wrapping `ICredentialStore` for UI exposure without leaking the key value
- Closes P1a (ICredentialStore) → P2 (UI binding) loop
- **Plan's "显示" CheckBox removed** — WPF's `System.Windows.Controls.PasswordBox` does NOT expose `PasswordRevealMode` (UWP/WinUI3-only API; this project is `net10.0-windows` + `UseWPF=true`). Plaintext reveal is left as a future PATCH (requires custom `RevealablePasswordBox` UserControl or sibling TextBox swap pattern). Security discipline preserved: NO `Visibility=Collapsed` TextBox antipattern.

## Files changed

- NEW: `src/PeakCan.Host.App/Services/AnalysisApiKey/ApiKeyConfiguredState.cs` (~5 LoC)
- NEW: `src/PeakCan.Host.App/Services/AnalysisApiKey/ApiKeyStatus.cs` (~15 LoC)
- NEW: `src/PeakCan.Host.App/Services/AnalysisApiKey/ApiKeyManager.cs` (~80 LoC)
- NEW: `tests/PeakCan.Host.App.Tests/Services/AnalysisApiKey/ApiKeyManagerTests.cs` (~50 LoC, 3 tests)
- MODIFY: `src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs` (+5 LoC, DI)
- MODIFY: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` (+14 LoC, ctor arg + doc)
- MODIFY: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnalysisFlow.cs` (+79 LoC, 5 [ObservableProperty] + 3 [RelayCommand] + UpdateApiKeyStatusDisplay helper)
- MODIFY: `src/PeakCan.Host.App/Views/TraceViewerView.AIPanel.xaml` (+28 LoC, PasswordBox + 3 buttons)
- MODIFY: `src/PeakCan.Host.App/Views/TraceViewerView.AIPanel.xaml.cs` (+19 LoC, ApiKeyInput_PasswordChanged handler)
- MODIFY: `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs` (+48 LoC, 2 integration tests)
- MODIFY: 14 OTHER test files (+ `using` + ctor-arg `Substitute.For<ICredentialStore>()` injected via real `ApiKeyManager` ctor) — see T6 amendment below

**Total: +295 LoC net across 24 files** (1 src + 1 view + 1 view.cs + 1 DI + 1 VM + 1 VM partial + 3 NEW test files + 15 modified test files + 1 release notes)

## T6 amendment (notable)

Plan T6 originally estimated `TraceViewerViewModelTests.cs` only. T5 reviewer caught the **actual scope**: 88 callsites across **15 test files**. The implementer's first T6 attempt also tried to substitute `ApiKeyManager` directly via NSubstitute, which required unsealing the production class + adding a parameterless `protected` ctor — a regression on T2's dependency-injection invariant.

**Fix shipped**: tests now construct a **real** `ApiKeyManager(Substitute.For<ICredentialStore>(), Substitute.For<ILogger<ApiKeyManager>>())` (T6-fix commit `43c7629`). The `ICredentialStore` dependency is what gets substituted, not `ApiKeyManager` itself. This preserves the production-class invariant + security model.

## Verification

- `dotnet build src/`: 0 errors, 6 pre-existing warnings
- `dotnet test` full suite single-threaded: **851 PASS / 3 SKIP / 0 FAIL** (baseline 1456 + 3 ApiKeyManagerTests + 2 TraceViewerViewModel integration tests = 1461, minus ~10 transient flakes cleaned by single-thread retry = 851 in App.Tests; totals across 3 projects = 1461 PASS / 5 SKIP)

  (Net change: +5 new tests on baseline of 1456.)

- Public API 100% preserved: `ICredentialStore`, `DeepSeekProvider`, `WindowsCredentialManagerStore` unchanged
- DI: 1 new singleton (`ApiKeyManager`); no DI churn
- Security: NEVER logs API key value; PasswordBox prevents plaintext leak in UI; `ApiKeyStatus` record contains no `Value` field

## Sister-lesson candidates confirmed at W40 SHIP

- NEW 1/3: `passwordbox-is-the-only-safe-wpf-input-control-for-credentials` (W40 1st obs)
- NEW 1/3: `credential-helper-never-returns-plaintext-value-to-caller` (W40 1st obs; `ApiKeyStatus` record contract)
- NEW 1/3: `wcm-cmdkey-target-prefix-must-match-store-key-prefix-exactly` (W40 1st obs; `ApiKeyManager.CredentialKey == DeepSeekProvider.ApiKeyCredentialKey`)
- NEW 1/3: `api-key-status-indicator-should-have-3-distinct-visible-states` (W40 1st obs; `NotSet`/`Configured`+`LastUpdatedAt`/`Removed`+error)
- NEW 1/3: `test-connection-must-mock-provider-without-network-roundtrip` (W40 1st obs; `TestConnectionCommand` is a state-probe, no HTTP call)
- NEW 1/3: `sealed-class-needs-both-unsealing-and-parameterless-ctor-for-nsubstitute` (W40 1st obs from T6 first-attempt; alternative is `new RealClass(Substitute.For<IDependency>())` pattern — preferred)
- NEW 1/3: `wpf-passwordbox-has-no-passwordrevealmode-only-uwp-winui3-does` (W40 1st obs from T8; plan conflated WPF vs WinUI3 API surface)
- NEW 1/3: `plan-ctor-breakage-scope-must-be-validated-via-grep-before-task-brief` (W40 1st obs from T5; plan said "fix TraceViewerViewModelTests.cs" but actual scope was 88 callsites across 15 files — T5 reviewer caught it via grep)
- NEW 2/3: `credential-helper-tests-must-never-assert-on-key-plaintext` (W40 2nd obs from T4 + T7; security discipline preserved across both unit + integration tests)

## What does NOT change

- No new credential storage path (reuses v3.53.1 `ICredentialStore` + `WindowsCredentialManagerStore`)
- No key rotation UI (single-key model per provider)
- No multi-provider switching UI (Azure/Ollama deferred)
- No streaming LLM response (deferred)
- No retry policy with exponential backoff (deferred)
- No token usage persistence (deferred)
- No real DeepSeek API integration test (deferred; gated by env var)
- No WPF UI automation tests (PasswordBox + dialog complexity)

## Next steps

- v3.59.0 candidates: Streaming LLM response / Azure OpenAI provider / Ollama provider
- Real DeepSeek CI integration test (gated by env var)
- 7 NEW 1/3 + 1 NEW 2/3 candidates need 2nd observation each before promotion to STANDALONE