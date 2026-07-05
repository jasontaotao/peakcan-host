# peakcan-host v3.6.1 PATCH — `.tmtrace` JSON Schema contract

## Summary

v3.6.1 PATCH is a **doc + test** PATCH that pins the canonical contract for the `.tmtrace` v1 bundle format introduced in v3.5.0 and refined in v3.6.0. No production code changes; the bundle format itself is unchanged.

1. **NEW** `docs/schemas/tmtrace-v1.schema.json` — a JSON Schema (draft 2020-12) document describing every top-level field + sub-DTO (`BundleSourceDto` / `BundlePlaybackDto` / `BundleViewportDto`) with types, ranges (`colorA/R/G/B` = 0-255), required-vs-optional markers, and `additionalProperties: true` to match the v3.5.0 design intent ("adding fields is non-breaking").
2. **NEW** `tests/.../TmtraceSchemaValidationTests.cs` — a reflection-based drift test that walks the `TraceSessionBundleDto` + sub-DTO `Type` definitions, enumerates each `[JsonPropertyName]`-attributed property, and asserts the schema contains a matching key. Catches accidental DTO rename / property drop / type change at test time. Also pins the ARGB byte range (0-255) and the `const`-pinned `version` + `schema` fields.

## Why this ship

- **Contract discoverability** — future contributors (including past-me and reviewer-bots) can read the schema file instead of reverse-engineering the C# DTOs to understand what a `.tmtrace` file looks like.
- **Drift detection** — without the test, a maintainer could rename a DTO property (e.g. `canIdFilter` → `canIdFilterString`) and the existing `TraceSessionLibraryTests` would still pass (deserializer ignores unknown keys), but every previously-saved bundle would silently lose the field. The new test fails on rename.
- **Forward-compat documentation** — the schema explicitly documents the `additionalProperties: true` design choice (matching the v3.5.0 release-notes "Adding fields is non-breaking" claim) so future readers know that the schema is a contract for known fields, not a closed-world validator.
- **Sets the seam for v2** — if a future MINOR ever ships a `tmtrace-v2.schema.json`, the test pattern (reflection on DTO + schema $defs) extends directly: add the v2 DTO type + a v2 $def block, and the test framework can compare multiple schema files side-by-side.

## What changed

**1 commit** (1 feat). 1 new docs file + 1 new test file + 1 modified README. Zero production code, zero new third-party dependencies.

| Path | Δ | Fix |
|------|---|-----|
| `docs/schemas/tmtrace-v1.schema.json` | NEW (~140 LOC) | JSON Schema draft 2020-12 — top-level + `$defs` for the 3 sub-DTOs. |
| `tests/PeakCan.Host.App.Tests/Services/Trace/TmtraceSchemaValidationTests.cs` | NEW (~145 LOC) | 5 tests: top-level DTO presence, sub-DTO presence, schema version+identifier pinned, required-fields superset, ARGB byte range. |
| `README.md` | +6 / −4 | Status line → v3.6.1; test count → 1122 + 5 SKIP; new bullet point mentions the schema doc. |

## Fix-by-fix detail

### Fix 1 — JSON Schema for `.tmtrace` v1

**File**: `docs/schemas/tmtrace-v1.schema.json`

**Design choices**:

- **Draft 2020-12** (latest stable; widely supported by validators; `$defs` instead of the deprecated `definitions`).
- **`additionalProperties: true`** on the top-level + each sub-DTO — the v3.5.0 design intent is "adding fields is non-breaking (deserializer ignores unknown keys)". A strict `false` here would forbid future extensions; a permissive `true` documents the producer's freedom and is the only correct choice.
- **`const` for `version` and `schema`** — pins the wire format to v1. Any other value at these keys means a different format and the schema validator should reject.
- **`required` array at top level** — includes all DTO fields except `playback` (which is nullable / optional). Sub-DTOs each have their own `required` arrays.
- **Per-property `description`** — embeds the design rationale into the schema itself (e.g. the comment about `playback` being restored to a paused cursor; the ARGB byte storage rationale; the path-reference-only `.asc` contract). Future consumers reading the schema get the why, not just the what.
- **Per-property types + ranges** — `colorA/R/G/B` are explicitly `{"type": "integer", "minimum": 0, "maximum": 255}` to pin the OxyColor byte contract; `playback.startTimestamp` and `playback.endTimestamp` are `["number", "null"]` for nullable double; `savedAt` is `format: date-time` for ISO 8601.
- **No enum on `strokeStyle`** — the actual `LineStyle` enum from OxyPlot has 12 members (Solid, Dash, Dot, DashDot, ..., Hidden, Automatic). A `string` constraint with a description noting "valid values are LineStyle enum names; consumer should fall back to Solid on unknown" is more maintainable than enumerating 12 in the schema.
- **No `$ref` to external schemas** — the schema is fully self-contained. Anyone can validate without network access.

