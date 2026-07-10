#!/usr/bin/env python3
"""Tier 3 ship for v3.16.9.5 PATCH + 3 file deletions.

The v3.16.9.4 Tier-3 ship (overlay commit 517df57) included all the source
files but could NOT delete files (Tier-3 only adds/modifies — it cannot
remove). 3 files in the v3.11.x..v3.16.x commit range were deleted
locally but remain on origin/main:

- src/PeakCan.Host.App/Composition/Converters/MasterRadioConverter.cs
- src/PeakCan.Host.App/Views/UdsView.xaml
- src/PeakCan.Host.App/Views/UdsView.xaml.cs

This script ships:
1. The v3.16.9.5 PATCH (ErrorCode.Ok mapping) — already covered by the
   `tier3_v3169_4.py` ADDED_OR_MODIFIED list since the mapper changes
   are in 3 source files. We re-run the FULL v3.16.9.5 overlay (without
   the v3.16.9.x commits that were already shipped) PLUS add a marker
   file whose content indicates "delete these 3 paths".
2. A separate gh API call to delete each file via the trees API.

Actually, the GitHub trees API supports `sha: null` for delete operations:
  {"path": "src/.../MasterRadioConverter.cs", "mode": "100644", "type": "blob", "sha": null}
This is the proper way to delete files in a Tier-3 overlay.

Usage (REVIEW first, then):
    python scripts/tier3_v3169_5_deletions.py

Prerequisites:
- `gh` CLI authenticated with repo:scope 'repo'
- Local branch `v3-16-9-x-patch-chain` at `db41590`
- `git fetch` to confirm `origin/main` is at `517df57` (v3.16.9.4 PATCH overlay)

Process pattern (refined from v3.16.9.4):
    1. git fetch (verify proxy not blocking)
    2. Tier-3 ship with DELETE entries for the 3 files
    3. Tag v3.16.9.5 + GH release
"""
import base64
import json
import subprocess
import sys
from pathlib import Path

REPO = "jasontaotao/peakcan-host"
# v3.16.9.4 PATCH overlay on origin/main as of 2026-07-10.
PARENT_SHA = "517df577f3a83f1201da751c9dcb7dde2cdef3d4"

REPO_ROOT = Path(r"D:\claude_proj2\peakcan-host")

# Auto-generated from `git diff origin/main..v3-16-9-x-patch-chain --name-status`
# (origin/main = v3.16.9.4 PATCH overlay at 517df57; local HEAD = v3.16.9.5 PATCH at db41590)
# Filtered: removed the 3 files already handled by the v3.16.9.4 overlay
# (commit 517df57 included them in its blob list since they existed at
# that point in history). The current diff origin/main..HEAD only has
# the 3 v3.16.9.5 PATCH source files + the 2 test changes.
ADDED_OR_MODIFIED = [
    "src/PeakCan.Host.Core/ErrorCode.cs",
    "src/PeakCan.Host.Infrastructure/Peak/PeakErrorMapper.cs",
    "tests/PeakCan.Host.Infrastructure.Tests/PeakErrorMapperTests.cs",
    "docs/release-notes-v3.16.9.5.md",
    "scripts/tier3_v3169_4.py",
    "scripts/tier3_v3169_5_deletions.py",
]

# Files to DELETE from origin/main (no blob upload; sha=null means "remove").
DELETED = [
    "src/PeakCan.Host.App/Composition/Converters/MasterRadioConverter.cs",
    "src/PeakCan.Host.App/Views/UdsView.xaml",
    "src/PeakCan.Host.App/Views/UdsView.xaml.cs",
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
    print(f"  files to add/modify: {len(ADDED_OR_MODIFIED)}")
    print(f"  files to delete   : {len(DELETED)}")
    parent_commit = gh("GET", f"git/commits/{PARENT_SHA}")
    if parent_commit is None:
        print(f"FAIL: cannot resolve {PARENT_SHA}", file=sys.stderr)
        sys.exit(1)
    full_parent_sha = parent_commit["sha"]
    parent_tree_sha = parent_commit["tree"]["sha"]
    print(f"  parent       {full_parent_sha}")
    print(f"  parent tree  {parent_tree_sha}")

    overlays = []
    # Adds + modifies
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
    # Deletes
    for relpath in DELETED:
        overlays.append({"path": relpath, "mode": "100644", "type": "blob", "sha": None})
        print(f"  delete        {relpath}")

    tree_result = gh("POST", "git/trees",
        {"base_tree": parent_tree_sha, "tree": overlays})
    new_tree_sha = tree_result["sha"]
    print(f"  tree  {new_tree_sha}")

    commit_msg = (
        "v3.16.9.5 PATCH + 3 file deletions:\n"
        "- ErrorCode.Ok mapping for PeakError.OK (review finding #25)\n"
        "- Delete MasterRadioConverter.cs (v3.11.6 PATCH removal)\n"
        "- Delete UdsView.xaml + UdsView.xaml.cs (v3.11.3 PATCH removal)"
    )
    commit_result = gh("POST", "git/commits",
        {"message": commit_msg, "parents": [full_parent_sha], "tree": new_tree_sha})
    new_commit_sha = commit_result["sha"]
    print(f"  commit {new_commit_sha}")

    gh("PATCH", "git/refs/heads/main", {"sha": new_commit_sha, "force": True})
    print(f"  refs/heads/main -> {new_commit_sha} (force)")

    # Tag v3.16.9.5 PATCH
    tag_v31695 = gh("POST", "git/tags", {
        "tag": "v3.16.9.5",
        "message": "v3.16.9.5 PATCH: ErrorCode.Ok mapping + cleanup 3 orphan files (MasterRadioConverter.cs, UdsView.xaml, UdsView.xaml.cs)",
        "object": new_commit_sha,
        "type": "commit",
    })
    tag_v31695_sha = tag_v31695["sha"]
    print(f"  tag    {tag_v31695_sha}  v3.16.9.5")
    existing95 = gh("GET", "git/refs/tags/v3.16.9.5")
    if existing95:
        gh("PATCH", "git/refs/tags/v3.16.9.5", {"sha": tag_v31695_sha, "force": True})
        print(f"  refs/tags/v3.16.9.5 -> {tag_v31695_sha} (force)")
    else:
        gh("POST", "git/refs", {"ref": "refs/tags/v3.16.9.5", "sha": tag_v31695_sha})
        print(f"  refs/tags/v3.16.9.5 -> {tag_v31695_sha}")

    # Create v3.16.9.5 release
    v31695_notes = (REPO_ROOT / "docs" / "release-notes-v3.16.9.5.md").read_text(encoding="utf-8")
    release_result = gh("POST", "releases", {
        "tag_name": "v3.16.9.5",
        "name": "v3.16.9.5 PATCH: ErrorCode.Ok mapping + 3 file deletions",
        "body": v31695_notes,
        "draft": False,
        "prerelease": False,
    })
    release_url = release_result.get("html_url", "")
    print(f"  release {release_url}")

    print("\n=== TIER 3 SHIP COMPLETE ===")
    print(f"  parent  : {full_parent_sha}")
    print(f"  new     : {new_commit_sha}")
    print(f"  tag v3.16.9.5 (new): {tag_v31695_sha}")
    print(f"  release v3.16.9.5: {release_url}")


if __name__ == "__main__":
    main()