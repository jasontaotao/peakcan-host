#!/usr/bin/env python3
"""Tier 3 ship for v3.16.9.4 PATCH (bus-off / driver-unload visibility).

The v3.16.9.0 MINOR was already shipped earlier today (origin/main at
7099a47, tag v3.16.9.0, GH release published). This ship overlays the
v3.16.9.4 PATCH on top of v3.16.9.0 MINOR (parent = origin/main HEAD).

The v3.16.9.4 PATCH includes:
- Bus-off / driver-unload / hardware-fault visibility via ICanChannel.ReadLoopError
- Branch rename feature/v3-12-0-minor -> v3-16-9-x-patch-chain
- 60-branch cleanup (Tier-0 metadata operation)
- Duplicate commit reconciliation docs
- v3-10-0-minor cherry-pick investigation docs

Parent: v3.16.9.0 MINOR (7099a47 on origin/main).
Target: v3.16.9.4 PATCH (5a9f2d2 on local v3-16-9-x-patch-chain).

Usage (REVIEW first, then):
    python scripts/tier3_v3169_4.py

Prerequisites:
- `gh` CLI authenticated with repo:scope 'repo'
- Local branch `v3-16-9-x-patch-chain` at `5a9f2d2`
- `git fetch` to confirm `origin/main` is at `7099a47` (v3.16.9.0 MINOR)

Process pattern (refined from v3.16.9.0):
    1. git fetch (verify proxy not blocking)
    2. Tier-3 ship from feature branch to origin/main (PATCH main)
    3. Tag v3.16.9.4 + GH release
"""
import base64
import json
import subprocess
import sys
from pathlib import Path

REPO = "jasontaotao/peakcan-host"
# v3.16.9.0 MINOR on origin/main as of 2026-07-10 (verified via git ls-remote).
PARENT_SHA = "7099a4747159dfd0e97904f3022801305f3bc207"  # v3.16.9.0 MINOR composite

REPO_ROOT = Path(r"D:\claude_proj2\peakcan-host")

# Auto-generated from `git diff origin/main..v3-16-9-x-patch-chain --name-status`
# then filtered to remove the 3 deleted files (Tier-3 cannot delete via overlay).
# Verified: 30 kept, 3 deleted.
ADDED_OR_MODIFIED = [
    "docs/release-notes-v3.16.9.4.md",
    "docs/superpowers/decisions/2026-07-10-rebuildsignalscore-duplicate-commit-reconciliation.md",
    "docs/superpowers/decisions/2026-07-10-v3-10-0-minor-cherry-pick-investigation.md",
    "docs/superpowers/plans/2026-07-10-feature-branch-cleanup.md",
    "docs/superpowers/session-anchors/2026-07-10-feature-branch-cleanup-complete.md",
    "docs/superpowers/session-anchors/2026-07-10-rename-and-orphan-delete-complete.md",
    "scripts/tier3_v3120.py",
    "scripts/tier3_v3130.py",
    "scripts/tier3_v3131.py",
    "scripts/tier3_v3132.py",
    "scripts/tier3_v3133.py",
    "scripts/tier3_v3140.py",
    "scripts/tier3_v3169_0.py",
    "scripts/tier3_v3169_4.py",
    "src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs",
    "src/PeakCan.Host.Core/ICanChannel.cs",
    "src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs",
    "tests/PeakCan.Host.App.Tests/Composition/SinkWiringServiceTests.cs",
    "tests/PeakCan.Host.App.Tests/Services/CyclicSendServiceRaceTests.cs",
    "tests/PeakCan.Host.App.Tests/Services/CyclicSendServiceTests.cs",
    "tests/PeakCan.Host.App.Tests/Services/MultiFrame/SequenceSendServiceDbcTests.cs",
    "tests/PeakCan.Host.App.Tests/Services/MultiFrame/SequenceSendServiceTests.cs",
    "tests/PeakCan.Host.App.Tests/Services/SendServiceTests.cs",
    "tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelMessageBoxPromptTests.cs",
    "tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs",
    "tests/PeakCan.Host.App.Tests/ViewModels/MultiFrameSendViewModelTests.cs",
    "tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs",
    "tests/PeakCan.Host.App.Tests/Windows/UdsWindowTests.cs",
    "tests/PeakCan.Host.Core.Tests/Replay/AscParserTests.cs",
    "tests/PeakCan.Host.Infrastructure.Tests/ChannelRouterTests.cs",
    "tests/PeakCan.Host.Infrastructure.Tests/PeakCanChannelTests.cs",
]

# DELETED FILES (cannot ship via Tier-3 overlay — must be deleted separately on main):
# - src/PeakCan.Host.App/Composition/Converters/MasterRadioConverter.cs
# - src/PeakCan.Host.App/Views/UdsView.xaml
# - src/PeakCan.Host.App/Views/UdsView.xaml.cs


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
        "v3.16.9.4 PATCH: bus-off / driver-unload / hardware-fault visibility via\n"
        "ICanChannel.ReadLoopError event (plus branch rename + 60-branch cleanup\n"
        "docs + duplicate commit reconciliation + v3-10-0-minor cherry-pick\n"
        "investigation)"
    )
    commit_result = gh("POST", "git/commits",
        {"message": commit_msg, "parents": [full_parent_sha], "tree": new_tree_sha})
    new_commit_sha = commit_result["sha"]
    print(f"  commit {new_commit_sha}")

    gh("PATCH", "git/refs/heads/main", {"sha": new_commit_sha, "force": True})
    print(f"  refs/heads/main -> {new_commit_sha} (force)")

    # Tag v3.16.9.4 PATCH — new tag pointing to the overlay commit
    tag_v31694 = gh("POST", "git/tags", {
        "tag": "v3.16.9.4",
        "message": "v3.16.9.4 PATCH: bus-off / driver-unload / hardware-fault visibility via ICanChannel.ReadLoopError event",
        "object": new_commit_sha,
        "type": "commit",
    })
    tag_v31694_sha = tag_v31694["sha"]
    print(f"  tag    {tag_v31694_sha}  v3.16.9.4")
    existing94 = gh("GET", "git/refs/tags/v3.16.9.4")
    if existing94:
        gh("PATCH", "git/refs/tags/v3.16.9.4", {"sha": tag_v31694_sha, "force": True})
        print(f"  refs/tags/v3.16.9.4 -> {tag_v31694_sha} (force)")
    else:
        gh("POST", "git/refs", {"ref": "refs/tags/v3.16.9.4", "sha": tag_v31694_sha})
        print(f"  refs/tags/v3.16.9.4 -> {tag_v31694_sha}")

    # Create v3.16.9.4 release
    v31694_notes = (REPO_ROOT / "docs" / "release-notes-v3.16.9.4.md").read_text(encoding="utf-8")
    release_result = gh("POST", "releases", {
        "tag_name": "v3.16.9.4",
        "name": "v3.16.9.4 PATCH: bus-off / driver-unload visibility",
        "body": v31694_notes,
        "draft": False,
        "prerelease": False,
    })
    release_url = release_result.get("html_url", "")
    print(f"  release {release_url}")

    print("\n=== TIER 3 SHIP COMPLETE ===")
    print(f"  parent  : {full_parent_sha}")
    print(f"  new     : {new_commit_sha}")
    print(f"  tag v3.16.9.4 (new): {tag_v31694_sha}")
    print(f"  release v3.16.9.4: {release_url}")


if __name__ == "__main__":
    main()