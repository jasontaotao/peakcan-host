#!/usr/bin/env python3
"""Tier 3 ship for v3.14.0 MINOR — code-review bug bash (7 HIGH fixes).

Pattern: 1-commit overlay on the v3.13.3 PATCH ship parent on origin/main.

- Parent SHA = 1b3f57608f4c105523692d1a17e50d5fec96af34 (v3.13.3 PATCH ship commit on origin/main)
- Overlay = 4 task commits from v3-16-9-x-patch-chain (local 2e9f173)
  - U1 A1: SignalDecoder 64-bit signed (commit f778203)
  - U2 A5: ChannelRouter DetachSink catch (commit 62df020)
  - U3 A2+A3+A4: Dispose audit (commit fa647cf)
  - U4 A6+A7: Replay service timer + loop region (commit 2e9f173)
- Result: new commit on origin/main + new tag v3.14.0 + new GitHub release
"""
import base64
import json
import subprocess
import sys
from pathlib import Path

REPO = "jasontaotao/peakcan-host"
PARENT_SHA = "1b3f57608f4c105523692d1a17e50d5fec96af34"  # v3.13.3 PATCH ship commit on origin/main

REPO_ROOT = Path(r"D:\claude_proj2\peakcan-host")

# All files modified by U1-U4 + this docs/ship commit. Auto-discover from
# the U1-U4 diff range so future unit additions don't need a script edit.
def get_diff_files():
    res = subprocess.run(
        ["git", "diff", "--name-only", PARENT_SHA, "HEAD"],
        capture_output=True, text=True, cwd=REPO_ROOT)
    return [line.strip() for line in res.stdout.splitlines() if line.strip() and not line.endswith(".py")]

ADDED_OR_MODIFIED = get_diff_files() + ["docs/release-notes-v3.14.0.md"]


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
    print(f"  files to ship: {len(ADDED_OR_MODIFIED)} entries")
    parent_commit = gh("GET", f"git/commits/{PARENT_SHA}")
    if parent_commit is None:
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

    commit_msg = "v3.14.0 MINOR: code-review bug bash — 7 HIGH fixes (A1 SignalDecoder, A2/A3/A4 Dispose leaks, A5 ChannelRouter, A6 ReplayService timer, A7 LoopRegion validation)"
    commit_result = gh("POST", "git/commits",
        {"message": commit_msg, "parents": [full_parent_sha], "tree": new_tree_sha})
    new_commit_sha = commit_result["sha"]
    assert len(new_commit_sha) == 40
    print(f"  commit {new_commit_sha}")

    gh("PATCH", "git/refs/heads/main", {"sha": new_commit_sha, "force": True})
    print(f"  refs/heads/main -> {new_commit_sha} (force)")

    tag_result = gh("POST", "git/tags",
        {"tag": "v3.14.0", "message": commit_msg, "object": new_commit_sha, "type": "commit"})
    tag_sha = tag_result["sha"]
    assert len(tag_sha) == 40
    print(f"  tag    {tag_sha}  v3.14.0")

    existing = gh("GET", "git/refs/tags/v3.14.0")
    if existing:
        gh("PATCH", "git/refs/tags/v3.14.0", {"sha": tag_sha, "force": True})
        print(f"  refs/tags/v3.14.0 -> {tag_sha} (force)")
    else:
        gh("POST", "git/refs", {"ref": "refs/tags/v3.14.0", "sha": tag_sha})
        print(f"  refs/tags/v3.14.0 -> {tag_sha}")

    release_notes = (REPO_ROOT / "docs" / "release-notes-v3.14.0.md").read_text(encoding="utf-8")
    release_result = gh("POST", "releases",
        {"tag_name": "v3.14.0", "name": "v3.14.0 MINOR: code-review bug bash — 7 HIGH fixes",
         "body": release_notes, "draft": False, "prerelease": False})
    release_url = release_result.get("html_url", "")
    print(f"  release {release_url}")

    print("\n=== TIER 3 SHIP COMPLETE ===")
    print(f"  parent  : {full_parent_sha}")
    print(f"  new     : {new_commit_sha}")
    print(f"  tag     : v3.14.0  ({tag_sha})")
    print(f"  release : {release_url}")


if __name__ == "__main__":
    main()