#!/usr/bin/env python3
"""Tier 3 ship for v3.13.0 PATCH — Trace Viewer Unexpected error + reopen state + dead DBC button.

Pattern: 1-commit overlay on the v3.12.0 MINOR ship parent on origin/main.

- Parent SHA = 8b911eefb1182da2fa31c8104cf6a3d93be54781 (v3.12.0 MINOR ship commit on origin/main)
- Overlay = 3 commits from v3-16-9-x-patch-chain (local cc19bf4)
  - F1 include exception type in Unexpected error (1e3cd2f) — 1 prod file
  - F2 reset VM mutable state on Trace Viewer window close (327bae9) — 2 prod files
  - F3 remove dead Load DBC button + LoadDbcAsync (cc19bf4) — 3 prod files + test adaptations
- Result: new commit on origin/main + new tag v3.13.0 + new GitHub release
"""
import base64
import json
import subprocess
import sys
from pathlib import Path

REPO = "jasontaotao/peakcan-host"
PARENT_SHA = "8b911eefb1182da2fa31c8104cf6a3d93be54781"  # v3.12.0 MINOR ship commit on origin/main

REPO_ROOT = Path(r"D:\claude_proj2\peakcan-host")

ADDED_OR_MODIFIED = [
    # F1 (commit 1e3cd2f)
    "src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs",
    # F2 (commit 327bae9) — also TraceViewerViewModel.cs + AppShellViewModel.cs
    "src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs",
    # F3 (commit cc19bf4)
    "src/PeakCan.Host.App/Views/TraceViewerView.xaml",
    "src/PeakCan.Host.App/Views/TraceViewerView.xaml.cs",
    # Test adaptations for F2/F3 (visible in single squash below)
    "tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelCanIdFilterTests.cs",
    "tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelChartWiringTests.cs",
    "tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelRebuildSignalsTests.cs",
    "tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs",
    # Release notes (this commit)
    "docs/release-notes-v3.13.0.md",
]


def gh(method, path, data=None):
    cmd = ["gh", "api", "-X", method, f"repos/{REPO}/{path}"]
    if data is not None:
        cmd.extend(["--input", "-"])
    res = subprocess.run(cmd, input=json.dumps(data) if data else None,
                         capture_output=True, text=True, encoding="utf-8")
    if res.returncode != 0:
        # Allow 404 on GETs (e.g. tag ref check) -- caller may want to fall through
        if method == "GET" and "Not Found" in res.stderr:
            return None
        print(f"FAIL gh {method} {path}", file=sys.stderr)
        print(res.stderr, file=sys.stderr)
        sys.exit(1)
    if not res.stdout.strip():
        return {}
    return json.loads(res.stdout)


def main():
    # Resolve full PARENT_SHA if a short SHA was provided
    parent_commit = gh("GET", f"git/commits/{PARENT_SHA}")
    if parent_commit is None:
        print(f"  WARN: short SHA {PARENT_SHA} not resolvable via git/commits; "
              "assuming it's already the full 40-char SHA", file=sys.stderr)
        full_parent_sha = PARENT_SHA
        parent_commit = gh("GET", f"git/commits/{full_parent_sha}")
        if parent_commit is None:
            print(f"FAIL: cannot resolve PARENT_SHA {PARENT_SHA}", file=sys.stderr)
            sys.exit(1)
    else:
        full_parent_sha = parent_commit["sha"]
    parent_tree_sha = parent_commit["tree"]["sha"]
    assert len(parent_tree_sha) == 40
    assert len(full_parent_sha) == 40
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
        assert len(sha) == 40
        overlays.append({"path": relpath, "mode": "100644", "type": "blob", "sha": sha})
        print(f"  blob   {sha}  {relpath}  ({len(content)} bytes)")

    tree_result = gh("POST", "git/trees",
        {"base_tree": parent_tree_sha, "tree": overlays})
    new_tree_sha = tree_result["sha"]
    assert len(new_tree_sha) == 40
    print(f"\n  tree  {new_tree_sha}")

    commit_msg = "v3.13.0 PATCH: Trace Viewer Unexpected error debug + reopen state reset + dead DBC button removal"
    commit_result = gh("POST", "git/commits",
        {"message": commit_msg, "parents": [full_parent_sha], "tree": new_tree_sha})
    new_commit_sha = commit_result["sha"]
    assert len(new_commit_sha) == 40
    print(f"  commit {new_commit_sha}")

    gh("PATCH", "git/refs/heads/main", {"sha": new_commit_sha, "force": True})
    print(f"  refs/heads/main -> {new_commit_sha} (force)")

    tag_result = gh("POST", "git/tags",
        {"tag": "v3.13.0", "message": commit_msg, "object": new_commit_sha, "type": "commit"})
    tag_sha = tag_result["sha"]
    assert len(tag_sha) == 40
    print(f"  tag    {tag_sha}  v3.13.0")

    # v3.8.8 lesson: tag ref may already exist (idempotent ship). Force-update.
    existing = gh("GET", "git/refs/tags/v3.13.0")
    if existing:
        gh("PATCH", "git/refs/tags/v3.13.0", {"sha": tag_sha, "force": True})
        print(f"  refs/tags/v3.13.0 -> {tag_sha} (force)")
    else:
        gh("POST", "git/refs", {"ref": "refs/tags/v3.13.0", "sha": tag_sha})
        print(f"  refs/tags/v3.13.0 -> {tag_sha}")

    release_notes = (REPO_ROOT / "docs" / "release-notes-v3.13.0.md").read_text(encoding="utf-8")
    release_result = gh("POST", "releases",
        {"tag_name": "v3.13.0", "name": "v3.13.0 PATCH: Trace Viewer Unexpected error debug + reopen state reset + dead DBC button removal",
         "body": release_notes, "draft": False, "prerelease": False})
    release_url = release_result.get("html_url", "")
    print(f"  release {release_url}")

    print("\n=== TIER 3 SHIP COMPLETE ===")
    print(f"  parent  : {full_parent_sha}")
    print(f"  new     : {new_commit_sha}")
    print(f"  tag     : v3.13.0  ({tag_sha})")
    print(f"  release : {release_url}")


if __name__ == "__main__":
    main()