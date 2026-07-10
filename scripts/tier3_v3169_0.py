#!/usr/bin/env python3
"""Tier 3 ship for v3.16.9.0 MINOR — composite trace-viewer overhaul.

Combines 4 PATCHes (X-axis wall-clock + LineSeries markers + reverse-
trigger guard + SetSpeed reorder) + 5 test migrations into a single
MINOR release. Parent: v3.16.8 hotfix (`d07cb77` on origin/main).

This script is the TEMPLATE; it must be reviewed and adjusted before
running. Specifically, the `ADDED_OR_MODIFIED` list must match the
actual diff between `5b19838` and `d07cb77` (or whichever parent
commit the Tier 3 ship starts from).

Usage (REVIEW first, then):
    python scripts/tier3_v3169_0.py

Prerequisites:
- `gh` CLI authenticated with repo:scope 'repo'
- Local branch `v3-16-9-x-patch-chain` at `5b19838`
- `git fetch` to confirm `origin/main` is at `d07cb77` (v3.16.8 hotfix)

Process pattern (CONFIRMED 2nd time today):
    1. git fetch (verify proxy not blocking)
    2. git merge origin/main (if any v3.16.8.x on main not in feature)
    3. Tier-3 ship from feature branch to origin/main
    4. git push to confirm overlay commit
    5. Tag v3.16.9.0 + GH release
"""
import base64
import json
import subprocess
import sys
from pathlib import Path

REPO = "jasontaotao/peakcan-host"
# v3.16.8 hotfix on origin/main as of 2026-07-10.
PARENT_SHA = "d07cb7763ab58f3edf3259d884c323e9b07fcf96"  # v3.16.8 hotfix (Directory.Build.props bump)

REPO_ROOT = Path(r"D:\claude_proj2\peakcan-host")

# Fill in from `git diff d07cb77..5b19838 --name-only` after merge.
# This list must be verified before running the script.
ADDED_OR_MODIFIED = [
    "appsettings.json",
    "docs/play-architecture.html",
    "docs/release-notes-v3.11.1.md",
    "docs/release-notes-v3.14.1.md",
    "docs/release-notes-v3.14.2.md",
    "docs/release-notes-v3.16.9.0.md",
    "docs/release-notes-v3.16.9.2.md",
    "docs/release-notes-v3.16.9.3.md",
    "docs/superpowers/plans/2026-07-07-v3-11-3-patch-uds-window.md",
    "docs/superpowers/plans/2026-07-07-v3-11-4-patch-trace-viewer-add-trace.md",
    "docs/superpowers/plans/2026-07-07-v3-11-5-patch-canoe-asc-parser.md",
    "docs/superpowers/plans/2026-07-07-v3-11-6-patch-trace-viewer-master-radio.md",
    "docs/superpowers/plans/2026-07-07-v3-12-0-minor-replay-vm-split-and-backlog.md",
    "docs/superpowers/plans/2026-07-08-v3-14-0-minor-code-review-bug-bash.md",
    "docs/superpowers/plans/2026-07-09-trace-viewer-enhancements.md",
    "docs/superpowers/plans/2026-07-10-trace-viewer-enhancements-remaining.md",
    "docs/superpowers/session-anchors/2026-07-10-phase-d-push-and-status.md",
    "docs/superpowers/session-anchors/2026-07-10-v3-5-to-v3-16-status-anchor.md",
    "docs/superpowers/specs/2026-07-09-trace-viewer-enhancements-design.md",
    "scripts/tier3_v3111.py",
    "scripts/tier3_v3112.py",
    "scripts/tier3_v3113.py",
    "scripts/tier3_v3114.py",
    "scripts/tier3_v3115.py",
    "scripts/tier3_v3116.py",
    "scripts/tier3_v3117.py",
    "scripts/tier3_v3120.py",
    "scripts/tier3_v3130.py",
    "scripts/tier3_v3131.py",
    "scripts/tier3_v3132.py",
    "scripts/tier3_v3133.py",
    "scripts/tier3_v3140_reship.py",
    "scripts/tier3_v3141.py",
    "scripts/tier3_v3142.py",
    "scripts/tier3_v3143.py",
    "scripts/tier3_v3150.py",
    "scripts/tier3_v3160.py",
    "scripts/tier3_v3161.py",
    "scripts/tier3_v3162.py",
    "scripts/tier3_v3163.py",
    "scripts/tier3_v3164.py",
    "scripts/tier3_v3165.py",
    "scripts/tier3_v3166.py",
    "scripts/tier3_v3167.py",
    "scripts/tier3_v3167_1.py",
    "scripts/tier3_v3168.py",
    "scripts/tier3_v3168_hotfix.py",
    "scripts/tier3_v3169_0.py",
    "src/Directory.Build.props",
    "src/PeakCan.Host.App/Composition/AppHostBuilder.cs",
    "src/PeakCan.Host.App/Services/Trace/ReplaySessionAutoSaver.cs",
    "src/PeakCan.Host.App/Services/Trace/TraceSessionAutoSaver.cs",
    "src/PeakCan.Host.App/Services/Trace/TraceSessionSnapshotBuilder.cs",
    "src/PeakCan.Host.App/Services/Trace/TraceSource.cs",
    "src/PeakCan.Host.App/ViewModels/TraceChartSeries.cs",
    "src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs",
    "src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs",
    "src/PeakCan.Host.Core/Dbc/DbcParser.cs",
    "src/PeakCan.Host.Core/Replay/AscParseResult.cs",
    "src/PeakCan.Host.Core/Replay/AscParser.cs",
    "src/PeakCan.Host.Core/Replay/ReplayTimeline.cs",
    "src/PeakCan.Host.Core/Replay/TraceViewerService.cs",
    "tests/PeakCan.Host.App.Tests/Services/Trace/TraceSessionSnapshotBuilderTests.cs",
    "tests/PeakCan.Host.App.Tests/ViewModels/TraceChartViewModelTests.cs",
    "tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelMultiTraceTests.cs",
    "tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs",
    "tests/PeakCan.Host.Core.Tests/DbcParserTests.cs",
    "tests/PeakCan.Host.Core.Tests/E51PtBmsSpecificValuesTests.cs",
    "tests/PeakCan.Host.Core.Tests/Replay/AscParserTests.cs",
    "tests/PeakCan.Host.Core.Tests/Replay/ReplayTimelineTests.cs",
    "tests/PeakCan.Host.Core.Tests/Replay/TraceViewerServiceTests.cs",
    "tools/smoke-diag/Program.cs",
    "tools/smoke-diag/smoke-diag.csproj",
]


