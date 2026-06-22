# PeakCan-Host Multi-Agent Code Review

## Overview
4 parallel review agents (by architecture layer) + 1 cross-verification agent.

## Layers
1. **Core** — PeakCan.Host.Core (domain models, DBC parser/decoder, Result/Error)
2. **Infrastructure** — PeakCan.Host.Infrastructure (Peak CAN channel, routing, statistics)
3. **App Services+VMs** — Services + ViewModels + Composition
4. **App Views+UI** — XAML Views, DispatcherExtensions, App.xaml

## Cross-Verification
After all 4 agents complete, a verifier agent reads all findings, checks for:
- Duplicate findings across agents (merge)
- False positives (agent misread code)
- Contradictory assessments (one says bug, another says fine)
- Missing coverage (layer not reviewed)
