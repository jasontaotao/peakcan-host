#!/usr/bin/env python3
"""Tier 3 ship for v3.13.3 PATCH — window title version pinned via Directory.Build.props.

Pattern: 1-commit overlay on the v3.13.2 PATCH ship parent on origin/main.

- Parent SHA = 0353dc8ce7f89f965f274b3e9b656accf21fed8c (v3.13.2 PATCH ship commit on origin/main)
- Overlay = 1 commit from feature/v3-12-0-minor (F6 fix)
  - 1 NEW file: src/Directory.Build.props (pins Version=3.13.3 + AssemblyVersion/FileVersion/InformationalVersion)
  - 1 MODIFIED file: src/PeakCan.Host.App/PeakCan.Host.App.csproj (comment explaining the version source)
  - 1 NEW file: docs/release-notes-v3.13.3.md (release body for the GitHub release)
- Result: new commit on origin/main + new tag v3.13.3 + new GitHub release
"""
import base64
import json
import subprocess
import sys
from pathlib import Path

REPO = "jasontaotao/peakcan-host"
PARENT_SHA = "0353dc8ce7f89f965f274b3e9b656accf21fed8c"  # v3.13.2 PATCH ship commit on origin/main

REPO_ROOT = Path(r"D:\claude_proj2\peakcan-host")

# F6 overlay files (all added or modified in the F6 commit on top of the v3.13.2 ship parent)
ADDED_OR_MODIFIED = [
    "src/Directory.Build.props",                              # NEW — pins Version=3.13.3
    "src/PeakCan.Host.App/PeakCan.Host.App.csproj",           # MODIFIED — xmldoc comment
    "docs/release-notes-v3.13.3.md",                          # NEW — release body
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
    print(f"  files to ship: {ADDED_OR_MODIFIED}")
    parent_commit = gh("GET", f"git/commits/{PARENT_SHA}")
    if parent_commit is None:
        print(f"FAIL: cannot resolve PARENT_SHA {PARENT_SHA}", file=sys.stderr)
        sys.exit(1)
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

    commit_msg = "v3.13.3 PATCH: window title version pinned via Directory.Build.props (F6)"
    commit_result = gh("POST", "git/commits",
        {"message": commit_msg, "parents": [full_parent_sha], "tree": new_tree_sha})
    new_commit_sha = commit_result["sha"]
    assert len(new_commit_sha) == 40
    print(f"  commit {new_commit_sha}")

    gh("PATCH", "git/refs/heads/main", {"sha": new_commit_sha, "force": True})
    print(f"  refs/heads/main -> {new_commit_sha} (force)")

    tag_result = gh("POST", "git/tags",
        {"tag": "v3.13.3", "message": commit_msg, "object": new_commit_sha, "type": "commit"})
    tag_sha = tag_result["sha"]
    assert len(tag_sha) == 40
    print(f"  tag    {tag_sha}  v3.13.3")

    existing = gh("GET", "git/refs/tags/v3.13.3")
    if existing:
        gh("PATCH", "git/refs/tags/v3.13.3", {"sha": tag_sha, "force": True})
        print(f"  refs/tags/v3.13.3 -> {tag_sha} (force)")
    else:
        gh("POST", "git/refs", {"ref": "refs/tags/v3.13.3", "sha": tag_sha})
        print(f"  refs/tags/v3.13.3 -> {tag_sha}")

    release_notes = (REPO_ROOT / "docs" / "release-notes-v3.13.3.md").read_text(encoding="utf-8")
    release_result = gh("POST", "releases",
        {"tag_name": "v3.13.3",
         "name": "v3.13.3 PATCH: window title version pinned via Directory.Build.props",
         "body": release_notes, "draft": False, "prerelease": False})
    release_url = release_result.get("html_url", "")
    print(f"  release {release_url}")

    print("\n=== TIER 3 SHIP COMPLETE ===")
    print(f"  parent  : {full_parent_sha}")
    print(f"  new     : {new_commit_sha}")
    print(f"  tag     : v3.13.3  ({tag_sha})")
    print(f"  release : {release_url}")


if __name__ == "__main__":
    main()