def gh(method, path, data=None):
    cmd = ["gh", "api", "-X", method, f"repos/{REPO}/{path}"]
    if data is not None:
        cmd.extend(["--input", "-"])
    res = subprocess.run(cmd, input=json.dumps(data) if data else None,
                         capture_output=True, text=True, encoding="utf-8")
    if res.returncode != 0:
        if method == "GET" and "Not Found" in res.stderr:
            return None
        print(f"FAIL gh {method} {path}", file=sys.stderr)
        print(res.stderr, file=sys.stderr)
        sys.exit(1)
    if not res.stdout.strip():
        return {}
    return json.loads(res.stdout)


def main():
    print(f"  files to ship: {len(ADDED_OR_MODIFIED)}")
    parent_commit = gh("GET", f"git/commits/{PARENT_SHA}")
    if parent_commit is None:
        print(f"FAIL: cannot resolve {PARENT_SHA}", file=sys.stderr)
        sys.exit(1)
    full_parent_sha = parent_commit["sha"]
    parent_tree_sha = parent_commit["tree"]["sha"]
    print(f"  parent       {full_parent_sha}")
    print(f"  parent tree  {parent_tree_sha}")

    overlays = []
    for relpath in ADDED_OR_MODIFIED:
        full = REPO_ROOT / relpath
        if not full.exists():
            print(f"  MISSING: {relpath}", file=sys.stderr)
            sys.exit(1)
        content = full.read_bytes().replace(b"\r\n", b"\n")
        result = gh("POST", "git/blobs",
            {"content": base64.b64encode(content).decode("ascii"), "encoding": "base64"})
        sha = result["sha"]
        overlays.append({"path": relpath, "mode": "100644", "type": "blob", "sha": sha})
        print(f"  blob   {sha}  {relpath}  ({len(content)} bytes)")

    tree_result = gh("POST", "git/trees",
        {"base_tree": parent_tree_sha, "tree": overlays})
    new_tree_sha = tree_result["sha"]
    print(f"  tree  {new_tree_sha}")

    commit_msg = (
        "v3.16.9.0 MINOR: composite trace-viewer overhaul (X-axis wall-clock + "
        "LineSeries markers + reverse-trigger guard + SetSpeed reorder + test migration)"
    )
    commit_result = gh("POST", "git/commits",
        {"message": commit_msg, "parents": [full_parent_sha], "tree": new_tree_sha})
    new_commit_sha = commit_result["sha"]
    print(f"  commit {new_commit_sha}")

    gh("PATCH", "git/refs/heads/main", {"sha": new_commit_sha, "force": True})
    print(f"  refs/heads/main -> {new_commit_sha} (force)")

    # Tag v3.16.9.0 as annotated tag
    tag_object = gh("POST", "git/tags", {
        "tag": "v3.16.9.0",
        "message": "v3.16.9.0 MINOR: composite trace-viewer overhaul",
        "object": new_commit_sha,
        "type": "commit",
    })
    tag_sha = tag_object["sha"]
    print(f"  tag    {tag_sha}  v3.16.9.0")

    existing = gh("GET", "git/refs/tags/v3.16.9.0")
    if existing:
        gh("PATCH", "git/refs/tags/v3.16.9.0", {"sha": tag_sha, "force": True})
        print(f"  refs/tags/v3.16.9.0 -> {tag_sha} (force)")
    else:
        gh("POST", "git/refs", {"ref": "refs/tags/v3.16.9.0", "sha": tag_sha})
        print(f"  refs/tags/v3.16.9.0 -> {tag_sha}")

    release_notes = (REPO_ROOT / "docs" / "release-notes-v3.16.9.0.md").read_text(encoding="utf-8")
    release_result = gh("POST", "releases", {
        "tag_name": "v3.16.9.0",
        "name": "v3.16.9.0 MINOR: composite trace-viewer overhaul",
        "body": release_notes,
        "draft": False,
        "prerelease": False,
    })
    release_url = release_result.get("html_url", "")
    print(f"  release {release_url}")

    print("\n=== TIER 3 SHIP COMPLETE ===")
    print(f"  parent  : {full_parent_sha}")
    print(f"  new     : {new_commit_sha}")
    print(f"  tag     : v3.16.9.0  ({tag_sha})")
    print(f"  release : {release_url}")


if __name__ == "__main__":
    main()
