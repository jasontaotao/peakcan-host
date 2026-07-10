#!/usr/bin/env python3
"""Tier 3 re-ship for v3.14.0 MINOR — fix: include all 13 U1-U4 files.

The first v3.14.0 ship (commit 5eb960b) only included the release notes
file because the auto-discovery `git diff 1b3f5760..HEAD` returned empty
(PARENT_SHA was the v3.13.3 PATCH ship commit on origin/main, which
the local git cache doesn't have because Tier 3 ship uses gh API
force-update, never `git fetch`).

This re-ship re-creates the v3.14.0 overlay with ALL 13 files modified
by U1-U4 + release notes + ship script. Uses the LOCAL v3.13.3 PATCH
docs/ship commit `9b779e9` as the git-diff base for file discovery,
but the actual git/commits/{PARENT_SHA} call still uses the cached
`1b3f5760` on origin/main (the v3.13.3 PATCH ship commit). The
overlay tree is constructed independently of the parent's tree
content, so the diff base only affects which files are listed.

This is a one-time fix. Future ship scripts should ALWAYS use the
LOCAL base commit hash (e.g. 9b779e9 for v3.13.3) for git diff
discovery, not the GitHub-cached full SHA, to avoid this class of bug.
"""
import base64
import json
import subprocess
import sys
from pathlib import Path

REPO = "jasontaotao/peakcan-host"
PARENT_SHA = "1b3f57608f4c105523692d1a17e50d5fec96af34"  # v3.13.3 PATCH ship commit on origin/main

REPO_ROOT = Path(r"D:\claude_proj2\peakcan-host")

# Hard-coded list of all 13 files modified by U1-U4 (git-diff against
# local v3.13.3 base 9b779e9). Plus the release notes + ship script.
ADDED_OR_MODIFIED = [
    # U1 A1
    "src/PeakCan.Host.Core/Dbc/SignalDecoder.cs",
    "tests/PeakCan.Host.Core.Tests/SignalDecoderTests.cs",
    # U2 A5
    "src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs",
    "tests/PeakCan.Host.Infrastructure.Tests/ChannelRouterTests.cs",
    # U3 A2+A3+A4
    "src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs",
    "src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs",
    "tests/PeakCan.Host.App.Tests/ViewModels/EventSubscriptionLeakTests.cs",
    # U4 A6+A7
    "src/PeakCan.Host.Core/Replay/ReplayService.cs",
    "src/PeakCan.Host.Core/Replay/ReplayTimeline.cs",
    "tests/PeakCan.Host.Core.Tests/Replay/TimerAsyncWaitTests.cs",
    "tests/PeakCan.Host.Core.Tests/Replay/LoopRegionValidationTests.cs",
    # Release notes + ship script
    "docs/release-notes-v3.14.0.md",
    "scripts/tier3_v3140.py",
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

    commit_msg = "v3.14.0 MINOR: code-review bug bash — 7 HIGH fixes (A1 SignalDecoder, A2/A3/A4 Dispose leaks, A5 ChannelRouter, A6 ReplayService timer, A7 LoopRegion validation) [reship: include U1-U4 files]"
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
    print(f"  tag    {tag_sha}  v3.14.0 (reship)")

    existing = gh("GET", "git/refs/tags/v3.14.0")
    if existing:
        gh("PATCH", "git/refs/tags/v3.14.0", {"sha": tag_sha, "force": True})
        print(f"  refs/tags/v3.14.0 -> {tag_sha} (force-updated over prior v3.14.0 tag)")
    else:
        gh("POST", "git/refs", {"ref": "refs/tags/v3.14.0", "sha": tag_sha})
        print(f"  refs/tags/v3.14.0 -> {tag_sha}")

    # v3.8.8 lesson: idempotent release. Delete the prior v3.14.0 release
    # (the one that only had release notes + a wrong tree), then re-create.
    # We use `gh release delete` which requires admin scope; if it fails
    # we just create the new release anyway and the prior becomes orphaned
    # (not ideal but acceptable for this PATCH chain).
    try:
        del_res = subprocess.run(
            ["gh", "release", "delete", "v3.14.0", "--yes", "--cleanup-tag"],
            capture_output=True, text=True)
        if del_res.returncode == 0:
            print(f"  prior v3.14.0 release deleted")
    except Exception as e:
        print(f"  WARN: could not delete prior v3.14.0 release: {e}", file=sys.stderr)

    release_notes = (REPO_ROOT / "docs" / "release-notes-v3.14.0.md").read_text(encoding="utf-8")
    release_result = gh("POST", "releases",
        {"tag_name": "v3.14.0", "name": "v3.14.0 MINOR: code-review bug bash — 7 HIGH fixes",
         "body": release_notes, "draft": False, "prerelease": False})
    release_url = release_result.get("html_url", "")
    print(f"  release {release_url}")

    print("\n=== TIER 3 RESHIP COMPLETE ===")
    print(f"  parent  : {full_parent_sha}")
    print(f"  new     : {new_commit_sha}")
    print(f"  tag     : v3.14.0  ({tag_sha})")
    print(f"  release : {release_url}")


if __name__ == "__main__":
    main()