#!/usr/bin/env python3
"""Tier 3 ship for v3.12.0 MINOR — ReplayViewModel god class split + project-wide WPF converter STA smoke matrix.

Pattern: 1-commit overlay on the v3.11.7 PATCH ship parent on origin/main.

- Parent SHA = 23da0505... (v3.11.7 PATCH ship commit on origin/main)
- Overlay = 1 commit from feature/v3-12-0-minor (local c70234c)
  - C2 ReplayViewModel partial split (commit 02c03ca) — 5 prod files
  - M3 converter smoke tests (commits b691e44 + c70234c) — 1 test file
  - H1 LoopRewound contract test (commit 1a6e0e4) — 1 test file
  - L1 ReplayException xmldoc (commit 455b61d) — 1 prod file (PLURAL ReplayExceptions.cs)
  - Release notes (this commit)
- Result: new commit on origin/main + new tag v3.12.0 + new GitHub release
"""
import base64
import json
import subprocess
import sys
from pathlib import Path

REPO = "jasontaotao/peakcan-host"
PARENT_SHA = "23da050523d8d22947c7c1e2b40019d561004ff5"  # v3.11.7 PATCH ship commit on origin/main (resolved via gh api .../commits?sha=v3.11.7)

REPO_ROOT = Path(r"D:\claude_proj2\peakcan-host")

ADDED_OR_MODIFIED = [
    # C2 ReplayViewModel split (commit 02c03ca)
    "src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs",
    "src/PeakCan.Host.App/ViewModels/ReplayViewModel.Loader.partial.cs",
    "src/PeakCan.Host.App/ViewModels/ReplayViewModel.Playback.partial.cs",
    "src/PeakCan.Host.App/ViewModels/ReplayViewModel.Bookmarks.partial.cs",
    "src/PeakCan.Host.App/ViewModels/ReplayViewModel.Bundle.partial.cs",
    "tests/PeakCan.Host.App.Tests/ViewModels/ReplayViewModelPartialSplitTests.cs",
    # M3 converter smoke tests (commits b691e44 + c70234c)
    "tests/PeakCan.Host.App.Tests/Composition/ConverterSmokeTests.cs",
    # H1 LoopRewound contract (commit 1a6e0e4)
    "tests/PeakCan.Host.Core.Tests/Replay/IReplayServiceLoopRewoundContractTests.cs",
    # L1 docs (commit 455b61d) -- NOTE: file is PLURAL "ReplayExceptions.cs"
    "src/PeakCan.Host.Core/Replay/ReplayExceptions.cs",
    # Release notes (this commit)
    "docs/release-notes-v3.12.0.md",
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

    commit_msg = "v3.12.0 MINOR: ReplayViewModel god class split + project-wide converter STA smoke matrix + H1/L1 closure"
    commit_result = gh("POST", "git/commits",
        {"message": commit_msg, "parents": [full_parent_sha], "tree": new_tree_sha})
    new_commit_sha = commit_result["sha"]
    assert len(new_commit_sha) == 40
    print(f"  commit {new_commit_sha}")

    gh("PATCH", "git/refs/heads/main", {"sha": new_commit_sha, "force": True})
    print(f"  refs/heads/main -> {new_commit_sha} (force)")

    tag_result = gh("POST", "git/tags",
        {"tag": "v3.12.0", "message": commit_msg, "object": new_commit_sha, "type": "commit"})
    tag_sha = tag_result["sha"]
    assert len(tag_sha) == 40
    print(f"  tag    {tag_sha}  v3.12.0")

    # v3.8.8 lesson: tag ref may already exist (idempotent ship). Force-update.
    existing = gh("GET", "git/refs/tags/v3.12.0")
    if existing:
        gh("PATCH", "git/refs/tags/v3.12.0", {"sha": tag_sha, "force": True})
        print(f"  refs/tags/v3.12.0 -> {tag_sha} (force)")
    else:
        gh("POST", "git/refs", {"ref": "refs/tags/v3.12.0", "sha": tag_sha})
        print(f"  refs/tags/v3.12.0 -> {tag_sha}")

    release_notes = (REPO_ROOT / "docs" / "release-notes-v3.12.0.md").read_text(encoding="utf-8")
    release_result = gh("POST", "releases",
        {"tag_name": "v3.12.0", "name": "v3.12.0 MINOR: ReplayViewModel god class split + project-wide converter STA smoke matrix",
         "body": release_notes, "draft": False, "prerelease": False})
    release_url = release_result.get("html_url", "")
    print(f"  release {release_url}")

    print("\n=== TIER 3 SHIP COMPLETE ===")
    print(f"  parent  : {full_parent_sha}")
    print(f"  new     : {new_commit_sha}")
    print(f"  tag     : v3.12.0  ({tag_sha})")
    print(f"  release : {release_url}")


if __name__ == "__main__":
    main()