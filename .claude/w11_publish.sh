#!/usr/bin/env bash
# orchestrator that reads staging files from .claude/vault-tmp/ and publishes to vault
set -e
python /d/claude_proj2/peakcan-host/.claude/w11_publish.py