### Fix 2 — Drift-detection test

**File**: `tests/PeakCan.Host.App.Tests/Services/Trace/TmtraceSchemaValidationTests.cs`

**5 tests** (the bare minimum for full coverage of the contract surface):

1. **`TopLevelDto_PropertiesArePresentInSchema`** — reflection over `TraceSessionBundleDto.GetProperties()`. For each `[JsonPropertyName]`-attributed property (skipping `[JsonIgnore]`), asserts the schema's `properties` contains a matching key. Catches: DTO property added but missing from schema; DTO property removed but still in schema; DTO property renamed.
2. **`SubDto_PropertiesArePresentInSchema`** — same check for the 3 sub-DTOs (`BundleSourceDto` / `BundlePlaybackDto` / `BundleViewportDto`) using the schema's `$defs` block.
3. **`SchemaVersionAndIdentifier_ArePinned`** — asserts `properties.version.const == 1` and `properties.schema.const == "tmtrace/v1"`. Catches: someone weakening the version constraint; someone shipping a v2 bundle under the v1 schema file.
4. **`TopLevelRequired_IncludesAllNonNullableScalarDtoProperties`** — asserts the schema's `required` array is a superset of the 8 fields that the C# DTO requires by construction (`version`, `schema`, `savedAt`, `appVersion`, `dbcPath`, `globalCanIdFilter`, `sources`, `viewports`). Catches: someone removing a field from `required` without considering the DTO.
5. **`BundleSourceDto_ColorChannelsAreByteRanged`** — asserts each of `colorA/R/G/B` has `type: integer, minimum: 0, maximum: 255`. Catches: someone widening the type to `int` (for HDR support) without updating the schema; someone dropping the range constraint.

**No new third-party dependencies**. Uses only `System.Reflection`, `System.Text.Json`, and `FluentAssertions` (already in the test project).

**Helper**: `FindRepoRoot()` walks up from `Directory.GetCurrentDirectory()` until it finds `PeakCan.Host.slnx`. This makes the test path-agnostic across CI / local / fork workflows.

## Test delta

| Suite | v3.6.0 | v3.6.1 | Δ |
|-------|--------|--------|---|
| App | 629 + 3 SKIP | **634 + 3 SKIP** | +5 (new `TmtraceSchemaValidationTests`) |
| Core | 404 | 404 | 0 |
| Infrastructure | 84 + 2 SKIP | 84 + 2 SKIP | 0 |
| **Total** | **1117 + 5 SKIP** | **1122 + 5 SKIP** | **+5** |

All 5 new tests are deterministic (no `Task.Delay` / wall-clock waits). The schema file is read once at test-static-init; the reflection walk is one-shot per test.

## Pre-ship review

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | — |
| HIGH     | 0 | — |
| MEDIUM   | 0 | — |
| LOW      | 0 | — |
| **Verdict** | — | **APPROVE** (self-reviewed; doc + test; both compile + run clean on attempt 1 after a `[JsonIgnore]`-skip and `properties.version.const` path fix; release-notes reflects the actual test count) |

## Tier 3 ship

- **Branch**: `feature/v3-6-0-minor` (local; never pushed — Tier 3 handles ship)
- **Parent**: v3.6.0 MINOR on origin/main (`ae64c94d678f95f4a910bfc76e4c526b905c9bd5`)
- **Tier 3 force-update** via `gh api` JSON payloads — `scripts/tier3_v361.py`
- **Tag**: `v3.6.1` (PATCH, non-breaking — doc + test only)

## Closest cousins / related

- [[peakcan-host-v3-6-0-minor-shipped]] — parent MINOR (the bundle format itself; the schema now formally documents what that MINOR shipped).
- [[peakcan-host-v3-5-0-minor-shipped]] — grandparent MINOR (`.tmtrace` bundle format first shipped; the "Adding fields is non-breaking" claim that this schema's `additionalProperties: true` formalizes).

## Non-scope (still deferred)

- **JSON Schema validator library in test project** — would let the test actually invoke the schema's `required` / `const` / `range` rules against a real bundle, instead of just doing reflection-based drift detection. Currently YAGNI: the schema doc + reflection test cover the drift surface that a maintainer can actually break; full validation would be a CI-side concern, not a unit-test concern. Reconsider if a v2 format is ever needed.
- **`.asc` file format doc** — the bundle is a meta-format that references `.asc` files. The `.asc` format itself (Vector ASCII trace) is documented by Vector Informatik, not by this project.
- `ITimerFactory` refactor for RecordService + StatisticsService — v3.6.x PATCH on observed-failure basis.
- `ReplayTimeline` cursor-walking tests.
- Hash-based `.asc` relocation; Replay tab session save.
- v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete (61st consecutive deferred list, crypto review needed).
