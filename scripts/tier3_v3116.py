#!/usr/bin/env python3
"""Tier 3 ship for v3.11.6 PATCH — Trace Viewer master-radio XAML parse exception.

Pattern: 1-commit overlay on the v3.11.5 force-updated parent.

- Parent SHA = 11f5b84fa9878acb9fc755f141ebe5066fcc5a2b (v3.11.5 force-updated on origin/main)
- Overlay = 1 commit from feature/v3-11-1-patch (local 91c452f)
  - 5 tree entries: 3 added/modified (TraceViewerView.xaml + App.xaml + SourceIdEqualsMasterConverter.cs) + 1 deleted (MasterRadioConverter.cs) + 1 new test (TraceViewerViewXamlTests.cs)
- Result: new commit on origin/main + new tag v3.11.6 + new GitHub release
"""
import base64
import json
import subprocess
import sys
from pathlib import Path

REPO = "jasontaotao/peakcan-host"
PARENT_SHA = "11f5b84fa9878acb9fc755f141ebe5066fcc5a2b"  # v3.11.5 force-updated on origin/main

REPO_ROOT = Path(r"D:\claude_proj2\peakcan-host")

ADDED_OR_MODIFIED = [
    # M7: Master-radio XAML antipattern fix
    "src/PeakCan.Host.App/Views/TraceViewerView.xaml",
    "src/PeakCan.Host.App/App.xaml",
    "src/PeakCan.Host.App/Composition/Converters/SourceIdEqualsMasterConverter.cs",
    "tests/PeakCan.Host.App.Tests/Views/TraceViewerViewXamlTests.cs",
    # Release notes
    "docs/release-notes-v3.11.6.md",
]


def gh(method, path, data=None):
    cmd = ["gh", "api", "-X", method, f"repos/{REPO}/{path}"]
    if data is not None:
        cmd.extend(["--input", "-"])
    res = subprocess.run(cmd, input=json.dumps(data) if data else None,
                         capture_output=True, text=True, encoding="utf-8")
    if res.returncode != 0:
        # Allow 404 on GETs (e.g. tag ref check) — caller may want to fall through
        if method == "GET" and "Not Found" in res.stderr:
            return None
        print(f"FAIL gh {method} {path}", file=sys.stderr)
        print(res.stderr, file=sys.stderr)
        sys.exit(1)
    if not res.stdout.strip():
        return {}
    return json.loads(res.stdout)


def main():
    parent_commit = gh("GET", f"git/commits/{PARENT_SHA}")
    parent_tree_sha = parent_commit["tree"]["sha"]
    assert len(parent_tree_sha) == 40
    print(f"  parent       {PARENT_SHA}")
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

    commit_msg = "v3.11.6 PATCH: Trace Viewer master-radio XAML uses MultiBinding (M7)"
    commit_result = gh("POST", "git/commits",
        {"message": commit_msg, "parents": [PARENT_SHA], "tree": new_tree_sha})
    new_commit_sha = commit_result["sha"]
    assert len(new_commit_sha) == 40
    print(f"  commit {new_commit_sha}")

    gh("PATCH", "git/refs/heads/main", {"sha": new_commit_sha, "force": True})
    print(f"  refs/heads/main -> {new_commit_sha} (force)")

    tag_result = gh("POST", "git/tags",
        {"tag": "v3.11.6", "message": commit_msg, "object": new_commit_sha, "type": "commit"})
    tag_sha = tag_result["sha"]
    assert len(tag_sha) == 40
    print(f"  tag    {tag_sha}  v3.11.6")

    # v3.8.8 lesson: tag ref may already exist (idempotent ship). Force-update.
    existing = gh("GET", "git/refs/tags/v3.11.6")
    if existing:
        gh("PATCH", "git/refs/tags/v3.11.6", {"sha": tag_sha, "force": True})
        print(f"  refs/tags/v3.11.6 -> {tag_sha} (force)")
    else:
        gh("POST", "git/refs", {"ref": "refs/tags/v3.11.6", "sha": tag_sha})
        print(f"  refs/tags/v3.11.6 -> {tag_sha}")

    release_notes = (REPO_ROOT / "docs" / "release-notes-v3.11.6.md").read_text(encoding="utf-8")
    release_result = gh("POST", "releases",
        {"tag_name": "v3.11.6", "name": "v3.11.6 PATCH: Trace Viewer master-radio XAML uses MultiBinding (M7)",
         "body": release_notes, "draft": False, "prerelease": False})
    release_url = release_result.get("html_url", "")
    print(f"  release {release_url}")

    print("\n=== TIER 3 SHIP COMPLETE ===")
    print(f"  parent  : {PARENT_SHA}")
    print(f"  new     : {new_commit_sha}")
    print(f"  tag     : v3.11.6  ({tag_sha})")
    print(f"  release : {release_url}")


if __name__ == "__main__":
    main()