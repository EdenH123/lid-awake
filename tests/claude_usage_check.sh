#!/bin/sh
# Claude usage while Claude Code is signed out (ClaudeUsageState.swift): reading "claude auth
# status", the saved state, and the words for numbers that may be old.
cd "$(dirname "$0")" || exit 1
OUT="$(mktemp -d)"
swiftc -o "$OUT/claude_usage" claude_usage/main.swift ../ClaudeUsageState.swift || { rm -rf "$OUT"; exit 1; }
"$OUT/claude_usage"; status=$?
rm -rf "$OUT"
exit $status
